using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models;
using Jellyfin.Plugin.ServerSync.Services;
using Jellyfin.Sdk.Generated.Models;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks;

// UpdateSyncTablesTask
// Scheduled task to update sync tables from the source server.
public class UpdateSyncTablesTask : IScheduledTask
{
    private readonly ILogger<UpdateSyncTablesTask> _logger;
    private static readonly char[] PathSeparators = { '/', '\\' };

    public UpdateSyncTablesTask(ILogger<UpdateSyncTablesTask> logger)
    {
        _logger = logger;
    }

    public string Name => "Refresh Sync Table";

    public string Key => "ServerSyncUpdateTables";

    public string Description => "Fetches item list from source server and updates the sync tracking table.";

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

        if (string.IsNullOrWhiteSpace(config.SourceServerUrl))
        {
            _logger.LogWarning("Sync table update skipped: source server URL not configured");
            return;
        }

        if (string.IsNullOrWhiteSpace(config.SourceServerApiKey))
        {
            _logger.LogWarning("Sync table update skipped: API key not configured");
            return;
        }

        var enabledMappings = config.LibraryMappings?.Where(m => m.IsEnabled).ToList() ?? new List<LibraryMapping>();
        if (enabledMappings.Count == 0)
        {
            _logger.LogDebug("Sync table update skipped: no enabled library mappings");
            return;
        }

        _logger.LogInformation("Starting sync table update from {SourceUrl}", config.SourceServerUrl);

        using var client = new SourceServerClient(
            plugin.LoggerFactory.CreateLogger<SourceServerClient>(),
            config.SourceServerUrl,
            config.SourceServerApiKey);

        var connectionResult = await client.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (!connectionResult.Success)
        {
            _logger.LogError("Failed to connect to source server at {SourceUrl}: {Error}",
                config.SourceServerUrl, connectionResult.ErrorMessage ?? "Unknown error");
            return;
        }

        var database = plugin.Database;
        var totalMappings = enabledMappings.Count;
        var processedMappings = 0;

        var downloadNewMode = config.DownloadNewContentMode;
        var replaceExistingMode = config.ReplaceExistingContentMode;
        var deleteMissingMode = config.DeleteMissingContentMode;
        var detectUpdatedFiles = config.DetectUpdatedFiles;

        foreach (var mapping in enabledMappings)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            await ProcessLibraryAsync(client, database, mapping, downloadNewMode, replaceExistingMode, deleteMissingMode, detectUpdatedFiles, cancellationToken).ConfigureAwait(false);

            processedMappings++;
            progress.Report((double)processedMappings / totalMappings * 100);
        }

        _logger.LogInformation("Sync table update completed");
    }

    // ProcessLibraryAsync
    // Processes a single library mapping, fetching items and updating the sync database.
    private async Task ProcessLibraryAsync(
        SourceServerClient client,
        SyncDatabase database,
        LibraryMapping mapping,
        ApprovalMode downloadNewMode,
        ApprovalMode replaceExistingMode,
        ApprovalMode deleteMissingMode,
        bool detectUpdatedFiles,
        CancellationToken cancellationToken)
    {
        const int batchSize = 100;
        const int maxConsecutiveErrors = 3;
        var startIndex = 0;
        var processedItems = 0;
        var consecutiveErrors = 0;

        Dictionary<string, SyncItem> existingItems;
        try
        {
            existingItems = database.GetBySourceLibrary(mapping.SourceLibraryId)
                .ToDictionary(i => i.SourceItemId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load existing items for library {LibraryName}", mapping.SourceLibraryName);
            return;
        }

        // Track which items we've seen from the source
        var seenSourceItemIds = new HashSet<string>();

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            BaseItemDtoQueryResult? result;
            try
            {
                result = await client.GetLibraryItemsAsync(
                    Guid.Parse(mapping.SourceLibraryId),
                    startIndex,
                    batchSize,
                    cancellationToken).ConfigureAwait(false);

                // Reset consecutive errors on success
                consecutiveErrors = 0;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveErrors++;
                _logger.LogWarning(ex, "Failed to fetch items from library {LibraryName} at index {Index} (attempt {Attempt}/{Max})",
                    mapping.SourceLibraryName, startIndex, consecutiveErrors, maxConsecutiveErrors);

                if (consecutiveErrors >= maxConsecutiveErrors)
                {
                    _logger.LogError("Too many consecutive errors fetching from {LibraryName}, stopping sync for this library",
                        mapping.SourceLibraryName);
                    break;
                }

                // Wait before retry
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(consecutiveErrors * 2), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            if (result?.Items == null || result.Items.Count == 0)
            {
                break;
            }

            foreach (var item in result.Items)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (item.Id == null || string.IsNullOrEmpty(item.Path))
                {
                    continue;
                }

                var sourceItemId = item.Id.Value.ToString("N", CultureInfo.InvariantCulture);
                seenSourceItemIds.Add(sourceItemId);

                try
                {
                    ProcessItem(database, mapping, item, existingItems, downloadNewMode, replaceExistingMode, detectUpdatedFiles);
                    processedItems++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to process item {ItemId} ({Path})", item.Id, item.Path);
                    // Continue processing other items
                }
            }

            startIndex += batchSize;

            if (result.Items.Count < batchSize)
            {
                break;
            }
        }

        // Handle items that exist in our database but no longer exist on the source
        if (deleteMissingMode != ApprovalMode.Disabled)
        {
            try
            {
                ProcessMissingItems(database, existingItems, seenSourceItemIds, deleteMissingMode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process missing items for library {LibraryName}", mapping.SourceLibraryName);
            }
        }

        _logger.LogInformation(
            "Processed {Count} items from {LibraryName}",
            processedItems,
            mapping.SourceLibraryName);
    }

    // ProcessMissingItems
    // Handles items that exist locally but no longer exist on the source server.
    // Deletions only affect the local server, never the source server.
    private void ProcessMissingItems(
        SyncDatabase database,
        Dictionary<string, SyncItem> existingItems,
        HashSet<string> seenSourceItemIds,
        ApprovalMode deleteMissingMode)
    {
        foreach (var kvp in existingItems)
        {
            if (seenSourceItemIds.Contains(kvp.Key))
            {
                continue;
            }

            var item = kvp.Value;

            // Skip items already pending deletion or ignored
            if (item.Status == SyncStatus.PendingDeletion || item.Status == SyncStatus.Ignored)
            {
                continue;
            }

            try
            {
                // Only process synced items (they have local files to potentially delete)
                if (item.Status != SyncStatus.Synced)
                {
                    // Remove non-synced items from tracking since source no longer has them
                    database.Delete(item.SourceItemId);
                    _logger.LogInformation("Removed tracking for {FileName} (no longer on source)", Path.GetFileName(item.SourcePath));
                    continue;
                }

                // Mark for deletion - status is the same, but approval mode affects UI behavior
                item.Status = SyncStatus.PendingDeletion;
                item.StatusDate = DateTime.UtcNow;
                database.Upsert(item);

                if (deleteMissingMode == ApprovalMode.RequireApproval)
                {
                    _logger.LogInformation("Marked {FileName} for pending deletion (requires approval)", Path.GetFileName(item.LocalPath));
                }
                else
                {
                    _logger.LogInformation("Queued {FileName} for deletion (missing from source)", Path.GetFileName(item.LocalPath));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to process missing item {SourceItemId}", item.SourceItemId);
            }
        }
    }

    // ProcessItem
    // Processes a single item from the source server and updates the database accordingly.
    private void ProcessItem(
        SyncDatabase database,
        LibraryMapping mapping,
        BaseItemDto item,
        Dictionary<string, SyncItem> existingItems,
        ApprovalMode downloadNewMode,
        ApprovalMode replaceExistingMode,
        bool detectUpdatedFiles)
    {
        var sourceItemId = item.Id!.Value.ToString("N", CultureInfo.InvariantCulture);
        var sourceSize = GetItemSize(item);
        var sourceCreateDate = item.DateCreated?.DateTime ?? DateTime.UtcNow;
        // ETag is derived from the file's DateModified, making it reliable for change detection
        var sourceETag = item.Etag;
        var localPath = TranslatePath(item.Path!, mapping.SourceRootPath, mapping.LocalRootPath);

        var existingItem = existingItems.GetValueOrDefault(sourceItemId);

        if (existingItem != null)
        {
            ProcessExistingItem(database, existingItem, item.Path!, sourceSize, sourceCreateDate, sourceETag, localPath, replaceExistingMode, detectUpdatedFiles);
        }
        else
        {
            ProcessNewItem(database, mapping, sourceItemId, item.Path!, sourceSize, sourceCreateDate, sourceETag, localPath, downloadNewMode);
        }
    }

    // ProcessExistingItem
    // Updates an existing sync item based on source changes.
    // Uses ETag for reliable change detection - ETag is derived from the file's DateModified.
    private void ProcessExistingItem(
        SyncDatabase database,
        SyncItem existingItem,
        string sourcePath,
        long sourceSize,
        DateTime sourceCreateDate,
        string? sourceETag,
        string localPath,
        ApprovalMode replaceExistingMode,
        bool detectUpdatedFiles)
    {
        // Ignored items stay in their state
        if (existingItem.Status == SyncStatus.Ignored)
        {
            return;
        }

        // If item was pending deletion but now exists on source, restore it
        if (existingItem.Status == SyncStatus.PendingDeletion)
        {
            existingItem.Status = SyncStatus.Queued;
            existingItem.StatusDate = DateTime.UtcNow;
            existingItem.SourcePath = sourcePath;
            existingItem.SourceSize = sourceSize;
            existingItem.SourceCreateDate = sourceCreateDate;
            existingItem.SourceETag = sourceETag;
            existingItem.LocalPath = localPath;
            database.Upsert(existingItem);
            _logger.LogInformation("Restored {FileName} (reappeared on source)", Path.GetFileName(sourcePath));
            return;
        }

        // Pending items stay pending but update metadata
        if (existingItem.Status == SyncStatus.Pending)
        {
            if (existingItem.SourceSize != sourceSize || existingItem.SourcePath != sourcePath || existingItem.SourceETag != sourceETag)
            {
                existingItem.SourcePath = sourcePath;
                existingItem.SourceSize = sourceSize;
                existingItem.SourceCreateDate = sourceCreateDate;
                existingItem.SourceETag = sourceETag;
                existingItem.LocalPath = localPath;
                database.Upsert(existingItem);
            }

            return;
        }

        // PendingReplacement items stay pending but update metadata
        if (existingItem.Status == SyncStatus.PendingReplacement)
        {
            if (existingItem.SourceSize != sourceSize || existingItem.SourcePath != sourcePath || existingItem.SourceETag != sourceETag)
            {
                existingItem.SourcePath = sourcePath;
                existingItem.SourceSize = sourceSize;
                existingItem.SourceCreateDate = sourceCreateDate;
                existingItem.SourceETag = sourceETag;
                existingItem.LocalPath = localPath;
                database.Upsert(existingItem);
            }

            return;
        }

        // Check for source changes using size, path, or ETag
        var sourceChanged = existingItem.SourceSize != sourceSize ||
                            existingItem.SourcePath != sourcePath ||
                            (sourceETag != null && existingItem.SourceETag != sourceETag);

        // Queued items stay queued but update metadata if changed
        if (existingItem.Status == SyncStatus.Queued)
        {
            if (sourceChanged)
            {
                existingItem.SourcePath = sourcePath;
                existingItem.SourceSize = sourceSize;
                existingItem.SourceCreateDate = sourceCreateDate;
                existingItem.SourceETag = sourceETag;
                existingItem.LocalPath = localPath;
                database.Upsert(existingItem);
            }

            return;
        }

        // Source changed (size, path, or ETag) - handle based on approval mode
        if (sourceChanged)
        {
            // If replace is disabled, don't queue the update
            if (replaceExistingMode == ApprovalMode.Disabled)
            {
                // Just update metadata without changing status
                existingItem.SourcePath = sourcePath;
                existingItem.SourceSize = sourceSize;
                existingItem.SourceCreateDate = sourceCreateDate;
                existingItem.SourceETag = sourceETag;
                existingItem.LocalPath = localPath;
                database.Upsert(existingItem);
                return;
            }

            var oldETag = existingItem.SourceETag;
            existingItem.SourcePath = sourcePath;
            existingItem.SourceSize = sourceSize;
            existingItem.SourceCreateDate = sourceCreateDate;
            existingItem.SourceETag = sourceETag;
            existingItem.LocalPath = localPath;

            if (replaceExistingMode == ApprovalMode.RequireApproval)
            {
                existingItem.Status = SyncStatus.PendingReplacement;
                existingItem.StatusDate = DateTime.UtcNow;
                database.Upsert(existingItem);
                _logger.LogInformation("Marked {FileName} for pending replacement (requires approval, ETag: {OldETag} -> {NewETag})",
                    Path.GetFileName(sourcePath), oldETag ?? "null", sourceETag ?? "null");
            }
            else
            {
                existingItem.Status = SyncStatus.Queued;
                existingItem.StatusDate = DateTime.UtcNow;
                database.Upsert(existingItem);
                _logger.LogInformation("Re-queued {FileName} (source changed, ETag: {OldETag} -> {NewETag})",
                    Path.GetFileName(sourcePath), oldETag ?? "null", sourceETag ?? "null");
            }

            return;
        }

        // For Synced items with detectUpdatedFiles enabled, verify local file integrity
        // Note: ETag changes are already caught by sourceChanged above
        if (existingItem.Status == SyncStatus.Synced && detectUpdatedFiles)
        {
            try
            {
                if (File.Exists(localPath))
                {
                    var localInfo = new FileInfo(localPath);
                    if (localInfo.Length != sourceSize)
                    {
                        // If replace is disabled, don't queue
                        if (replaceExistingMode == ApprovalMode.Disabled)
                        {
                            return;
                        }

                        if (replaceExistingMode == ApprovalMode.RequireApproval)
                        {
                            existingItem.Status = SyncStatus.PendingReplacement;
                        }
                        else
                        {
                            existingItem.Status = SyncStatus.Queued;
                        }

                        existingItem.StatusDate = DateTime.UtcNow;
                        database.Upsert(existingItem);
                        _logger.LogInformation("Re-queued {FileName} (local size {LocalSize} != source size {SourceSize})",
                            Path.GetFileName(localPath), localInfo.Length, sourceSize);
                    }
                }
                else
                {
                    // Local file missing - this is like a new download, use download mode
                    existingItem.Status = SyncStatus.Queued;
                    existingItem.StatusDate = DateTime.UtcNow;
                    existingItem.LocalItemId = null;
                    database.Upsert(existingItem);
                    _logger.LogInformation("Re-queued {FileName} (local file missing)", Path.GetFileName(localPath));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to check local file status for {LocalPath}", localPath);
            }
        }
    }

    // ProcessNewItem
    // Creates a new sync item entry in the database.
    private static void ProcessNewItem(
        SyncDatabase database,
        LibraryMapping mapping,
        string sourceItemId,
        string sourcePath,
        long sourceSize,
        DateTime sourceCreateDate,
        string? sourceETag,
        string localPath,
        ApprovalMode downloadNewMode)
    {
        // If download new content is disabled, don't track new items at all
        if (downloadNewMode == ApprovalMode.Disabled)
        {
            return;
        }

        var syncItem = new SyncItem
        {
            SourceLibraryId = mapping.SourceLibraryId,
            LocalLibraryId = mapping.LocalLibraryId,
            SourceItemId = sourceItemId,
            SourcePath = sourcePath,
            SourceSize = sourceSize,
            SourceCreateDate = sourceCreateDate,
            SourceModifyDate = sourceCreateDate, // Default to create date, ETag is the reliable change indicator
            SourceETag = sourceETag,
            LocalPath = localPath,
            StatusDate = DateTime.UtcNow,
            Status = downloadNewMode == ApprovalMode.RequireApproval ? SyncStatus.Pending : SyncStatus.Queued
        };

        // Check if local file already exists with matching size
        if (File.Exists(localPath))
        {
            var localInfo = new FileInfo(localPath);
            if (localInfo.Length == sourceSize)
            {
                syncItem.Status = SyncStatus.Synced;
            }
        }

        database.Upsert(syncItem);
    }

    // GetItemSize
    // Extracts the file size from an item's media sources.
    private static long GetItemSize(BaseItemDto item)
    {
        if (item.MediaSources != null && item.MediaSources.Count > 0)
        {
            var firstSource = item.MediaSources[0];
            if (firstSource.Size.HasValue)
            {
                return firstSource.Size.Value;
            }
        }

        return 0;
    }

    // TranslatePath
    // Translates a source server path to the corresponding local path.
    private static string TranslatePath(string sourcePath, string sourceRoot, string localRoot)
    {
        // Normalize path separators for comparison
        sourceRoot = sourceRoot.TrimEnd('/').TrimEnd('\\');
        localRoot = localRoot.TrimEnd('/').TrimEnd('\\');

        if (sourcePath.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase))
        {
            var relativePath = sourcePath.Substring(sourceRoot.Length).TrimStart('/').TrimStart('\\');

            // Split by both separators and recombine using Path.Combine for cross-platform support
            var pathParts = relativePath.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (pathParts.Length > 0)
            {
                var result = localRoot;
                foreach (var part in pathParts)
                {
                    result = Path.Combine(result, part);
                }

                return result;
            }

            return localRoot;
        }

        var fileName = Path.GetFileName(sourcePath);
        return Path.Combine(localRoot, fileName);
    }

    public IEnumerable<MediaBrowser.Model.Tasks.TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new MediaBrowser.Model.Tasks.TaskTriggerInfo
            {
                Type = MediaBrowser.Model.Tasks.TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(6).Ticks
            }
        };
    }
}
