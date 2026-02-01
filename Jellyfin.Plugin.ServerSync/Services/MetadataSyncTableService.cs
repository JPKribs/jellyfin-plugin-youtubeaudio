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
    /// <param name="syncMetadata">Whether to sync metadata fields.</param>
    /// <param name="syncImages">Whether to sync images.</param>
    /// <param name="syncPeople">Whether to sync people.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="onItemProcessed">Optional callback after each item is processed.</param>
    /// <returns>Number of items processed.</returns>
    public async Task<int> ProcessLibraryAsync(
        SourceServerClient client,
        SyncDatabase database,
        LibraryMapping libraryMapping,
        bool syncMetadata,
        bool syncImages,
        bool syncPeople,
        CancellationToken cancellationToken,
        Action? onItemProcessed = null)
    {
        var sourceLibraryId = Guid.Parse(libraryMapping.SourceLibraryId);

        var startIndex = 0;
        var processedItems = 0;
        var consecutiveErrors = 0;

        // Load existing metadata items for this library (one record per item now)
        var existingItems = new Dictionary<string, MetadataSyncItem>();
        try
        {
            var items = database.GetMetadataSyncItemsByLibrary(libraryMapping.SourceLibraryId);
            foreach (var item in items)
            {
                existingItems[item.SourceItemId] = item;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load existing metadata items for library {Library}",
                libraryMapping.SourceLibraryName);
            return 0;
        }

        _logger.LogInformation(
            "Processing metadata for library {Library} (Metadata: {Metadata}, Images: {Images}, People: {People})",
            libraryMapping.SourceLibraryName, syncMetadata, syncImages, syncPeople);

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
                        syncMetadata,
                        syncImages,
                        syncPeople,
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
        bool syncMetadata,
        bool syncImages,
        bool syncPeople,
        Dictionary<string, MetadataSyncItem> existingItems)
    {
        var sourceItemId = sourceItem.Id!.Value.ToString("N", CultureInfo.InvariantCulture);
        var sourcePath = sourceItem.Path!;

        // Translate path to local path
        var localPath = PathUtilities.TranslatePath(sourcePath, libraryMapping.SourceRootPath, libraryMapping.LocalRootPath);

        // Try to find the local item by path
        var localItem = _libraryManager.FindByPath(localPath, isFolder: false);
        string? localItemId = localItem?.Id.ToString("N", CultureInfo.InvariantCulture);

        // Get existing item for this source item
        var existingItem = existingItems.GetValueOrDefault(sourceItemId);

        if (existingItem != null)
        {
            // Update existing item with all category values
            UpdateMetadataItem(existingItem, sourceItem, localItem, localPath, localItemId, syncMetadata, syncImages, syncPeople);
            database.UpsertMetadataSyncItem(existingItem);
        }
        else
        {
            // Create new item with all category values
            var newItem = CreateMetadataItem(
                libraryMapping,
                sourceItem,
                sourceItemId,
                sourcePath,
                localPath,
                localItemId,
                localItem,
                syncMetadata,
                syncImages,
                syncPeople);

            database.UpsertMetadataSyncItem(newItem);
        }
    }

    /// <summary>
    /// Creates a new metadata sync item with all category values.
    /// </summary>
    private MetadataSyncItem CreateMetadataItem(
        LibraryMapping libraryMapping,
        BaseItemDto sourceItem,
        string sourceItemId,
        string sourcePath,
        string localPath,
        string? localItemId,
        BaseItem? localItem,
        bool syncMetadata,
        bool syncImages,
        bool syncPeople)
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

            // Tracking
            Status = BaseSyncStatus.Queued,
            StatusDate = DateTime.UtcNow
        };

        // Set values for each enabled category
        if (syncMetadata)
        {
            SetMetadataFieldValues(item, sourceItem, localItem);
        }

        if (syncImages)
        {
            SetImagesValues(item, sourceItem, localItem);
        }

        if (syncPeople)
        {
            SetPeopleValues(item, sourceItem, localItem);
        }

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
        bool syncMetadata,
        bool syncImages,
        bool syncPeople)
    {
        // Update identification
        item.LocalItemId = localItemId;
        item.LocalPath = localPath;
        item.ItemName = sourceItem.Name ?? item.ItemName;

        // Update values for each enabled category
        if (syncMetadata)
        {
            SetMetadataFieldValues(item, sourceItem, localItem);
        }
        else
        {
            // Clear metadata if disabled
            item.SourceMetadataValue = null;
            item.LocalMetadataValue = null;
        }

        if (syncImages)
        {
            SetImagesValues(item, sourceItem, localItem);
        }
        else
        {
            // Clear images if disabled
            item.SourceImagesValue = null;
            item.LocalImagesValue = null;
            item.SourceImagesHash = null;
        }

        if (syncPeople)
        {
            SetPeopleValues(item, sourceItem, localItem);
        }
        else
        {
            // Clear people if disabled
            item.SourcePeopleValue = null;
            item.LocalPeopleValue = null;
        }

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

        item.SourceMetadataValue = JsonSerializer.Serialize(sourceMetadata);

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

            item.LocalMetadataValue = JsonSerializer.Serialize(localMetadata);
        }
    }

    /// <summary>
    /// Sets images values (hash-based comparison).
    /// </summary>
    private void SetImagesValues(MetadataSyncItem item, BaseItemDto sourceItem, BaseItem? localItem)
    {
        // Extract image tags as a simple dictionary of image type -> tag string
        var imageTags = new Dictionary<string, string>();
        if (sourceItem.ImageTags?.AdditionalData != null)
        {
            foreach (var kvp in sourceItem.ImageTags.AdditionalData)
            {
                if (kvp.Value != null)
                {
                    imageTags[kvp.Key] = kvp.Value.ToString() ?? string.Empty;
                }
            }
        }

        var backdropTags = sourceItem.BackdropImageTags ?? new List<string>();

        // Only store if there are actual images
        if (imageTags.Count > 0 || backdropTags.Count > 0)
        {
            var sourceTagInfo = new SortedDictionary<string, object?>
            {
                ["ImageTags"] = imageTags,
                ["BackdropTags"] = backdropTags
            };

            item.SourceImagesValue = JsonSerializer.Serialize(sourceTagInfo);

            // Create a hash of source image tags - this changes when source images change
            item.SourceImagesHash = ComputeImageTagsHash(imageTags, backdropTags);
        }
        else
        {
            // No images on source - clear values
            item.SourceImagesValue = null;
            item.SourceImagesHash = null;
        }

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

            item.LocalImagesValue = JsonSerializer.Serialize(localImageInfo);
        }
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

            item.SourcePeopleValue = JsonSerializer.Serialize(sourcePeople);
        }
        else
        {
            item.SourcePeopleValue = "[]";
        }

        // TODO: Get local people when applying
        // For now, we'll compare during the apply phase
        item.LocalPeopleValue = null;
    }

    /// <summary>
    /// Computes a hash for image tags comparison.
    /// </summary>
    private static string? ComputeImageTagsHash(Dictionary<string, string>? imageTags, List<string>? backdropTags)
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
