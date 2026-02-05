using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Plugin.ServerSync.Utilities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks;

/// <summary>
/// Scheduled task to sync content from the source server.
/// Downloads queued content, processes deletions, and triggers library refresh.
/// </summary>
public class DownloadMissingContentTask : IScheduledTask
{
    private readonly ILogger<DownloadMissingContentTask> _logger;
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Default maximum retry count if not configured.
    /// </summary>
    private const int DefaultMaxRetries = 3;

    /// <summary>
    /// Circuit breaker for source server failures.
    /// </summary>
    private static CircuitBreaker? _circuitBreaker;

    public DownloadMissingContentTask(ILogger<DownloadMissingContentTask> logger, ILibraryManager libraryManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
    }

    public string Name => "Sync Content";

    public string Key => "ServerSyncDownloadContent";

    public string Description => "Downloads queued content from the source server, processes deletions, and triggers a library refresh.";

    public string Category => "Content Sync";

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

        // Initialize client for all operations
        using var client = new SourceServerClient(
            plugin.LoggerFactory.CreateLogger<SourceServerClient>(),
            config.SourceServerUrl,
            config.SourceServerApiKey);

        // Initialize or get circuit breaker
        _circuitBreaker ??= new CircuitBreaker(
            _logger,
            "SourceServer",
            failureThreshold: 5,
            cooldownPeriod: TimeSpan.FromMinutes(5));

        // Check circuit breaker before attempting connection
        if (!_circuitBreaker.AllowOperation(out var circuitReason))
        {
            _logger.LogWarning("Sync skipped: {Reason}", circuitReason);
            return;
        }

        var connectionResult = await client.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (!connectionResult.Success)
        {
            _circuitBreaker.RecordFailure(connectionResult.ErrorMessage);
            _logger.LogError("Failed to connect to source server: {Message}", connectionResult.ErrorMessage);
            return;
        }

        // Connection succeeded, record success
        _circuitBreaker.RecordSuccess();

        // Cleanup stale download entries
        var staleCount = ActiveDownloadTracker.CleanupStaleEntries();
        if (staleCount > 0)
        {
            _logger.LogInformation("Cleaned up {Count} stale download entries", staleCount);
        }

        var maxRetries = config.MaxRetryCount > 0 ? config.MaxRetryCount : DefaultMaxRetries;
        var itemsToSync = database.GetByStatus(SyncStatus.Queued)
            .Concat(database.GetErroredItemsForRetry(maxRetries: maxRetries))
            .ToList();

        if (itemsToSync.Count == 0)
        {
            // Still process deletions and library refresh even if no items to download
            var (deleted, _) = FileDeletionService.ProcessPendingDeletions(database, config, _logger, cancellationToken);

            if (deleted > 0)
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

            progress.Report(100);
            return;
        }

        config.LastSyncStartTime = DateTime.UtcNow;
        plugin.SaveConfiguration();

        var totalBytes = itemsToSync.Sum(i => i.SourceSize);
        _logger.LogInformation(
            "Starting download of {Count} items ({TotalSize})",
            itemsToSync.Count,
            FormatUtilities.FormatBytes(totalBytes));

        var tempPath = plugin.GetTempDownloadPath();
        Directory.CreateDirectory(tempPath);

        var downloadService = new DownloadService(_logger);

        // Download content (0-90% progress)
        var (successCount, failCount) = await ProcessDownloadsAsync(
            client,
            database,
            downloadService,
            itemsToSync,
            tempPath,
            config.MaxConcurrentDownloads,
            config.GetEffectiveDownloadSpeedBytes(),
            config,
            new Progress<double>(p => progress.Report(p * 0.9)),
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

        // Process deletions and library refresh (90-100% progress)
        progress.Report(90);
        var (deletedCount, _) = FileDeletionService.ProcessPendingDeletions(database, config, _logger, cancellationToken);

        if (successCount > 0 || deletedCount > 0)
        {
            try
            {
                _logger.LogInformation("Triggering library refresh");
                await _libraryManager.ValidateMediaLibrary(new Progress<double>(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to trigger library refresh");
            }
        }

        progress.Report(100);
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

            // Generate sanitized temp file path for this item
            var tempFileName = FileNameSanitizer.SanitizeTempFileName(item.SourceItemId, item.LocalPath);
            var tempFilePath = Path.Combine(tempPath, tempFileName);

            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                // Register download with the centralized tracker
                if (!ActiveDownloadTracker.TryStartDownload(item.SourceItemId, tempFilePath))
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
                ActiveDownloadTracker.CompleteDownload(item.SourceItemId);
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
        // Guard against null LocalPath
        if (string.IsNullOrEmpty(item.LocalPath))
        {
            var errorMsg = "Item has no local path configured";
            database.UpdateStatus(item.SourceItemId, SyncStatus.Errored, errorMessage: errorMsg);
            _logger.LogError("FAILED: {SourceItemId} - {Error}", item.SourceItemId, errorMsg);
            return new DownloadResult(false, errorMsg);
        }

        var fileName = Path.GetFileName(item.LocalPath);
        var fileSize = FormatUtilities.FormatBytes(item.SourceSize);

        // Per-file disk space check before download
        if (!DiskSpaceService.HasSufficientSpaceForFile(item.LocalPath, item.SourceSize, config.MinimumFreeDiskSpaceGb))
        {
            var errorMsg = $"Insufficient disk space for {fileName} ({fileSize}). " +
                           $"Required: {fileSize} + {config.MinimumFreeDiskSpaceGb} GB reserve";
            database.UpdateStatus(item.SourceItemId, SyncStatus.Errored, errorMessage: errorMsg);
            _logger.LogError("DISK FULL: {FileName} ({Size}) - Not enough space on target drive. " +
                            "Stopping download to prevent disk exhaustion.", fileName, fileSize);
            return new DownloadResult(false, errorMsg);
        }

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
            _circuitBreaker?.RecordSuccess();
            database.UpdateStatus(item.SourceItemId, SyncStatus.Synced,
                localPath: item.LocalPath, sourceETag: item.SourceETag, sourceSize: item.SourceSize,
                companionFiles: result.CompanionFiles);
            _logger.LogInformation("DOWNLOADED: {FileName} ({Size}) -> {LocalPath}", fileName, fileSize, item.LocalPath);
        }
        else
        {
            _circuitBreaker?.RecordFailure(result.ErrorMessage);
            database.UpdateStatus(item.SourceItemId, SyncStatus.Errored, errorMessage: result.ErrorMessage);
            _logger.LogError("FAILED: {FileName} ({Size}) - {Error}. Source: {SourcePath}",
                fileName, fileSize, result.ErrorMessage, item.SourcePath);
        }

        return result;
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
