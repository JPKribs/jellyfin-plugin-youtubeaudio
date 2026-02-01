using System;
using Jellyfin.Plugin.ServerSync.Models.Common;

namespace Jellyfin.Plugin.ServerSync.Models.MetadataSync;

/// <summary>
/// Property categories for metadata sync items.
/// </summary>
public static class MetadataPropertyCategory
{
    /// <summary>
    /// Item metadata (title, overview, genres, tags, ratings, etc.).
    /// </summary>
    public const string Metadata = "Metadata";

    /// <summary>
    /// Item images (Primary, Backdrop, Logo, Thumb, etc.).
    /// </summary>
    public const string Images = "Images";

    /// <summary>
    /// People associated with the item (actors, directors, writers).
    /// </summary>
    public const string People = "People";
}

/// <summary>
/// Represents a metadata sync record for an item.
/// One record per property category (Metadata, Images, People) per item.
/// Matches items by file path using library mappings.
/// </summary>
public class MetadataSyncItem
{
    /// <summary>
    /// Gets or sets the unique database identifier.
    /// </summary>
    public long Id { get; set; }

    // ===== Item Identification =====

    /// <summary>
    /// Gets or sets the source library ID.
    /// </summary>
    public string SourceLibraryId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the local library ID.
    /// </summary>
    public string LocalLibraryId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source server item ID.
    /// </summary>
    public string SourceItemId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the local server item ID (if matched).
    /// </summary>
    public string? LocalItemId { get; set; }

    /// <summary>
    /// Gets or sets the item name for display.
    /// </summary>
    public string? ItemName { get; set; }

    /// <summary>
    /// Gets or sets the source file path (for matching).
    /// </summary>
    public string? SourcePath { get; set; }

    /// <summary>
    /// Gets or sets the local file path (translated from source).
    /// </summary>
    public string? LocalPath { get; set; }

    // ===== Property Category =====

    /// <summary>
    /// Gets or sets the property category (Metadata, Images, People).
    /// </summary>
    public string PropertyCategory { get; set; } = string.Empty;

    // ===== Values =====

    /// <summary>
    /// Gets or sets the source value (JSON representation of the metadata/images/people).
    /// </summary>
    public string? SourceValue { get; set; }

    /// <summary>
    /// Gets or sets the local value (JSON representation).
    /// </summary>
    public string? LocalValue { get; set; }

    /// <summary>
    /// Gets or sets the merged value (what will be applied - source wins).
    /// </summary>
    public string? MergedValue { get; set; }

    // ===== Image Hashes (for Images category) =====

    /// <summary>
    /// Gets or sets the source images hash (combined hash of all images).
    /// </summary>
    public string? SourceImagesHash { get; set; }

    /// <summary>
    /// Gets or sets the local images hash.
    /// </summary>
    public string? LocalImagesHash { get; set; }

    /// <summary>
    /// Gets or sets the last synced images hash.
    /// </summary>
    public string? SyncedImagesHash { get; set; }

    // ===== Sync Tracking =====

    /// <summary>
    /// Gets or sets the sync status.
    /// </summary>
    public BaseSyncStatus Status { get; set; }

    /// <summary>
    /// Gets or sets when the status was last changed.
    /// </summary>
    public DateTime StatusDate { get; set; }

    /// <summary>
    /// Gets or sets when the item was last synced.
    /// </summary>
    public DateTime? LastSyncTime { get; set; }

    /// <summary>
    /// Gets or sets the error message if status is Errored.
    /// </summary>
    public string? ErrorMessage { get; set; }

    // ===== Computed Properties =====

    /// <summary>
    /// Gets a value indicating whether there are changes to sync.
    /// For Images: compares source hash to synced hash (what we last synced).
    /// For others: compares source value to synced local value.
    /// </summary>
    public bool HasChanges
    {
        get
        {
            // No local item = can't sync yet
            if (string.IsNullOrEmpty(LocalItemId))
            {
                return false;
            }

            if (PropertyCategory == MetadataPropertyCategory.Images)
            {
                // For images, compare source hash to what we last synced
                // If SyncedImagesHash is null, we haven't synced yet
                if (!string.IsNullOrEmpty(SourceImagesHash))
                {
                    // If we've never synced, there are changes
                    if (string.IsNullOrEmpty(SyncedImagesHash))
                    {
                        return true;
                    }

                    // Compare source to what we synced
                    return !string.Equals(SourceImagesHash, SyncedImagesHash, StringComparison.OrdinalIgnoreCase);
                }

                return false;
            }

            // For Metadata and People, compare source value (what we want) to local value (what exists)
            // LocalValue is updated after successful sync to match what was applied
            return !JsonComparisonUtility.JsonEquals(SourceValue, LocalValue);
        }
    }

    /// <summary>
    /// Gets a display-friendly summary of the change.
    /// </summary>
    public string ChangesSummary
    {
        get
        {
            if (string.IsNullOrEmpty(LocalItemId))
            {
                return "Local item not found";
            }

            if (!HasChanges)
            {
                return "No changes";
            }

            if (PropertyCategory == MetadataPropertyCategory.Images)
            {
                return "Images differ";
            }

            // Count the number of differing properties
            var diffCount = JsonComparisonUtility.CountDifferences(MergedValue, LocalValue);
            if (diffCount == 0)
            {
                return "No changes";
            }

            return diffCount == 1 ? "1 difference" : $"{diffCount} differences";
        }
    }
}
