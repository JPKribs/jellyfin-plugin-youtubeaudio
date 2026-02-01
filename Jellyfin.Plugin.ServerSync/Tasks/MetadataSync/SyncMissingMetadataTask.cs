using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Jellyfin.Plugin.ServerSync.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks;

/// <summary>
/// Scheduled task to apply queued metadata sync changes to the local server.
/// </summary>
public class SyncMissingMetadataTask : IScheduledTask
{
    private readonly ILogger<SyncMissingMetadataTask> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncMissingMetadataTask"/> class.
    /// </summary>
    public SyncMissingMetadataTask(
        ILogger<SyncMissingMetadataTask> logger,
        ILibraryManager libraryManager,
        IProviderManager providerManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _providerManager = providerManager;
    }

    /// <inheritdoc />
    public string Name => "Sync Metadata";

    /// <inheritdoc />
    public string Key => "ServerSyncMissingMetadata";

    /// <inheritdoc />
    public string Description => "Applies queued metadata changes from the source server to the local server.";

    /// <inheritdoc />
    public string Category => "Metadata Sync";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return;
        }

        var config = plugin.Configuration;

        // Check if metadata sync is enabled
        if (!config.EnableMetadataSync)
        {
            return;
        }

        _logger.LogInformation("Starting metadata sync application");

        var database = plugin.Database;

        // Get all queued metadata items
        var queuedItems = database.GetMetadataSyncItemsByStatus(BaseSyncStatus.Queued);
        var totalItems = queuedItems.Count;

        if (totalItems == 0)
        {
            _logger.LogInformation("No queued metadata items to sync");
            progress.Report(100);
            return;
        }

        _logger.LogInformation("Processing {Count} queued metadata items", totalItems);

        var processedCount = 0;
        var successCount = 0;
        var errorCount = 0;

        foreach (var item in queuedItems)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                var success = await SyncMetadataItemAsync(item, database, cancellationToken).ConfigureAwait(false);

                if (success)
                {
                    successCount++;
                }
                else
                {
                    errorCount++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to sync metadata item {ItemName} ({Category})",
                    item.ItemName, item.PropertyCategory);
                item.Status = BaseSyncStatus.Errored;
                item.ErrorMessage = ex.Message;
                item.StatusDate = DateTime.UtcNow;
                database.UpsertMetadataSyncItem(item);
                errorCount++;
            }

            processedCount++;
            progress.Report((double)processedCount / totalItems * 100);
        }

        _logger.LogInformation(
            "Metadata sync completed: {Success} succeeded, {Error} failed out of {Total}",
            successCount, errorCount, totalItems);

        progress.Report(100);
    }

    /// <summary>
    /// Syncs a single metadata item to the local server.
    /// </summary>
    private async Task<bool> SyncMetadataItemAsync(
        MetadataSyncItem item,
        SyncDatabase database,
        CancellationToken cancellationToken)
    {
        // Validate we have a local item ID
        if (string.IsNullOrEmpty(item.LocalItemId))
        {
            _logger.LogWarning("Cannot sync metadata for {ItemName}: local item not found", item.ItemName);
            item.Status = BaseSyncStatus.Errored;
            item.ErrorMessage = "Local item not found";
            item.StatusDate = DateTime.UtcNow;
            database.UpsertMetadataSyncItem(item);
            return false;
        }

        // Parse local item ID
        if (!Guid.TryParse(item.LocalItemId, out var localItemId))
        {
            _logger.LogWarning("Cannot sync metadata for {ItemName}: invalid item ID", item.ItemName);
            item.Status = BaseSyncStatus.Errored;
            item.ErrorMessage = "Invalid item ID";
            item.StatusDate = DateTime.UtcNow;
            database.UpsertMetadataSyncItem(item);
            return false;
        }

        // Get the local item
        var localItem = _libraryManager.GetItemById(localItemId);
        if (localItem == null)
        {
            _logger.LogWarning("Cannot sync metadata for {ItemName}: local item not found in library", item.ItemName);
            item.Status = BaseSyncStatus.Errored;
            item.ErrorMessage = "Local item not found in library";
            item.StatusDate = DateTime.UtcNow;
            database.UpsertMetadataSyncItem(item);
            return false;
        }

        var success = false;

        switch (item.PropertyCategory)
        {
            case MetadataPropertyCategory.Metadata:
                success = await ApplyMetadataAsync(localItem, item, cancellationToken).ConfigureAwait(false);
                break;
            case MetadataPropertyCategory.Images:
                success = await ApplyImagesAsync(localItem, item, cancellationToken).ConfigureAwait(false);
                break;
            case MetadataPropertyCategory.People:
                success = await ApplyPeopleAsync(localItem, item, cancellationToken).ConfigureAwait(false);
                break;
            default:
                _logger.LogWarning("Unknown property category {Category} for {ItemName}",
                    item.PropertyCategory, item.ItemName);
                success = false;
                break;
        }

        if (success)
        {
            _logger.LogDebug("Synced {Category} for {ItemName}", item.PropertyCategory, item.ItemName);

            // Update item status
            item.Status = BaseSyncStatus.Synced;
            item.LastSyncTime = DateTime.UtcNow;
            item.StatusDate = DateTime.UtcNow;
            item.ErrorMessage = null;

            // Update local value to match source value (what we just applied)
            // This is used for comparison on next refresh
            item.LocalValue = item.SourceValue;

            if (item.PropertyCategory == MetadataPropertyCategory.Images)
            {
                // Track what source hash we synced - used for comparison on refresh
                item.SyncedImagesHash = item.SourceImagesHash;
            }

            database.UpsertMetadataSyncItem(item);
            return true;
        }
        else
        {
            _logger.LogWarning("Failed to sync {Category} for {ItemName}", item.PropertyCategory, item.ItemName);
            item.Status = BaseSyncStatus.Errored;
            item.ErrorMessage = "Failed to apply metadata";
            item.StatusDate = DateTime.UtcNow;
            database.UpsertMetadataSyncItem(item);
            return false;
        }
    }

    /// <summary>
    /// Applies metadata fields to a local item.
    /// </summary>
    private async Task<bool> ApplyMetadataAsync(
        MediaBrowser.Controller.Entities.BaseItem localItem,
        MetadataSyncItem item,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(item.MergedValue))
        {
            return false;
        }

        try
        {
            var metadata = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(item.MergedValue);
            if (metadata == null)
            {
                return false;
            }

            var hasChanges = false;

            // Apply each metadata field
            if (metadata.TryGetValue("Name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String)
            {
                var name = nameValue.GetString();
                if (!string.IsNullOrEmpty(name) && localItem.Name != name)
                {
                    localItem.Name = name;
                    hasChanges = true;
                }
            }

            if (metadata.TryGetValue("OriginalTitle", out var origTitleValue) && origTitleValue.ValueKind == JsonValueKind.String)
            {
                var origTitle = origTitleValue.GetString();
                if (localItem.OriginalTitle != origTitle)
                {
                    localItem.OriginalTitle = origTitle;
                    hasChanges = true;
                }
            }

            if (metadata.TryGetValue("Overview", out var overviewValue) && overviewValue.ValueKind == JsonValueKind.String)
            {
                var overview = overviewValue.GetString();
                if (localItem.Overview != overview)
                {
                    localItem.Overview = overview;
                    hasChanges = true;
                }
            }

            if (metadata.TryGetValue("OfficialRating", out var ratingValue) && ratingValue.ValueKind == JsonValueKind.String)
            {
                var rating = ratingValue.GetString();
                if (localItem.OfficialRating != rating)
                {
                    localItem.OfficialRating = rating;
                    hasChanges = true;
                }
            }

            if (metadata.TryGetValue("CommunityRating", out var commRatingValue))
            {
                float? commRating = commRatingValue.ValueKind == JsonValueKind.Number
                    ? commRatingValue.GetSingle()
                    : null;
                if (localItem.CommunityRating != commRating)
                {
                    localItem.CommunityRating = commRating;
                    hasChanges = true;
                }
            }

            if (metadata.TryGetValue("CriticRating", out var criticRatingValue))
            {
                float? criticRating = criticRatingValue.ValueKind == JsonValueKind.Number
                    ? criticRatingValue.GetSingle()
                    : null;
                if (localItem.CriticRating != criticRating)
                {
                    localItem.CriticRating = criticRating;
                    hasChanges = true;
                }
            }

            if (metadata.TryGetValue("ProductionYear", out var yearValue))
            {
                int? year = yearValue.ValueKind == JsonValueKind.Number
                    ? yearValue.GetInt32()
                    : null;
                if (localItem.ProductionYear != year)
                {
                    localItem.ProductionYear = year;
                    hasChanges = true;
                }
            }

            if (metadata.TryGetValue("Genres", out var genresValue) && genresValue.ValueKind == JsonValueKind.Array)
            {
                var genres = new List<string>();
                foreach (var genre in genresValue.EnumerateArray())
                {
                    if (genre.ValueKind == JsonValueKind.String)
                    {
                        genres.Add(genre.GetString()!);
                    }
                }

                localItem.Genres = genres.ToArray();
                hasChanges = true;
            }

            if (metadata.TryGetValue("Tags", out var tagsValue) && tagsValue.ValueKind == JsonValueKind.Array)
            {
                var tags = new List<string>();
                foreach (var tag in tagsValue.EnumerateArray())
                {
                    if (tag.ValueKind == JsonValueKind.String)
                    {
                        tags.Add(tag.GetString()!);
                    }
                }

                localItem.Tags = tags.ToArray();
                hasChanges = true;
            }

            if (metadata.TryGetValue("ProviderIds", out var providerIdsValue) && providerIdsValue.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in providerIdsValue.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var providerValue = prop.Value.GetString();
                        if (!string.IsNullOrEmpty(providerValue))
                        {
                            localItem.SetProviderId(prop.Name, providerValue);
                            hasChanges = true;
                        }
                    }
                }
            }

            if (hasChanges)
            {
                await localItem.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply metadata for {ItemName}", item.ItemName);
            return false;
        }
    }

    /// <summary>
    /// Applies images from the source server to a local item.
    /// </summary>
    private async Task<bool> ApplyImagesAsync(
        MediaBrowser.Controller.Entities.BaseItem localItem,
        MetadataSyncItem item,
        CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return false;
        }

        var config = plugin.Configuration;
        if (string.IsNullOrEmpty(config.SourceServerUrl) || string.IsNullOrEmpty(config.SourceServerApiKey))
        {
            _logger.LogWarning("Source server not configured, cannot sync images");
            return false;
        }

        try
        {
            // Parse source image info to determine what images exist
            if (string.IsNullOrEmpty(item.SourceValue))
            {
                return true; // No images on source
            }

            var sourceImageInfo = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(item.SourceValue);
            if (sourceImageInfo == null)
            {
                return true;
            }

            // Get image tags from source
            Dictionary<string, string>? imageTags = null;
            List<string>? backdropTags = null;

            if (sourceImageInfo.TryGetValue("ImageTags", out var imageTagsElement) && imageTagsElement.ValueKind == JsonValueKind.Object)
            {
                imageTags = new Dictionary<string, string>();
                foreach (var prop in imageTagsElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        imageTags[prop.Name] = prop.Value.GetString() ?? string.Empty;
                    }
                }
            }

            if (sourceImageInfo.TryGetValue("BackdropTags", out var backdropElement) && backdropElement.ValueKind == JsonValueKind.Array)
            {
                backdropTags = new List<string>();
                foreach (var tag in backdropElement.EnumerateArray())
                {
                    if (tag.ValueKind == JsonValueKind.String)
                    {
                        backdropTags.Add(tag.GetString() ?? string.Empty);
                    }
                }
            }

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("X-Emby-Token", config.SourceServerApiKey);

            var baseUrl = config.SourceServerUrl.TrimEnd('/');
            var sourceItemId = item.SourceItemId;

            // Download and save each image type
            var imageTypes = new[] { "Primary", "Logo", "Thumb", "Banner", "Art", "Disc" };

            foreach (var imageType in imageTypes)
            {
                if (imageTags != null && imageTags.ContainsKey(imageType))
                {
                    var imageUrl = $"{baseUrl}/Items/{sourceItemId}/Images/{imageType}";
                    await DownloadAndSaveImageAsync(httpClient, imageUrl, localItem, imageType, 0, cancellationToken).ConfigureAwait(false);
                }
            }

            // Download backdrops
            if (backdropTags != null)
            {
                for (int i = 0; i < backdropTags.Count; i++)
                {
                    var imageUrl = $"{baseUrl}/Items/{sourceItemId}/Images/Backdrop/{i}";
                    await DownloadAndSaveImageAsync(httpClient, imageUrl, localItem, "Backdrop", i, cancellationToken).ConfigureAwait(false);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply images for {ItemName}", item.ItemName);
            return false;
        }
    }

    /// <summary>
    /// Downloads an image from the source server and saves it to the local item.
    /// </summary>
    private async Task DownloadAndSaveImageAsync(
        HttpClient httpClient,
        string imageUrl,
        MediaBrowser.Controller.Entities.BaseItem localItem,
        string imageTypeName,
        int imageIndex,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!Enum.TryParse<ImageType>(imageTypeName, out var imageType))
            {
                _logger.LogWarning("Unknown image type: {ImageType}", imageTypeName);
                return;
            }

            _logger.LogDebug("Downloading {ImageType} image for {ItemName} from {Url}",
                imageTypeName, localItem.Name, imageUrl);

            using var response = await httpClient.GetAsync(imageUrl, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to download image {ImageType} for {ItemName}: {StatusCode}",
                    imageTypeName, localItem.Name, response.StatusCode);
                return;
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            // Read into memory stream so we can use it
            using var memoryStream = new MemoryStream();
            await stream.CopyToAsync(memoryStream, cancellationToken).ConfigureAwait(false);
            memoryStream.Position = 0;

            // Save the image using the provider manager
            await _providerManager.SaveImage(
                localItem,
                memoryStream,
                contentType,
                imageType,
                imageIndex,
                cancellationToken).ConfigureAwait(false);

            _logger.LogDebug("Saved {ImageType} image for {ItemName}", imageTypeName, localItem.Name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error downloading/saving {ImageType} image for {ItemName}",
                imageTypeName, localItem.Name);
        }
    }

    /// <summary>
    /// Applies people to a local item.
    /// </summary>
    private async Task<bool> ApplyPeopleAsync(
        MediaBrowser.Controller.Entities.BaseItem localItem,
        MetadataSyncItem item,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(item.MergedValue))
        {
            return false;
        }

        try
        {
            var peopleList = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(item.MergedValue);
            if (peopleList == null)
            {
                return false;
            }

            var people = new List<MediaBrowser.Controller.Entities.PersonInfo>();
            foreach (var person in peopleList)
            {
                var personInfo = new MediaBrowser.Controller.Entities.PersonInfo();

                if (person.TryGetValue("Name", out var name))
                {
                    personInfo.Name = name;
                }

                if (person.TryGetValue("Role", out var role))
                {
                    personInfo.Role = role;
                }

                if (person.TryGetValue("Type", out var type) && Enum.TryParse<Jellyfin.Data.Enums.PersonKind>(type, out var personKind))
                {
                    personInfo.Type = personKind;
                }

                if (!string.IsNullOrEmpty(personInfo.Name))
                {
                    people.Add(personInfo);
                }
            }

            _libraryManager.UpdatePeople(localItem, people);
            await localItem.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply people for {ItemName}", item.ItemName);
            return false;
        }
    }

    /// <inheritdoc />
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
