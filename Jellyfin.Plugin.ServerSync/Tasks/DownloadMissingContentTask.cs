using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Utilities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks;

/// <summary>
/// Scheduled task to download missing content from the source server.
/// </summary>
public class DownloadMissingContentTask : IScheduledTask
{
    private readonly ILogger<DownloadMissingContentTask> _logger;
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Tracks items currently being downloaded to prevent duplicate downloads.
    /// </summary>
    private static readonly ConcurrentDictionary<string, DateTime> ActiveDownloads = new();

    private const int StaleDownloadHours = 1;
    private const int MaxRetries = 3;

    public DownloadMissingContentTask(ILogger<DownloadMissingContentTask> logger, ILibraryManager libraryManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
    }

    public string Name => "Sync Missing Content";

    public string Key => "ServerSyncDownloadContent";

    public string Description => "Downloads queued content from the source server.";

    public string Category => "Server Sync";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return;
        }

        var config = plugin.Configuration;

        if (!config.EnableContentSync)
        {
            return;
        }

        if (!ConfigurationUtilities.HasValidAuthConfiguration(config))
        {
            _logger.LogError("Sync skipped: no valid authentication configured");
            return;
        }

        if (!DiskSpaceService.HasSufficientSpace(config, out var insufficientPath))
        {
            var diskInfo = DiskSpaceService.GetDiskSpaceInfo(config).FirstOrDefault(d => d.Path == insufficientPath);
            var message = diskInfo != null
                ? DiskSpaceService.FormatInsufficientSpaceMessage(insufficientPath!, diskInfo.FreeBytes, config.MinimumFreeDiskSpaceGb)
                : $"Insufficient disk space on {insufficientPath}";
            _logger.LogError("Sync skipped: {Message}", message);
            return;
        }

        var database = plugin.Database;

        CleanupStaleDownloadEntries();

        var itemsToSync = database.GetByStatus(SyncStatus.Queued)
            .Concat(database.GetErroredItemsForRetry(maxRetries: MaxRetries))
            .ToList();

        if (itemsToSync.Count == 0)
        {
            return;
        }

        config.LastSyncStartTime = DateTime.UtcNow;
        plugin.SaveConfiguration();

        var totalBytes = itemsToSync.Sum(i => i.SourceSize);
        _logger.LogInformation(
            "Starting download of {Count} items ({TotalSize})",
            itemsToSync.Count,
            FormatUtilities.FormatBytes(totalBytes));

        using var client = new SourceServerClient(
            plugin.LoggerFactory.CreateLogger<SourceServerClient>(),
            config.SourceServerUrl,
            config.SourceServerApiKey);

        var connectionResult = await client.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (!connectionResult.Success)
        {
            _logger.LogError("Failed to connect to source server: {Message}", connectionResult.ErrorMessage);
            return;
        }

        var tempPath = plugin.GetTempDownloadPath();
        Directory.CreateDirectory(tempPath);

        var downloadService = new DownloadService(_logger);
        var (successCount, failCount) = await ProcessDownloadsAsync(
            client,
            database,
            downloadService,
            itemsToSync,
            tempPath,
            config.MaxConcurrentDownloads,
            config.GetEffectiveDownloadSpeedBytes(),
            config,
            progress,
            cancellationToken).ConfigureAwait(false);

        config.LastSyncEndTime = DateTime.UtcNow;
        try
        {
            plugin.SaveConfiguration();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save sync end time");
        }

        _logger.LogInformation(
            "Download task complete: {Success} succeeded, {Failed} failed out of {Total} items",
            successCount,
            failCount,
            itemsToSync.Count);

        var (deleted, _) = FileDeletionService.ProcessPendingDeletions(database, config, _logger, cancellationToken);

        if (successCount > 0 || deleted > 0)
        {
            try
            {
                await _libraryManager.ValidateMediaLibrary(new Progress<double>(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to trigger library refresh");
            }
        }
    }

    private async Task<(int SuccessCount, int FailCount)> ProcessDownloadsAsync(
        SourceServerClient client,
        SyncDatabase database,
        DownloadService downloadService,
        List<SyncItem> itemsToSync,
        string tempPath,
        int maxConcurrent,
        long speedLimitBytesPerSecond,
        PluginConfiguration config,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var totalItems = itemsToSync.Count;
        var processedItems = 0;
        var successCount = 0;
        var failCount = 0;

        using var semaphore = new SemaphoreSlim(Math.Max(1, maxConcurrent));

        var downloadTasks = itemsToSync.Select(async item =>
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                if (!ActiveDownloads.TryAdd(item.SourceItemId, DateTime.UtcNow))
                {
                    _logger.LogDebug("Item {SourceItemId} is already being downloaded, skipping", item.SourceItemId);
                    return;
                }

                var result = await ProcessSingleDownloadAsync(
                    client, database, downloadService, item, tempPath,
                    speedLimitBytesPerSecond, config, cancellationToken).ConfigureAwait(false);

                if (result.Success)
                {
                    Interlocked.Increment(ref successCount);
                }
                else
                {
                    Interlocked.Increment(ref failCount);
                }

                var processed = Interlocked.Increment(ref processedItems);
                progress.Report((double)processed / totalItems * 100);
            }
            finally
            {
                ActiveDownloads.TryRemove(item.SourceItemId, out _);
                semaphore.Release();
            }
        });

        try
        {
            await Task.WhenAll(downloadTasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Download task was cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during parallel download execution");
        }

        return (successCount, failCount);
    }

    private async Task<DownloadResult> ProcessSingleDownloadAsync(
        SourceServerClient client,
        SyncDatabase database,
        DownloadService downloadService,
        SyncItem item,
        string tempPath,
        long speedLimitBytesPerSecond,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(item.LocalPath);
        var fileSize = FormatUtilities.FormatBytes(item.SourceSize);

        var (isValid, validationError) = DownloadService.ValidateForDownload(item, config, database);
        if (!isValid)
        {
            _logger.LogWarning("SKIPPED: {FileName} ({Size}) - {Reason}. Source: {SourcePath}",
                fileName, fileSize, validationError, item.SourcePath);
            database.UpdateStatus(item.SourceItemId, SyncStatus.Errored, errorMessage: validationError);
            return new DownloadResult(false, validationError);
        }

        if (DownloadService.ShouldSkipDownload(item, out var skipReason))
        {
            database.UpdateStatus(item.SourceItemId, SyncStatus.Synced,
                localPath: item.LocalPath, sourceETag: item.SourceETag, sourceSize: item.SourceSize);
            _logger.LogInformation("SKIPPED: {FileName} ({Size}) - {Reason}", fileName, fileSize, skipReason);
            return new DownloadResult(true);
        }

        var result = await downloadService.DownloadItemAsync(
            client, item, tempPath, speedLimitBytesPerSecond,
            config.IncludeCompanionFiles, config, cancellationToken).ConfigureAwait(false);

        if (result.Success)
        {
            database.UpdateStatus(item.SourceItemId, SyncStatus.Synced,
                localPath: item.LocalPath, sourceETag: item.SourceETag, sourceSize: item.SourceSize);
            _logger.LogInformation("DOWNLOADED: {FileName} ({Size}) -> {LocalPath}", fileName, fileSize, item.LocalPath);
        }
        else
        {
            database.UpdateStatus(item.SourceItemId, SyncStatus.Errored, errorMessage: result.ErrorMessage);
            _logger.LogError("FAILED: {FileName} ({Size}) - {Error}. Source: {SourcePath}",
                fileName, fileSize, result.ErrorMessage, item.SourcePath);
        }

        return result;
    }

    private void CleanupStaleDownloadEntries()
    {
        var staleThreshold = DateTime.UtcNow.AddHours(-StaleDownloadHours);
        var staleEntries = ActiveDownloads
            .Where(kvp => kvp.Value < staleThreshold)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in staleEntries)
        {
            if (ActiveDownloads.TryRemove(key, out _))
            {
                _logger.LogDebug("Removed stale download entry for {SourceItemId}", key);
            }
        }

        if (staleEntries.Count > 0)
        {
            _logger.LogInformation("Cleaned up {Count} stale download entries", staleEntries.Count);
        }
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(12).Ticks
            }
        };
    }
}
