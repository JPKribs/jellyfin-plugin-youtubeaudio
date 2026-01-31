using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
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
    private readonly ILogger _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;

    private const int DefaultBatchSize = 100;
    private const int MaxConsecutiveErrors = 3;

    /// <summary>
    /// Initializes a new instance of the <see cref="HistorySyncTableService"/> class.
    /// </summary>
    public HistorySyncTableService(
        ILogger logger,
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

        var startIndex = 0;
        var processedItems = 0;
        var consecutiveErrors = 0;

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

        _logger.LogInformation(
            "Processing history for user {SourceUser} -> {LocalUser} in library {Library}",
            userMapping.SourceUserName, userMapping.LocalUserName, libraryMapping.SourceLibraryName);

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            BaseItemDtoQueryResult? result;
            try
            {
                // Get items with user data from source server
                result = await client.GetUserLibraryItemsAsync(
                    sourceUserId,
                    sourceLibraryId,
                    startIndex,
                    DefaultBatchSize,
                    cancellationToken).ConfigureAwait(false);

                consecutiveErrors = 0;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveErrors++;
                _logger.LogWarning(ex,
                    "Failed to fetch items from library {Library} at index {Index} (attempt {Attempt}/{Max})",
                    libraryMapping.SourceLibraryName, startIndex, consecutiveErrors, MaxConsecutiveErrors);

                if (consecutiveErrors >= MaxConsecutiveErrors)
                {
                    _logger.LogError("Too many consecutive errors fetching from {Library}, stopping sync",
                        libraryMapping.SourceLibraryName);
                    break;
                }

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

            foreach (var sourceItem in result.Items)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (sourceItem.Id == null || string.IsNullOrEmpty(sourceItem.Path))
                {
                    continue;
                }

                try
                {
                    ProcessHistoryItem(
                        database,
                        userMapping,
                        libraryMapping,
                        sourceItem,
                        localUser,
                        existingItems);

                    processedItems++;
                    onItemProcessed?.Invoke();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to process history item {ItemId} ({Name})",
                        sourceItem.Id, sourceItem.Name);
                }
            }

            startIndex += DefaultBatchSize;

            if (result.Items.Count < DefaultBatchSize)
            {
                break;
            }
        }

        _logger.LogInformation(
            "Processed {Count} history items for user {User} in library {Library}",
            processedItems, userMapping.SourceUserName, libraryMapping.SourceLibraryName);

        return processedItems;
    }

    /// <summary>
    /// Processes a single history item from the source server.
    /// </summary>
    private void ProcessHistoryItem(
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
        string? localItemId = localItem?.Id.ToString("N", CultureInfo.InvariantCulture);

        // Get local user data if item exists
        UserItemData? localUserData = null;
        if (localItem != null)
        {
            localUserData = _userDataManager.GetUserData(localUser, localItem);
        }

        // Check if we have an existing history item
        var existingItem = existingItems.GetValueOrDefault(sourceItemId);

        if (existingItem != null)
        {
            // Update existing item
            UpdateHistoryItem(existingItem, sourceItem, localItem, localUserData, localPath, localItemId);
            database.UpsertHistoryItem(existingItem);
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
        string? localItemId,
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
            SourceLastPlayedDate = sourceItem.UserData?.LastPlayedDate?.DateTime,
            SourceIsFavorite = sourceItem.UserData?.IsFavorite,

            // Local history state
            LocalIsPlayed = localUserData?.Played,
            LocalPlayCount = localUserData?.PlayCount,
            LocalPlaybackPositionTicks = localUserData?.PlaybackPositionTicks,
            LocalLastPlayedDate = localUserData?.LastPlayedDate,
            LocalIsFavorite = localUserData?.IsFavorite,

            // Tracking
            Status = HistorySyncStatus.Queued,
            StatusDate = DateTime.UtcNow
        };

        // Calculate merged values
        HistorySyncMergeService.MergeHistoryData(item);

        // Determine initial status based on whether there are changes
        if (string.IsNullOrEmpty(localItemId))
        {
            // Local item not found - can't sync yet, keep queued until content syncs
            item.Status = HistorySyncStatus.Queued;
        }
        else if (HistorySyncMergeService.HasChangesToSync(item))
        {
            // Has changes - queue for sync (no approval needed for history)
            item.Status = HistorySyncStatus.Queued;
        }
        else
        {
            // No changes needed - already in sync
            item.Status = HistorySyncStatus.Synced;
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
        BaseItem? localItem,
        UserItemData? localUserData,
        string localPath,
        string? localItemId)
    {
        // Update identification
        item.LocalItemId = localItemId;
        item.LocalPath = localPath;
        item.ItemName = sourceItem.Name ?? item.ItemName;

        // Update source state
        item.SourceIsPlayed = sourceItem.UserData?.Played;
        item.SourcePlayCount = sourceItem.UserData?.PlayCount;
        item.SourcePlaybackPositionTicks = sourceItem.UserData?.PlaybackPositionTicks;
        item.SourceLastPlayedDate = sourceItem.UserData?.LastPlayedDate?.DateTime;
        item.SourceIsFavorite = sourceItem.UserData?.IsFavorite;

        // Update local state
        item.LocalIsPlayed = localUserData?.Played;
        item.LocalPlayCount = localUserData?.PlayCount;
        item.LocalPlaybackPositionTicks = localUserData?.PlaybackPositionTicks;
        item.LocalLastPlayedDate = localUserData?.LastPlayedDate;
        item.LocalIsFavorite = localUserData?.IsFavorite;

        // Recalculate merged values
        HistorySyncMergeService.MergeHistoryData(item);

        // Update status if it was already synced but now has new changes
        if (item.Status == HistorySyncStatus.Synced && HistorySyncMergeService.HasChangesToSync(item))
        {
            item.Status = HistorySyncStatus.Queued;
            item.StatusDate = DateTime.UtcNow;
        }
        else if (item.Status == HistorySyncStatus.Queued && !HistorySyncMergeService.HasChangesToSync(item) && !string.IsNullOrEmpty(localItemId))
        {
            // Previously queued but now in sync
            item.Status = HistorySyncStatus.Synced;
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
