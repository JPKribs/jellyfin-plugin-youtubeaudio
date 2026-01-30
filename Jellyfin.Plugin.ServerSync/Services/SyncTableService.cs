using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;
using Jellyfin.Plugin.ServerSync.Utilities;
using Jellyfin.Sdk.Generated.Models;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Service for synchronizing the sync table with the source server.
/// </summary>
public class SyncTableService
{
    private readonly ILogger _logger;
    private readonly ILibraryManager _libraryManager;

    private const int DefaultBatchSize = 100;
    private const int MaxConsecutiveErrors = 3;

    public SyncTableService(ILogger logger, ILibraryManager libraryManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Processes a single library mapping, fetching items and updating the sync database.
    /// </summary>
    /// <param name="client">Source server client.</param>
    /// <param name="database">Sync database.</param>
    /// <param name="mapping">Library mapping to process.</param>
    /// <param name="downloadNewMode">Mode for handling new items.</param>
    /// <param name="replaceExistingMode">Mode for handling existing items.</param>
    /// <param name="deleteMissingMode">Mode for handling missing items.</param>
    /// <param name="detectUpdatedFiles">Whether to detect updated files.</param>
    /// <param name="changeDetectionPolicy">Policy for detecting source changes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="onItemProcessed">Optional callback invoked after each item is processed.</param>
    /// <returns>Number of items processed.</returns>
    public async Task<int> ProcessLibraryAsync(
        SourceServerClient client,
        SyncDatabase database,
        LibraryMapping mapping,
        ApprovalMode downloadNewMode,
        ApprovalMode replaceExistingMode,
        ApprovalMode deleteMissingMode,
        bool detectUpdatedFiles,
        ChangeDetectionPolicy changeDetectionPolicy,
        CancellationToken cancellationToken,
        Action? onItemProcessed = null)
    {
        var startIndex = 0;
        var processedItems = 0;
        var consecutiveErrors = 0;

        Dictionary<string, SyncItem> existingItems;
        try
        {
            var items = database.GetBySourceLibrary(mapping.SourceLibraryId);
            existingItems = new Dictionary<string, SyncItem>();
            foreach (var item in items)
            {
                existingItems[item.SourceItemId] = item;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load existing items for library {LibraryName}", mapping.SourceLibraryName);
            return 0;
        }

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
                _logger.LogWarning(ex, "Failed to fetch items from library {LibraryName} at index {Index} (attempt {Attempt}/{Max})",
                    mapping.SourceLibraryName, startIndex, consecutiveErrors, MaxConsecutiveErrors);

                if (consecutiveErrors >= MaxConsecutiveErrors)
                {
                    _logger.LogError("Too many consecutive errors fetching from {LibraryName}, stopping sync for this library",
                        mapping.SourceLibraryName);
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
                    ProcessItem(database, mapping, item, existingItems, downloadNewMode, replaceExistingMode, detectUpdatedFiles, changeDetectionPolicy);
                    processedItems++;
                    onItemProcessed?.Invoke();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to process item {ItemId} ({Path})", item.Id, item.Path);
                }
            }

            startIndex += DefaultBatchSize;

            if (result.Items.Count < DefaultBatchSize)
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

        _logger.LogInformation("Processed {Count} items from {LibraryName}", processedItems, mapping.SourceLibraryName);

        return processedItems;
    }

    /// <summary>
    /// Resolves LocalItemId for synced items by looking them up in the local Jellyfin library.
    /// </summary>
    /// <param name="database">Sync database.</param>
    /// <returns>Number of items resolved.</returns>
    public int ResolveLocalItemIds(SyncDatabase database)
    {
        var syncedItems = database.GetByStatus(SyncStatus.Synced);
        var resolvedCount = 0;

        foreach (var item in syncedItems)
        {
            if (!string.IsNullOrEmpty(item.LocalItemId))
            {
                continue;
            }

            if (string.IsNullOrEmpty(item.LocalPath))
            {
                continue;
            }

            try
            {
                var localItem = _libraryManager.FindByPath(item.LocalPath, isFolder: false);
                if (localItem != null)
                {
                    item.LocalItemId = localItem.Id.ToString();
                    database.Upsert(item);
                    resolvedCount++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to resolve LocalItemId for {FileName}", System.IO.Path.GetFileName(item.LocalPath));
            }
        }

        if (resolvedCount > 0)
        {
            _logger.LogInformation("Resolved {Count} local item IDs", resolvedCount);
        }

        return resolvedCount;
    }

    /// <summary>
    /// Processes items that exist locally but no longer exist on the source server.
    /// </summary>
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

            try
            {
                SyncStateService.ProcessMissingItem(database, kvp.Value, deleteMissingMode, _logger);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to process missing item {SourceItemId}", kvp.Value.SourceItemId);
            }
        }
    }

    /// <summary>
    /// Processes a single item from the source server and updates the database accordingly.
    /// </summary>
    private void ProcessItem(
        SyncDatabase database,
        LibraryMapping mapping,
        BaseItemDto item,
        Dictionary<string, SyncItem> existingItems,
        ApprovalMode downloadNewMode,
        ApprovalMode replaceExistingMode,
        bool detectUpdatedFiles,
        ChangeDetectionPolicy changeDetectionPolicy)
    {
        var sourceItemId = item.Id!.Value.ToString("N", CultureInfo.InvariantCulture);
        var sourceSize = MediaItemUtilities.GetItemSize(item);
        var sourceCreateDate = item.DateCreated?.DateTime ?? DateTime.UtcNow;
        var sourceETag = item.Etag;
        var localPath = PathUtilities.TranslatePath(item.Path!, mapping.SourceRootPath, mapping.LocalRootPath);

        var existingItem = existingItems.GetValueOrDefault(sourceItemId);

        if (existingItem != null)
        {
            SyncStateService.ProcessExistingItem(
                database,
                existingItem,
                item.Path!,
                sourceSize,
                sourceCreateDate,
                sourceETag,
                localPath,
                replaceExistingMode,
                detectUpdatedFiles,
                changeDetectionPolicy,
                _logger);
        }
        else
        {
            SyncStateService.ProcessNewItem(
                database,
                mapping,
                sourceItemId,
                item.Path!,
                sourceSize,
                sourceCreateDate,
                sourceETag,
                localPath,
                downloadNewMode);
        }
    }
}
