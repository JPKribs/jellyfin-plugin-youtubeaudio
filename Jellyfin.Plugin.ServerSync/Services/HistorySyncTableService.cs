using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;
using Jellyfin.Plugin.ServerSync.Utilities;
using Jellyfin.Sdk.Generated.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Service for synchronizing the history sync table with source and local servers.
/// </summary>
public class HistorySyncTableService
{
    private readonly ILogger<HistorySyncTableService> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="HistorySyncTableService"/> class.
    /// </summary>
    public HistorySyncTableService(
        ILogger<HistorySyncTableService> logger,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
    }

    /// <summary>
    /// Processes a user/library mapping, fetching history data and updating the sync database.
    /// </summary>
    /// <param name="client">Source server client.</param>
    /// <param name="database">Sync database.</param>
    /// <param name="userMapping">User mapping.</param>
    /// <param name="libraryMapping">Library mapping.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="onItemProcessed">Optional callback after each item is processed.</param>
    /// <returns>Number of items processed.</returns>
    public async Task<int> ProcessUserLibraryAsync(
        SourceServerClient client,
        SyncDatabase database,
        UserMapping userMapping,
        LibraryMapping libraryMapping,
        CancellationToken cancellationToken,
        Action? onItemProcessed = null)
    {
        var sourceUserId = Guid.Parse(userMapping.SourceUserId);
        var localUserId = Guid.Parse(userMapping.LocalUserId);
        var sourceLibraryId = Guid.Parse(libraryMapping.SourceLibraryId);

        // Verify local user exists
        var localUser = _userManager.GetUserById(localUserId);
        if (localUser == null)
        {
            _logger.LogWarning("Local user {LocalUserId} not found, skipping history sync", localUserId);
            return 0;
        }

        var processedItems = 0;

        // Load existing history items for this user/library
        var existingItems = new Dictionary<string, HistorySyncItem>();
        try
        {
            var items = database.GetHistoryItemsByUserMapping(userMapping.SourceUserId, userMapping.LocalUserId);
            foreach (var item in items)
            {
                if (item.SourceLibraryId == libraryMapping.SourceLibraryId)
                {
                    existingItems[item.SourceItemId] = item;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load existing history items for user {User} in library {Library}",
                userMapping.SourceUserName, libraryMapping.SourceLibraryName);
            return 0;
        }

        // Remove existing history records for items under newly-ignored paths
        if (libraryMapping.IgnoredPaths?.Count > 0)
        {
            var keysToRemove = new List<string>();
            foreach (var kvp in existingItems)
            {
                if (!string.IsNullOrEmpty(kvp.Value.SourcePath)
                    && PathUtilities.IsPathIgnored(kvp.Value.SourcePath, libraryMapping.SourceRootPath, libraryMapping.IgnoredPaths))
                {
                    database.DeleteHistoryItem(kvp.Value.Id);
                    keysToRemove.Add(kvp.Key);
                    _logger.LogInformation("Removed history record for {Path} (path matches ignored folder)", kvp.Value.SourcePath);
                }
            }

            foreach (var key in keysToRemove)
            {
                existingItems.Remove(key);
            }
        }

        _logger.LogInformation(
            "Processing history for user {SourceUser} -> {LocalUser} in library {Library}",
            userMapping.SourceUserName, userMapping.LocalUserName, libraryMapping.SourceLibraryName);

        processedItems = await PaginatedFetchUtility.FetchAllPagesAsync(
            fetchPage: (startIndex, batchSize, ct) => client.GetUserLibraryItemsAsync(sourceUserId, sourceLibraryId, startIndex, batchSize, ct),
            processItem: (sourceItem, _) =>
            {
                var wasProcessed = ProcessHistoryItem(
                    database,
                    userMapping,
                    libraryMapping,
                    sourceItem,
                    localUser,
                    existingItems);
                return Task.FromResult(wasProcessed);
            },
            libraryName: libraryMapping.SourceLibraryName,
            sourceRootPath: libraryMapping.SourceRootPath,
            ignoredPaths: libraryMapping.IgnoredPaths,
            logger: _logger,
            cancellationToken: cancellationToken,
            onItemProcessed: onItemProcessed).ConfigureAwait(false);

        _logger.LogInformation(
            "Processed {Count} history items for user {User} in library {Library}",
            processedItems, userMapping.SourceUserName, libraryMapping.SourceLibraryName);

        return processedItems;
    }

    /// <summary>
    /// Processes a single history item from the source server.
    /// </summary>
    /// <returns>True if the item was processed, false if skipped (no local equivalent).</returns>
    private bool ProcessHistoryItem(
        SyncDatabase database,
        UserMapping userMapping,
        LibraryMapping libraryMapping,
        BaseItemDto sourceItem,
        User localUser,
        Dictionary<string, HistorySyncItem> existingItems)
    {
        var sourceItemId = sourceItem.Id!.Value.ToString("N", CultureInfo.InvariantCulture);
        var sourcePath = sourceItem.Path!;

        // Translate path to local path
        var localPath = PathUtilities.TranslatePath(sourcePath, libraryMapping.SourceRootPath, libraryMapping.LocalRootPath);

        // Try to find the local item
        var localItem = _libraryManager.FindByPath(localPath, isFolder: false);

        // Skip items that don't have a local equivalent - only sync history for items that exist locally
        if (localItem == null)
        {
            // If there was an existing history item for this source item, remove it since the local file no longer exists
            var existingItem = existingItems.GetValueOrDefault(sourceItemId);
            if (existingItem != null)
            {
                database.DeleteHistoryItem(existingItem.Id);
            }

            return false;
        }

        string localItemId = localItem.Id.ToString("N", CultureInfo.InvariantCulture);

        // Get local user data
        var localUserData = _userDataManager.GetUserData(localUser, localItem);

        // Check if we have an existing history item
        var existing = existingItems.GetValueOrDefault(sourceItemId);

        if (existing != null)
        {
            // Update existing item
            UpdateHistoryItem(existing, sourceItem, localItem, localUserData, localPath, localItemId);
            database.UpsertHistoryItem(existing);
        }
        else
        {
            // Create new item
            var newItem = CreateHistoryItem(
                userMapping,
                libraryMapping,
                sourceItem,
                sourceItemId,
                sourcePath,
                localPath,
                localItemId,
                localUserData);

            database.UpsertHistoryItem(newItem);
        }

        return true;
    }

    /// <summary>
    /// Creates a new history sync item.
    /// </summary>
    private HistorySyncItem CreateHistoryItem(
        UserMapping userMapping,
        LibraryMapping libraryMapping,
        BaseItemDto sourceItem,
        string sourceItemId,
        string sourcePath,
        string localPath,
        string localItemId,
        UserItemData? localUserData)
    {
        var item = new HistorySyncItem
        {
            // Mapping context
            SourceUserId = userMapping.SourceUserId,
            LocalUserId = userMapping.LocalUserId,
            SourceLibraryId = libraryMapping.SourceLibraryId,
            LocalLibraryId = libraryMapping.LocalLibraryId ?? string.Empty,

            // Item identification
            SourceItemId = sourceItemId,
            LocalItemId = localItemId,
            ItemName = sourceItem.Name ?? System.IO.Path.GetFileNameWithoutExtension(sourcePath),
            SourcePath = sourcePath,
            LocalPath = localPath,

            // Source history state (from UserData in BaseItemDto)
            SourceIsPlayed = sourceItem.UserData?.Played,
            SourcePlayCount = sourceItem.UserData?.PlayCount,
            SourcePlaybackPositionTicks = sourceItem.UserData?.PlaybackPositionTicks,
            SourceLastPlayedDate = sourceItem.UserData?.LastPlayedDate?.UtcDateTime,
            SourceIsFavorite = sourceItem.UserData?.IsFavorite,

            // Local history state (may be null if user never interacted with item)
            LocalIsPlayed = localUserData?.Played,
            LocalPlayCount = localUserData?.PlayCount,
            LocalPlaybackPositionTicks = localUserData?.PlaybackPositionTicks,
            LocalLastPlayedDate = localUserData?.LastPlayedDate,
            LocalIsFavorite = localUserData?.IsFavorite,

            // Tracking
            Status = BaseSyncStatus.Queued,
            StatusDate = DateTime.UtcNow
        };

        // Calculate merged values
        HistorySyncMergeService.MergeHistoryData(item);

        // Determine initial status based on whether there are changes
        if (HistorySyncMergeService.HasChangesToSync(item))
        {
            // Has changes - queue for sync (no approval needed for history)
            item.Status = BaseSyncStatus.Queued;
        }
        else
        {
            // No changes needed - already in sync
            item.Status = BaseSyncStatus.Synced;
            item.LastSyncTime = DateTime.UtcNow;
        }

        return item;
    }

    /// <summary>
    /// Updates an existing history sync item with current data.
    /// </summary>
    private void UpdateHistoryItem(
        HistorySyncItem item,
        BaseItemDto sourceItem,
        BaseItem localItem,
        UserItemData? localUserData,
        string localPath,
        string localItemId)
    {
        // Update identification
        item.LocalItemId = localItemId;
        item.LocalPath = localPath;
        item.ItemName = sourceItem.Name ?? item.ItemName;

        // Update source state
        item.SourceIsPlayed = sourceItem.UserData?.Played;
        item.SourcePlayCount = sourceItem.UserData?.PlayCount;
        item.SourcePlaybackPositionTicks = sourceItem.UserData?.PlaybackPositionTicks;
        item.SourceLastPlayedDate = sourceItem.UserData?.LastPlayedDate?.UtcDateTime;
        item.SourceIsFavorite = sourceItem.UserData?.IsFavorite;

        // Update local state (may be null if user never interacted with item)
        item.LocalIsPlayed = localUserData?.Played;
        item.LocalPlayCount = localUserData?.PlayCount;
        item.LocalPlaybackPositionTicks = localUserData?.PlaybackPositionTicks;
        item.LocalLastPlayedDate = localUserData?.LastPlayedDate;
        item.LocalIsFavorite = localUserData?.IsFavorite;

        // Recalculate merged values
        HistorySyncMergeService.MergeHistoryData(item);

        // Preserve Ignored status - don't change status for ignored items
        if (item.Status == BaseSyncStatus.Ignored)
        {
            return;
        }

        // Update status if it was already synced but now has new changes
        if (item.Status == BaseSyncStatus.Synced && HistorySyncMergeService.HasChangesToSync(item))
        {
            item.Status = BaseSyncStatus.Queued;
            item.StatusDate = DateTime.UtcNow;
        }
        else if (item.Status == BaseSyncStatus.Queued && !HistorySyncMergeService.HasChangesToSync(item))
        {
            // Previously queued but now in sync
            item.Status = BaseSyncStatus.Synced;
            item.StatusDate = DateTime.UtcNow;
            item.LastSyncTime = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Gets the total count of items to process for progress tracking.
    /// </summary>
    public async Task<int> GetTotalItemCountAsync(
        SourceServerClient client,
        IEnumerable<UserMapping> userMappings,
        IEnumerable<LibraryMapping> libraryMappings,
        CancellationToken cancellationToken)
    {
        var totalCount = 0;

        foreach (var userMapping in userMappings)
        {
            if (!userMapping.IsEnabled || string.IsNullOrEmpty(userMapping.SourceUserId))
            {
                continue;
            }

            var sourceUserId = Guid.Parse(userMapping.SourceUserId);

            foreach (var libraryMapping in libraryMappings)
            {
                if (!libraryMapping.IsEnabled || string.IsNullOrEmpty(libraryMapping.SourceLibraryId))
                {
                    continue;
                }

                var sourceLibraryId = Guid.Parse(libraryMapping.SourceLibraryId);
                var count = await client.GetUserLibraryItemCountAsync(
                    sourceUserId,
                    sourceLibraryId,
                    cancellationToken).ConfigureAwait(false);

                totalCount += count;
            }
        }

        return totalCount;
    }
}
