using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Jellyfin.Plugin.ServerSync.Utilities;
using Jellyfin.Sdk.Generated.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Service for synchronizing the metadata sync table with source and local servers.
/// Uses file path matching (like History Sync) to correlate items.
/// </summary>
public class MetadataSyncTableService
{
    private readonly ILogger _logger;
    private readonly ILibraryManager _libraryManager;

    private const int DefaultBatchSize = 100;
    private const int MaxConsecutiveErrors = 3;

    /// <summary>
    /// Initializes a new instance of the <see cref="MetadataSyncTableService"/> class.
    /// </summary>
    public MetadataSyncTableService(
        ILogger logger,
        ILibraryManager libraryManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Processes a library mapping, fetching metadata and updating the sync database.
    /// </summary>
    /// <param name="client">Source server client.</param>
    /// <param name="database">Sync database.</param>
    /// <param name="libraryMapping">Library mapping.</param>
    /// <param name="enabledCategories">List of enabled property categories to sync.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="onItemProcessed">Optional callback after each item is processed.</param>
    /// <returns>Number of items processed.</returns>
    public async Task<int> ProcessLibraryAsync(
        SourceServerClient client,
        SyncDatabase database,
        LibraryMapping libraryMapping,
        List<string> enabledCategories,
        CancellationToken cancellationToken,
        Action? onItemProcessed = null)
    {
        var sourceLibraryId = Guid.Parse(libraryMapping.SourceLibraryId);

        var startIndex = 0;
        var processedItems = 0;
        var consecutiveErrors = 0;

        // Load existing metadata items for this library
        var existingItems = new Dictionary<string, Dictionary<string, MetadataSyncItem>>();
        try
        {
            var items = database.GetMetadataSyncItemsByLibrary(libraryMapping.SourceLibraryId);
            foreach (var item in items)
            {
                if (!existingItems.TryGetValue(item.SourceItemId, out var categoryDict))
                {
                    categoryDict = new Dictionary<string, MetadataSyncItem>();
                    existingItems[item.SourceItemId] = categoryDict;
                }

                categoryDict[item.PropertyCategory] = item;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load existing metadata items for library {Library}",
                libraryMapping.SourceLibraryName);
            return 0;
        }

        _logger.LogInformation(
            "Processing metadata for library {Library} with categories: {Categories}",
            libraryMapping.SourceLibraryName, string.Join(", ", enabledCategories));

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            BaseItemDtoQueryResult? result;
            try
            {
                // Get items with metadata from source server
                result = await client.GetLibraryItemsWithMetadataAsync(
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
                    ProcessMetadataItem(
                        database,
                        libraryMapping,
                        sourceItem,
                        enabledCategories,
                        existingItems);

                    processedItems++;
                    onItemProcessed?.Invoke();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to process metadata item {ItemId} ({Name})",
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
            "Processed {Count} metadata items for library {Library}",
            processedItems, libraryMapping.SourceLibraryName);

        return processedItems;
    }

    /// <summary>
    /// Processes a single metadata item from the source server.
    /// </summary>
    private void ProcessMetadataItem(
        SyncDatabase database,
        LibraryMapping libraryMapping,
        BaseItemDto sourceItem,
        List<string> enabledCategories,
        Dictionary<string, Dictionary<string, MetadataSyncItem>> existingItems)
    {
        var sourceItemId = sourceItem.Id!.Value.ToString("N", CultureInfo.InvariantCulture);
        var sourcePath = sourceItem.Path!;

        // Translate path to local path
        var localPath = PathUtilities.TranslatePath(sourcePath, libraryMapping.SourceRootPath, libraryMapping.LocalRootPath);

        // Try to find the local item by path
        var localItem = _libraryManager.FindByPath(localPath, isFolder: false);
        string? localItemId = localItem?.Id.ToString("N", CultureInfo.InvariantCulture);

        // Get existing items for this source item
        var existingForItem = existingItems.GetValueOrDefault(sourceItemId) ?? new Dictionary<string, MetadataSyncItem>();

        // Process each enabled category
        foreach (var category in enabledCategories)
        {
            var existingItem = existingForItem.GetValueOrDefault(category);

            if (existingItem != null)
            {
                // Update existing item
                UpdateMetadataItem(existingItem, sourceItem, localItem, localPath, localItemId, category);
                database.UpsertMetadataSyncItem(existingItem);
            }
            else
            {
                // Create new item
                var newItem = CreateMetadataItem(
                    libraryMapping,
                    sourceItem,
                    sourceItemId,
                    sourcePath,
                    localPath,
                    localItemId,
                    localItem,
                    category);

                database.UpsertMetadataSyncItem(newItem);
            }
        }
    }

    /// <summary>
    /// Creates a new metadata sync item.
    /// </summary>
    private MetadataSyncItem CreateMetadataItem(
        LibraryMapping libraryMapping,
        BaseItemDto sourceItem,
        string sourceItemId,
        string sourcePath,
        string localPath,
        string? localItemId,
        BaseItem? localItem,
        string propertyCategory)
    {
        var item = new MetadataSyncItem
        {
            // Library context
            SourceLibraryId = libraryMapping.SourceLibraryId,
            LocalLibraryId = libraryMapping.LocalLibraryId ?? string.Empty,

            // Item identification
            SourceItemId = sourceItemId,
            LocalItemId = localItemId,
            ItemName = sourceItem.Name ?? System.IO.Path.GetFileNameWithoutExtension(sourcePath),
            SourcePath = sourcePath,
            LocalPath = localPath,

            // Property category
            PropertyCategory = propertyCategory,

            // Tracking
            Status = BaseSyncStatus.Queued,
            StatusDate = DateTime.UtcNow
        };

        // Extract and set values based on category
        SetMetadataValues(item, sourceItem, localItem, propertyCategory);

        // Determine initial status based on whether there are changes
        if (string.IsNullOrEmpty(localItemId))
        {
            // Local item not found - can't sync yet
            item.Status = BaseSyncStatus.Queued;
        }
        else if (item.HasChanges)
        {
            // Has changes - queue for sync
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
    /// Updates an existing metadata sync item with current data.
    /// </summary>
    private void UpdateMetadataItem(
        MetadataSyncItem item,
        BaseItemDto sourceItem,
        BaseItem? localItem,
        string localPath,
        string? localItemId,
        string propertyCategory)
    {
        // Update identification
        item.LocalItemId = localItemId;
        item.LocalPath = localPath;
        item.ItemName = sourceItem.Name ?? item.ItemName;

        // Update values based on category
        SetMetadataValues(item, sourceItem, localItem, propertyCategory);

        // Preserve Ignored status - don't change status for ignored items
        if (item.Status == BaseSyncStatus.Ignored)
        {
            return;
        }

        // Update status if it was already synced but now has new changes
        if (item.Status == BaseSyncStatus.Synced && item.HasChanges)
        {
            item.Status = BaseSyncStatus.Queued;
            item.StatusDate = DateTime.UtcNow;
        }
        else if (item.Status == BaseSyncStatus.Queued && !item.HasChanges && !string.IsNullOrEmpty(localItemId))
        {
            // Previously queued but now in sync
            item.Status = BaseSyncStatus.Synced;
            item.StatusDate = DateTime.UtcNow;
            item.LastSyncTime = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Sets the source, local, and merged values based on the property category.
    /// </summary>
    private void SetMetadataValues(
        MetadataSyncItem item,
        BaseItemDto sourceItem,
        BaseItem? localItem,
        string propertyCategory)
    {
        switch (propertyCategory)
        {
            case MetadataPropertyCategory.Metadata:
                SetMetadataFieldValues(item, sourceItem, localItem);
                break;
            case MetadataPropertyCategory.Images:
                SetImagesValues(item, sourceItem, localItem);
                break;
            case MetadataPropertyCategory.People:
                SetPeopleValues(item, sourceItem, localItem);
                break;
        }
    }

    /// <summary>
    /// Sets metadata field values (title, overview, genres, tags, etc.).
    /// </summary>
    private void SetMetadataFieldValues(MetadataSyncItem item, BaseItemDto sourceItem, BaseItem? localItem)
    {
        // Extract relevant metadata fields from source
        var sourceMetadata = new Dictionary<string, object?>
        {
            ["Name"] = sourceItem.Name,
            ["OriginalTitle"] = sourceItem.OriginalTitle,
            ["SortName"] = sourceItem.SortName,
            ["ForcedSortName"] = sourceItem.ForcedSortName,
            ["Overview"] = sourceItem.Overview,
            ["OfficialRating"] = sourceItem.OfficialRating,
            ["CustomRating"] = sourceItem.CustomRating,
            ["CommunityRating"] = sourceItem.CommunityRating,
            ["CriticRating"] = sourceItem.CriticRating,
            ["PremiereDate"] = sourceItem.PremiereDate,
            ["ProductionYear"] = sourceItem.ProductionYear,
            ["Genres"] = sourceItem.Genres,
            ["Tags"] = sourceItem.Tags,
            ["Studios"] = sourceItem.Studios,
            ["ProductionLocations"] = sourceItem.ProductionLocations,
            ["ProviderIds"] = sourceItem.ProviderIds
        };

        item.SourceValue = JsonSerializer.Serialize(sourceMetadata);

        // Extract local metadata if item exists
        if (localItem != null)
        {
            var localMetadata = new Dictionary<string, object?>
            {
                ["Name"] = localItem.Name,
                ["OriginalTitle"] = localItem.OriginalTitle,
                ["SortName"] = localItem.SortName,
                ["ForcedSortName"] = localItem.ForcedSortName,
                ["Overview"] = localItem.Overview,
                ["OfficialRating"] = localItem.OfficialRating,
                ["CustomRating"] = localItem.CustomRating,
                ["CommunityRating"] = localItem.CommunityRating,
                ["CriticRating"] = localItem.CriticRating,
                ["PremiereDate"] = localItem.PremiereDate,
                ["ProductionYear"] = localItem.ProductionYear,
                ["Genres"] = localItem.Genres,
                ["Tags"] = localItem.Tags,
                ["Studios"] = localItem.Studios,
                ["ProductionLocations"] = localItem.ProductionLocations,
                ["ProviderIds"] = localItem.ProviderIds
            };

            item.LocalValue = JsonSerializer.Serialize(localMetadata);
        }

        // Source wins - merged value is always source value
        item.MergedValue = item.SourceValue;
    }

    /// <summary>
    /// Sets images values (hash-based comparison).
    /// </summary>
    private void SetImagesValues(MetadataSyncItem item, BaseItemDto sourceItem, BaseItem? localItem)
    {
        // For images, we track what image types exist on source and local
        // Use consistent format for both so comparison works properly
        var sourceImageInfo = new SortedDictionary<string, bool>
        {
            ["Primary"] = sourceItem.ImageTags?.AdditionalData?.ContainsKey("Primary") == true,
            ["Backdrop"] = sourceItem.BackdropImageTags?.Count > 0,
            ["Logo"] = sourceItem.ImageTags?.AdditionalData?.ContainsKey("Logo") == true,
            ["Thumb"] = sourceItem.ImageTags?.AdditionalData?.ContainsKey("Thumb") == true,
            ["Banner"] = sourceItem.ImageTags?.AdditionalData?.ContainsKey("Banner") == true,
            ["Art"] = sourceItem.ImageTags?.AdditionalData?.ContainsKey("Art") == true,
            ["Disc"] = sourceItem.ImageTags?.AdditionalData?.ContainsKey("Disc") == true
        };

        // Also include the actual image tags for tracking when images change on source
        var sourceTagInfo = new SortedDictionary<string, object?>
        {
            ["ImageTags"] = sourceItem.ImageTags?.AdditionalData,
            ["BackdropTags"] = sourceItem.BackdropImageTags
        };

        item.SourceValue = JsonSerializer.Serialize(sourceTagInfo);

        // Create a hash of source image tags - this changes when source images change
        item.SourceImagesHash = ComputeImageTagsHash(sourceItem.ImageTags?.AdditionalData, sourceItem.BackdropImageTags);

        if (localItem != null)
        {
            // For local, track what image types exist
            var localImageInfo = new SortedDictionary<string, bool>
            {
                ["Primary"] = localItem.HasImage(MediaBrowser.Model.Entities.ImageType.Primary),
                ["Backdrop"] = localItem.HasImage(MediaBrowser.Model.Entities.ImageType.Backdrop),
                ["Logo"] = localItem.HasImage(MediaBrowser.Model.Entities.ImageType.Logo),
                ["Thumb"] = localItem.HasImage(MediaBrowser.Model.Entities.ImageType.Thumb),
                ["Banner"] = localItem.HasImage(MediaBrowser.Model.Entities.ImageType.Banner),
                ["Art"] = localItem.HasImage(MediaBrowser.Model.Entities.ImageType.Art),
                ["Disc"] = localItem.HasImage(MediaBrowser.Model.Entities.ImageType.Disc)
            };

            item.LocalValue = JsonSerializer.Serialize(localImageInfo);

            // LocalImagesHash tracks what we've synced - only update if not already set
            // The sync task will update this after successful sync
            // If SyncedImagesHash matches SourceImagesHash, we're in sync
        }

        // Source wins
        item.MergedValue = item.SourceValue;
    }

    /// <summary>
    /// Sets people values (actors, directors, writers).
    /// </summary>
    private void SetPeopleValues(MetadataSyncItem item, BaseItemDto sourceItem, BaseItem? localItem)
    {
        // Serialize people from source
        if (sourceItem.People != null && sourceItem.People.Count > 0)
        {
            var sourcePeople = new List<Dictionary<string, object?>>();
            foreach (var person in sourceItem.People)
            {
                sourcePeople.Add(new Dictionary<string, object?>
                {
                    ["Name"] = person.Name,
                    ["Role"] = person.Role,
                    ["Type"] = person.Type
                });
            }

            item.SourceValue = JsonSerializer.Serialize(sourcePeople);
        }
        else
        {
            item.SourceValue = "[]";
        }

        // TODO: Get local people when applying
        // For now, we'll compare during the apply phase
        item.LocalValue = null;

        // Source wins
        item.MergedValue = item.SourceValue;
    }

    /// <summary>
    /// Computes a hash for image tags comparison.
    /// </summary>
    private static string? ComputeImageTagsHash(IDictionary<string, object>? imageTags, List<string>? backdropTags)
    {
        if ((imageTags == null || imageTags.Count == 0) && (backdropTags == null || backdropTags.Count == 0))
        {
            return null;
        }

        var combined = JsonSerializer.Serialize(new { imageTags, backdropTags });
        var hashBytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(combined));
        return Convert.ToHexString(hashBytes).ToLowerInvariant()[..32];
    }

    /// <summary>
    /// Computes a hash for local item images.
    /// </summary>
    private static string? ComputeLocalImageHash(BaseItem localItem)
    {
        var imageInfo = new
        {
            HasPrimary = localItem.HasImage(MediaBrowser.Model.Entities.ImageType.Primary),
            HasBackdrop = localItem.HasImage(MediaBrowser.Model.Entities.ImageType.Backdrop),
            HasLogo = localItem.HasImage(MediaBrowser.Model.Entities.ImageType.Logo),
            HasThumb = localItem.HasImage(MediaBrowser.Model.Entities.ImageType.Thumb)
        };

        var combined = JsonSerializer.Serialize(imageInfo);
        var hashBytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(combined));
        return Convert.ToHexString(hashBytes).ToLowerInvariant()[..32];
    }

    /// <summary>
    /// Gets the total count of items to process for progress tracking.
    /// </summary>
    public async Task<int> GetTotalItemCountAsync(
        SourceServerClient client,
        IEnumerable<LibraryMapping> libraryMappings,
        CancellationToken cancellationToken)
    {
        var totalCount = 0;

        foreach (var libraryMapping in libraryMappings)
        {
            if (!libraryMapping.IsEnabled || string.IsNullOrEmpty(libraryMapping.SourceLibraryId))
            {
                continue;
            }

            var sourceLibraryId = Guid.Parse(libraryMapping.SourceLibraryId);
            var count = await client.GetLibraryItemCountAsync(
                sourceLibraryId,
                cancellationToken).ConfigureAwait(false);

            totalCount += count;
        }

        return totalCount;
    }
}
