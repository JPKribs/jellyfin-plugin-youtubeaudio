using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
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

        // Validate authentication configuration
        if (!HasValidAuthConfiguration(config))
        {
            _logger.LogError("Sync skipped: no valid authentication configured");
            return;
        }

        // Validate disk space before starting
        if (!HasSufficientDiskSpace(config, out var diskSpaceMessage))
        {
            _logger.LogError("Sync skipped: {Message}", diskSpaceMessage);
            return;
        }

        var database = plugin.Database;

        var itemsToSync = database.GetByStatus(SyncStatus.Queued)
            .Concat(database.GetErroredItemsForRetry(maxRetries: 3))
            .ToList();

        if (itemsToSync.Count == 0)
        {
            return;
        }

        // Update sync timestamps
        config.LastSyncStartTime = DateTime.UtcNow;
        plugin.SaveConfiguration();

        var totalBytes = itemsToSync.Sum(i => i.SourceSize);
        _logger.LogInformation(
            "Starting download of {Count} items ({TotalSize})",
            itemsToSync.Count,
            FormatBytes(totalBytes));

        using var client = CreateSourceServerClient(config, plugin);

        var connectionResult = await client.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (!connectionResult.Success)
        {
            _logger.LogError("Failed to connect to source server: {Message}", connectionResult.ErrorMessage);
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

        var speedLimitBytesPerSecond = config.GetEffectiveDownloadSpeedBytes();

        var downloadTasks = itemsToSync.Select(async item =>
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var fileName = Path.GetFileName(item.LocalPath);
                var fileSize = FormatBytes(item.SourceSize);

                // Validate path before downloading
                var pathValidation = ValidatePath(item, config, database);
                if (!pathValidation.IsValid)
                {
                    _logger.LogWarning(
                        "SKIPPED: {FileName} ({Size}) - {Reason}. Source: {SourcePath}",
                        fileName,
                        fileSize,
                        pathValidation.Message,
                        item.SourcePath);
                    database.UpdateStatus(item.SourceItemId, SyncStatus.Errored, errorMessage: pathValidation.Message);
                    Interlocked.Increment(ref failCount);
                    return;
                }

                // Check disk space before each download
                if (!HasSufficientDiskSpaceForItem(item, config))
                {
                    _logger.LogWarning(
                        "SKIPPED: {FileName} ({Size}) - Insufficient disk space. Target: {LocalPath}",
                        fileName,
                        fileSize,
                        item.LocalPath);
                    database.UpdateStatus(item.SourceItemId, SyncStatus.Errored, errorMessage: "Insufficient disk space");
                    Interlocked.Increment(ref failCount);
                    return;
                }

                // Check write permissions
                if (!HasWritePermission(item.LocalPath))
                {
                    _logger.LogWarning(
                        "SKIPPED: {FileName} ({Size}) - No write permission to target directory. Target: {LocalPath}",
                        fileName,
                        fileSize,
                        item.LocalPath);
                    database.UpdateStatus(item.SourceItemId, SyncStatus.Errored, errorMessage: "No write permission to target directory");
                    Interlocked.Increment(ref failCount);
                    return;
                }

                // Pre-download validation: Check if local file already exists with matching size
                // This prevents unnecessary downloads and file overwrites
                if (ShouldSkipDownload(item, database, out var skipReason))
                {
                    _logger.LogInformation(
                        "SKIPPED: {FileName} ({Size}) - {Reason}",
                        fileName,
                        fileSize,
                        skipReason);
                    Interlocked.Increment(ref successCount);
                    return;
                }

                var (success, errorMessage) = await DownloadItemAsync(
                    client,
                    item,
                    tempPath,
                    speedLimitBytesPerSecond,
                    config.IncludeCompanionFiles,
                    config,
                    cancellationToken).ConfigureAwait(false);

                if (success)
                {
                    // Pass ETag and size to ensure they're preserved after sync
                    // Note: LocalItemId is resolved at delete time via FindByPath
                    database.UpdateStatus(
                        item.SourceItemId,
                        SyncStatus.Synced,
                        localPath: item.LocalPath,
                        sourceETag: item.SourceETag,
                        sourceSize: item.SourceSize);
                    Interlocked.Increment(ref successCount);
                    _logger.LogInformation(
                        "DOWNLOADED: {FileName} ({Size}) -> {LocalPath}",
                        fileName,
                        fileSize,
                        item.LocalPath);
                }
                else
                {
                    database.UpdateStatus(item.SourceItemId, SyncStatus.Errored, errorMessage: errorMessage);
                    Interlocked.Increment(ref failCount);
                    _logger.LogError(
                        "FAILED: {FileName} ({Size}) - {Error}. Source: {SourcePath}",
                        fileName,
                        fileSize,
                        errorMessage,
                        item.SourcePath);
                }

                var processed = Interlocked.Increment(ref processedItems);
                progress.Report((double)processed / totalItems * 100);
            }
            finally
            {
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
            // Log but don't throw - some downloads may have succeeded
            _logger.LogError(ex, "Error during parallel download execution");
        }

        // Update sync timestamps
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
            totalItems);

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

    // CreateSourceServerClient
    // Creates a source server client using API key authentication.
    private static SourceServerClient CreateSourceServerClient(PluginConfiguration config, Plugin plugin)
    {
        return new SourceServerClient(
            plugin.LoggerFactory.CreateLogger<SourceServerClient>(),
            config.SourceServerUrl,
            config.SourceServerApiKey);
    }

    // HasValidAuthConfiguration
    // Checks if valid authentication is configured.
    private static bool HasValidAuthConfiguration(PluginConfiguration config)
    {
        return !string.IsNullOrWhiteSpace(config.SourceServerUrl) &&
               !string.IsNullOrWhiteSpace(config.SourceServerApiKey);
    }

    // HasSufficientDiskSpace
    // Checks if there's sufficient disk space across all library paths.
    private bool HasSufficientDiskSpace(PluginConfiguration config, out string message)
    {
        message = string.Empty;
        var requiredBytes = (long)config.MinimumFreeDiskSpaceGb * 1024 * 1024 * 1024;

        foreach (var mapping in config.LibraryMappings.Where(m => m.IsEnabled && !string.IsNullOrEmpty(m.LocalRootPath)))
        {
            try
            {
                var driveInfo = new DriveInfo(Path.GetPathRoot(mapping.LocalRootPath) ?? mapping.LocalRootPath);
                if (driveInfo.AvailableFreeSpace < requiredBytes)
                {
                    message = $"Insufficient disk space on {mapping.LocalRootPath}: {FormatBytes(driveInfo.AvailableFreeSpace)} free, {config.MinimumFreeDiskSpaceGb} GB required";
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to check disk space for {Path}", mapping.LocalRootPath);
            }
        }

        return true;
    }

    // HasSufficientDiskSpaceForItem
    // Checks if there's sufficient disk space for a specific item.
    private static bool HasSufficientDiskSpaceForItem(SyncItem item, PluginConfiguration config)
    {
        if (string.IsNullOrEmpty(item.LocalPath) || item.SourceSize <= 0)
        {
            return true;
        }

        try
        {
            var pathRoot = Path.GetPathRoot(item.LocalPath);
            if (string.IsNullOrEmpty(pathRoot))
            {
                return true;
            }

            var driveInfo = new DriveInfo(pathRoot);
            var requiredBytes = (long)config.MinimumFreeDiskSpaceGb * 1024 * 1024 * 1024;

            // Need enough space for the file plus the minimum reserve
            return driveInfo.AvailableFreeSpace >= item.SourceSize + requiredBytes;
        }
        catch
        {
            return true;
        }
    }

    // HasWritePermission
    // Checks if write permission exists for the target directory.
    private static bool HasWritePermission(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directory))
        {
            return false;
        }

        try
        {
            // Try to create the directory if it doesn't exist
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                return true;
            }

            // Try to create a temp file to test write permission
            var testFile = Path.Combine(directory, $".write_test_{Guid.NewGuid():N}");
            try
            {
                using (File.Create(testFile, 1, FileOptions.DeleteOnClose))
                {
                    return true;
                }
            }
            finally
            {
                if (File.Exists(testFile))
                {
                    try
                    {
                        File.Delete(testFile);
                    }
                    catch
                    {
                        // Ignore cleanup errors
                    }
                }
            }
        }
        catch
        {
            return false;
        }
    }

    // ShouldSkipDownload
    // Pre-download validation to check if file already exists and is valid.
    // Returns true if download should be skipped (file is already valid).
    private bool ShouldSkipDownload(SyncItem item, SyncDatabase database, out string skipReason)
    {
        skipReason = string.Empty;

        if (string.IsNullOrEmpty(item.LocalPath))
        {
            return false;
        }

        // Check if local file exists
        if (!File.Exists(item.LocalPath))
        {
            return false;
        }

        try
        {
            var localInfo = new FileInfo(item.LocalPath);

            // If sizes match, the file is likely valid - mark as synced and skip download
            if (item.SourceSize > 0 && localInfo.Length == item.SourceSize)
            {
                // Update status to Synced since file is already valid, preserve ETag
                // Note: LocalItemId is resolved at delete time via FindByPath
                database.UpdateStatus(
                    item.SourceItemId,
                    SyncStatus.Synced,
                    localPath: item.LocalPath,
                    sourceETag: item.SourceETag,
                    sourceSize: item.SourceSize);
                skipReason = $"Local file already exists with matching size ({FormatBytes(localInfo.Length)})";
                return true;
            }

            // Size mismatch - needs re-download, don't skip
            _logger.LogDebug(
                "Local file exists but size differs: local={LocalSize}, source={SourceSize} for {FileName}",
                localInfo.Length,
                item.SourceSize,
                Path.GetFileName(item.LocalPath));
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check local file {LocalPath}", item.LocalPath);
            return false;
        }
    }

    // ValidatePath
    // Validates the local path for an item.
    private static (bool IsValid, string? Message) ValidatePath(SyncItem item, PluginConfiguration config, SyncDatabase database)
    {
        if (string.IsNullOrEmpty(item.LocalPath))
        {
            return (false, "No local path configured");
        }

        // Check for path traversal
        var normalizedPath = Path.GetFullPath(item.LocalPath);
        if (!normalizedPath.Equals(item.LocalPath, StringComparison.OrdinalIgnoreCase))
        {
            // Path was normalized, check if it's still within allowed boundaries
            var isWithinLibrary = config.LibraryMappings
                .Where(m => m.IsEnabled && !string.IsNullOrEmpty(m.LocalRootPath))
                .Any(m => normalizedPath.StartsWith(m.LocalRootPath, StringComparison.OrdinalIgnoreCase));

            if (!isWithinLibrary)
            {
                return (false, "Path traversal detected - path escapes library boundaries");
            }
        }

        // Check for path collision
        if (database.CheckPathCollision(item.LocalPath, item.SourceItemId))
        {
            return (false, "Path collision - another item is already using this path");
        }

        return (true, null);
    }

    // DownloadItemAsync
    // Downloads a single item and its companion files from the source server to the local path.
    private async Task<(bool Success, string? ErrorMessage)> DownloadItemAsync(
        SourceServerClient client,
        SyncItem item,
        string tempPath,
        long speedLimitBytesPerSecond,
        bool includeCompanionFiles,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(item.LocalPath))
        {
            return (false, "No local path configured");
        }

        var tempFileName = $"{item.SourceItemId}_{Path.GetFileName(item.LocalPath)}";
        var tempFilePath = Path.Combine(tempPath, tempFileName);
        var itemId = Guid.Parse(item.SourceItemId);

        try
        {
            using var sourceStream = await client.DownloadFileAsync(itemId, cancellationToken).ConfigureAwait(false);

            if (sourceStream == null)
            {
                return (false, "No response from server");
            }

            using (var fileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await CopyWithSpeedLimitAsync(sourceStream, fileStream, speedLimitBytesPerSecond, cancellationToken).ConfigureAwait(false);
            }

            var downloadedInfo = new FileInfo(tempFilePath);
            if (item.SourceSize > 0 && downloadedInfo.Length != item.SourceSize)
            {
                var errorMsg = $"Size mismatch: expected {FormatBytes(item.SourceSize)}, got {FormatBytes(downloadedInfo.Length)}";
                File.Delete(tempFilePath);
                return (false, errorMsg);
            }

            var targetDir = Path.GetDirectoryName(item.LocalPath);
            if (!string.IsNullOrEmpty(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            // If file exists and recycling bin is enabled, move old file to bin before replacing
            if (File.Exists(item.LocalPath) && config.EnableRecyclingBin && !string.IsNullOrEmpty(config.RecyclingBinPath))
            {
                RecyclingBinService.MoveWithCompanionsToRecyclingBin(item.LocalPath, config.RecyclingBinPath, _logger);
            }

            // Use atomic move with overwrite to avoid race conditions
            // This is safer than delete-then-move as it's a single operation
            MoveFileWithOverwrite(tempFilePath, item.LocalPath);

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

            return (true, null);
        }
        catch (Exception ex)
        {
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

            return (false, ex.Message);
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

                    // Use atomic move with overwrite
                    MoveFileWithOverwrite(tempFilePath, targetPath);
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

    // MoveFileWithOverwrite
    // Moves a file with atomic overwrite semantics where possible.
    // Falls back to delete-then-move with retry on failure.
    private void MoveFileWithOverwrite(string sourcePath, string destinationPath)
    {
        const int maxRetries = 3;
        const int retryDelayMs = 100;

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                // .NET 6+ File.Move supports overwrite parameter
                File.Move(sourcePath, destinationPath, overwrite: true);
                return;
            }
            catch (IOException ex) when (attempt < maxRetries)
            {
                _logger.LogWarning(ex, "File move attempt {Attempt}/{MaxRetries} failed for {Destination}, retrying",
                    attempt, maxRetries, Path.GetFileName(destinationPath));
                Thread.Sleep(retryDelayMs * attempt);
            }
        }

        // Final attempt - if this fails, let the exception propagate
        File.Move(sourcePath, destinationPath, overwrite: true);
    }

    // FormatBytes
    // Formats bytes to human-readable string.
    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        var unitIndex = 0;

        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size:F2} {units[unitIndex]}";
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
