using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models;
using Jellyfin.Plugin.ServerSync.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks;

// DownloadMissingContentTask
// Scheduled task to download missing content from the source server.
public class DownloadMissingContentTask : IScheduledTask
{
    private readonly ILogger<DownloadMissingContentTask> _logger;
    private readonly ILibraryManager _libraryManager;

    public DownloadMissingContentTask(ILogger<DownloadMissingContentTask> logger, ILibraryManager libraryManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
    }

    public string Name => "Download Missing Content";

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

        if (string.IsNullOrWhiteSpace(config.SourceServerUrl) || string.IsNullOrWhiteSpace(config.SourceServerApiKey))
        {
            return;
        }

        var database = plugin.Database;

        var itemsToSync = database.GetByStatus(SyncStatus.Queued)
            .Concat(database.GetByStatus(SyncStatus.Errored))
            .ToList();

        if (itemsToSync.Count == 0)
        {
            return;
        }

        _logger.LogInformation("Starting download of {Count} items", itemsToSync.Count);

        using var client = new SourceServerClient(
            plugin.LoggerFactory.CreateLogger<SourceServerClient>(),
            config.SourceServerUrl,
            config.SourceServerApiKey);

        var connectionResult = await client.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (connectionResult.ServerName == null)
        {
            _logger.LogError("Failed to connect to source server at {SourceUrl}", config.SourceServerUrl);
            return;
        }

        var tempPath = plugin.GetTempDownloadPath();
        Directory.CreateDirectory(tempPath);

        var totalItems = itemsToSync.Count;
        var processedItems = 0;
        var successCount = 0;
        var failCount = 0;

        var maxConcurrent = Math.Max(1, config.MaxConcurrentDownloads);
        using var semaphore = new SemaphoreSlim(maxConcurrent);

        var speedLimitBytesPerSecond = config.MaxDownloadSpeedMbps > 0
            ? config.MaxDownloadSpeedMbps * 1024L * 1024L
            : 0L;

        var downloadTasks = itemsToSync.Select(async item =>
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var success = await DownloadItemAsync(
                    client,
                    item,
                    tempPath,
                    speedLimitBytesPerSecond,
                    config.IncludeCompanionFiles,
                    cancellationToken).ConfigureAwait(false);

                if (success)
                {
                    database.UpdateStatus(item.SourceItemId, SyncStatus.Synced, localPath: item.LocalPath);
                    Interlocked.Increment(ref successCount);
                    _logger.LogInformation("Downloaded {FileName}", Path.GetFileName(item.LocalPath));
                }
                else
                {
                    database.UpdateStatus(item.SourceItemId, SyncStatus.Errored);
                    Interlocked.Increment(ref failCount);
                }

                var processed = Interlocked.Increment(ref processedItems);
                progress.Report((double)processed / totalItems * 100);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(downloadTasks).ConfigureAwait(false);

        _logger.LogInformation("Download complete: {Success} succeeded, {Failed} failed", successCount, failCount);

        if (successCount > 0)
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

    // DownloadItemAsync
    // Downloads a single item and its companion files from the source server to the local path.
    private async Task<bool> DownloadItemAsync(
        SourceServerClient client,
        SyncItem item,
        string tempPath,
        long speedLimitBytesPerSecond,
        bool includeCompanionFiles,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(item.LocalPath))
        {
            _logger.LogWarning("Item {SourceItemId} has no local path configured", item.SourceItemId);
            return false;
        }

        var tempFileName = $"{item.SourceItemId}_{Path.GetFileName(item.LocalPath)}";
        var tempFilePath = Path.Combine(tempPath, tempFileName);
        var itemId = Guid.Parse(item.SourceItemId);

        try
        {
            using var sourceStream = await client.DownloadFileAsync(itemId, cancellationToken).ConfigureAwait(false);

            if (sourceStream == null)
            {
                _logger.LogError("Failed to download {FileName}: no response from server", Path.GetFileName(item.LocalPath));
                return false;
            }

            using (var fileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await CopyWithSpeedLimitAsync(sourceStream, fileStream, speedLimitBytesPerSecond, cancellationToken).ConfigureAwait(false);
            }

            var downloadedInfo = new FileInfo(tempFilePath);
            if (item.SourceSize > 0 && downloadedInfo.Length != item.SourceSize)
            {
                _logger.LogError(
                    "Download failed for {FileName}: size mismatch (expected {Expected}, got {Actual})",
                    Path.GetFileName(item.LocalPath),
                    item.SourceSize,
                    downloadedInfo.Length);
                File.Delete(tempFilePath);
                return false;
            }

            var targetDir = Path.GetDirectoryName(item.LocalPath);
            if (!string.IsNullOrEmpty(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            if (File.Exists(item.LocalPath))
            {
                File.Delete(item.LocalPath);
            }

            File.Move(tempFilePath, item.LocalPath);

            if (includeCompanionFiles)
            {
                await DownloadCompanionFilesAsync(
                    client,
                    itemId,
                    targetDir ?? Path.GetDirectoryName(item.LocalPath) ?? string.Empty,
                    tempPath,
                    speedLimitBytesPerSecond,
                    cancellationToken).ConfigureAwait(false);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Download failed for {FileName}", Path.GetFileName(item.LocalPath));

            if (File.Exists(tempFilePath))
            {
                try
                {
                    File.Delete(tempFilePath);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }

            return false;
        }
    }

    // DownloadCompanionFilesAsync
    // Downloads external companion files (subtitles, etc.) for an item.
    private async Task DownloadCompanionFilesAsync(
        SourceServerClient client,
        Guid itemId,
        string targetDir,
        string tempPath,
        long speedLimitBytesPerSecond,
        CancellationToken cancellationToken)
    {
        try
        {
            var companions = await client.GetCompanionFilesAsync(itemId, cancellationToken).ConfigureAwait(false);

            if (companions.Count == 0)
            {
                return;
            }

            _logger.LogInformation("Downloading {Count} companion files for item {ItemId}", companions.Count, itemId);

            foreach (var companion in companions)
            {
                try
                {
                    var tempFileName = $"{itemId}_{companion.FileName}";
                    var tempFilePath = Path.Combine(tempPath, tempFileName);
                    var targetPath = Path.Combine(targetDir, companion.FileName);

                    using var stream = await client.DownloadCompanionFileAsync(itemId, companion.SourcePath, cancellationToken).ConfigureAwait(false);

                    if (stream == null)
                    {
                        _logger.LogWarning("Failed to download companion file {FileName}", companion.FileName);
                        continue;
                    }

                    using (var fileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await CopyWithSpeedLimitAsync(stream, fileStream, speedLimitBytesPerSecond, cancellationToken).ConfigureAwait(false);
                    }

                    if (File.Exists(targetPath))
                    {
                        File.Delete(targetPath);
                    }

                    File.Move(tempFilePath, targetPath);
                    _logger.LogInformation("Downloaded companion file {FileName}", companion.FileName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to download companion file {FileName}", companion.FileName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get companion files for item {ItemId}", itemId);
        }
    }

    // CopyWithSpeedLimitAsync
    // Copies stream with optional speed limiting.
    private static async Task CopyWithSpeedLimitAsync(
        Stream source,
        Stream destination,
        long bytesPerSecond,
        CancellationToken cancellationToken)
    {
        if (bytesPerSecond <= 0)
        {
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            return;
        }

        const int bufferSize = 81920;
        var buffer = new byte[bufferSize];
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        long totalBytesRead = 0;

        int bytesRead;
        while ((bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
            totalBytesRead += bytesRead;

            var expectedTime = (double)totalBytesRead / bytesPerSecond * 1000;
            var actualTime = stopwatch.ElapsedMilliseconds;

            if (actualTime < expectedTime)
            {
                var delay = (int)(expectedTime - actualTime);
                if (delay > 10)
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
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
