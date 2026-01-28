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

        // Validate authentication based on method
        var hasValidAuth = config.AuthMethod == AuthenticationMethod.ApiKey
            ? !string.IsNullOrWhiteSpace(config.SourceServerApiKey)
            : !string.IsNullOrWhiteSpace(config.SourceServerUsername);

        if (!hasValidAuth)
        {
            _logger.LogWarning("Sync table update skipped: authentication not configured");
            return;
        }

        var enabledMappings = config.LibraryMappings?.Where(m => m.IsEnabled).ToList() ?? new List<LibraryMapping>();
        if (enabledMappings.Count == 0)
        {
            _logger.LogDebug("Sync table update skipped: no enabled library mappings");
            return;
        }

        _logger.LogInformation("Starting sync table update from {SourceUrl}", config.SourceServerUrl);

        SourceServerClient client;
        if (config.AuthMethod == AuthenticationMethod.UserCredentials)
        {
            client = new SourceServerClient(
                plugin.LoggerFactory.CreateLogger<SourceServerClient>(),
                config.SourceServerUrl,
                config.SourceServerUsername,
                config.SourceServerPassword ?? string.Empty,
                config.SourceServerAccessToken);
        }
        else
        {
            client = new SourceServerClient(
                plugin.LoggerFactory.CreateLogger<SourceServerClient>(),
                config.SourceServerUrl,
                config.SourceServerApiKey);
        }

        using (client)
        {
            var connectionResult = await client.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
            if (!connectionResult.Success)
            {
                _logger.LogError("Failed to connect to source server at {SourceUrl}: {Error}",
                    config.SourceServerUrl, connectionResult.ErrorMessage ?? "Unknown error");
                return;
            }

            // Save access token if using user credentials
            if (config.AuthMethod == AuthenticationMethod.UserCredentials && !string.IsNullOrEmpty(connectionResult.AccessToken))
            {
                config.SourceServerAccessToken = connectionResult.AccessToken;
                try
                {
                    plugin.SaveConfiguration();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to save access token");
                }
            }

            var database = plugin.Database;
            var totalMappings = enabledMappings.Count;
            var processedMappings = 0;

            var requireApproval = config.RequireApprovalToSync;
            var detectUpdatedFiles = config.DetectUpdatedFiles;
            var deleteIfMissing = config.DeleteIfMissingFromSource;

            foreach (var mapping in enabledMappings)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                await ProcessLibraryAsync(client, database, mapping, requireApproval, detectUpdatedFiles, deleteIfMissing, cancellationToken).ConfigureAwait(false);

                processedMappings++;
                progress.Report((double)processedMappings / totalMappings * 100);
            }

            _logger.LogInformation("Sync table update completed");
        }
    }

    private async Task ProcessLibraryAsync(
        SourceServerClient client,
        SyncDatabase database,
        LibraryMapping mapping,
        bool requireApproval,
        bool detectUpdatedFiles,
        bool deleteIfMissing,
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
                    ProcessItem(database, mapping, item, existingItems, requireApproval, detectUpdatedFiles);
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
        if (deleteIfMissing)
        {
            try
            {
                ProcessMissingItems(database, existingItems, seenSourceItemIds, requireApproval);
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
        bool requireApproval)
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

                if (requireApproval)
                {
                    // Set to pending deletion for manual approval
                    item.Status = SyncStatus.PendingDeletion;
                    item.StatusDate = DateTime.UtcNow;
                    database.Upsert(item);
                    _logger.LogInformation("Marked {FileName} for pending deletion (missing from source)", Path.GetFileName(item.LocalPath));
                }
                else
                {
                    // Mark for automatic deletion - will be handled by delete task or controller
                    item.Status = SyncStatus.PendingDeletion;
                    item.StatusDate = DateTime.UtcNow;
                    database.Upsert(item);
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
        bool requireApproval,
        bool detectUpdatedFiles)
    {
        var sourceItemId = item.Id!.Value.ToString("N", CultureInfo.InvariantCulture);
        var sourceSize = GetItemSize(item);
        var sourceCreateDate = item.DateCreated?.DateTime ?? DateTime.UtcNow;
        var sourceModifyDate = item.DateCreated?.DateTime ?? DateTime.UtcNow;
        var localPath = TranslatePath(item.Path!, mapping.SourceRootPath, mapping.LocalRootPath);

        var existingItem = existingItems.GetValueOrDefault(sourceItemId);

        if (existingItem != null)
        {
            ProcessExistingItem(database, existingItem, item.Path!, sourceSize, sourceCreateDate, sourceModifyDate, localPath, detectUpdatedFiles);
        }
        else
        {
            ProcessNewItem(database, mapping, sourceItemId, item.Path!, sourceSize, sourceCreateDate, sourceModifyDate, localPath, requireApproval);
        }
    }

    // ProcessExistingItem
    // Updates an existing sync item based on source changes.
    private void ProcessExistingItem(
        SyncDatabase database,
        SyncItem existingItem,
        string sourcePath,
        long sourceSize,
        DateTime sourceCreateDate,
        DateTime sourceModifyDate,
        string localPath,
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
            existingItem.SourceModifyDate = sourceModifyDate;
            existingItem.LocalPath = localPath;
            database.Upsert(existingItem);
            _logger.LogInformation("Restored {FileName} (reappeared on source)", Path.GetFileName(sourcePath));
            return;
        }

        // Pending items stay pending but update metadata
        if (existingItem.Status == SyncStatus.Pending)
        {
            if (existingItem.SourceSize != sourceSize || existingItem.SourcePath != sourcePath)
            {
                existingItem.SourcePath = sourcePath;
                existingItem.SourceSize = sourceSize;
                existingItem.SourceCreateDate = sourceCreateDate;
                existingItem.SourceModifyDate = sourceModifyDate;
                existingItem.LocalPath = localPath;
                database.Upsert(existingItem);
            }

            return;
        }

        var sourceChanged = existingItem.SourceSize != sourceSize || existingItem.SourcePath != sourcePath;

        // Queued items stay queued but update metadata if changed
        if (existingItem.Status == SyncStatus.Queued)
        {
            if (sourceChanged)
            {
                existingItem.SourcePath = sourcePath;
                existingItem.SourceSize = sourceSize;
                existingItem.SourceCreateDate = sourceCreateDate;
                existingItem.SourceModifyDate = sourceModifyDate;
                existingItem.LocalPath = localPath;
                database.Upsert(existingItem);
            }

            return;
        }

        // Source changed - mark as queued
        if (sourceChanged)
        {
            existingItem.SourcePath = sourcePath;
            existingItem.SourceSize = sourceSize;
            existingItem.SourceCreateDate = sourceCreateDate;
            existingItem.SourceModifyDate = sourceModifyDate;
            existingItem.LocalPath = localPath;
            existingItem.Status = SyncStatus.Queued;
            existingItem.StatusDate = DateTime.UtcNow;
            database.Upsert(existingItem);
            _logger.LogInformation("Re-queued {FileName} (source changed)", Path.GetFileName(sourcePath));
            return;
        }

        // Check if synced items need re-syncing based on detectUpdatedFiles setting
        if (existingItem.Status == SyncStatus.Synced && detectUpdatedFiles)
        {
            // Check if source was modified after we last synced
            if (sourceModifyDate > existingItem.StatusDate)
            {
                existingItem.SourceModifyDate = sourceModifyDate;
                existingItem.Status = SyncStatus.Queued;
                existingItem.StatusDate = DateTime.UtcNow;
                database.Upsert(existingItem);
                _logger.LogInformation("Re-queued {FileName} (source modified after sync)", Path.GetFileName(sourcePath));
                return;
            }

            // Check if local file exists and matches size
            try
            {
                if (File.Exists(localPath))
                {
                    var localInfo = new FileInfo(localPath);
                    if (localInfo.Length != sourceSize)
                    {
                        existingItem.Status = SyncStatus.Queued;
                        existingItem.StatusDate = DateTime.UtcNow;
                        database.Upsert(existingItem);
                        _logger.LogInformation("Re-queued {FileName} (local size mismatch)", Path.GetFileName(localPath));
                    }
                }
                else
                {
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
        DateTime sourceModifyDate,
        string localPath,
        bool requireApproval)
    {
        var syncItem = new SyncItem
        {
            SourceLibraryId = mapping.SourceLibraryId,
            LocalLibraryId = mapping.LocalLibraryId,
            SourceItemId = sourceItemId,
            SourcePath = sourcePath,
            SourceSize = sourceSize,
            SourceCreateDate = sourceCreateDate,
            SourceModifyDate = sourceModifyDate,
            LocalPath = localPath,
            StatusDate = DateTime.UtcNow,
            Status = requireApproval ? SyncStatus.Pending : SyncStatus.Queued
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
