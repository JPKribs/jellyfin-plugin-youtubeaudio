using System;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Utilities;

namespace Jellyfin.Plugin.ServerSync.Models.MetadataSync;

/// <summary>
/// Represents a metadata sync record for an item.
/// One record per item, containing all three categories (Metadata, Images, People).
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

    // ===== Metadata Category =====

    /// <summary>
    /// Gets or sets the source metadata value (JSON).
    /// </summary>
    public string? SourceMetadataValue { get; set; }

    /// <summary>
    /// Gets or sets the local metadata value (JSON).
    /// </summary>
    public string? LocalMetadataValue { get; set; }

    // ===== Images Category =====

    /// <summary>
    /// Gets or sets the source images value (JSON with image tags).
    /// </summary>
    public string? SourceImagesValue { get; set; }

    /// <summary>
    /// Gets or sets the local images value (JSON).
    /// </summary>
    public string? LocalImagesValue { get; set; }

    /// <summary>
    /// Gets or sets the source images hash (for change detection).
    /// </summary>
    public string? SourceImagesHash { get; set; }

    /// <summary>
    /// Gets or sets the last synced images hash.
    /// </summary>
    public string? SyncedImagesHash { get; set; }

    // ===== People Category =====

    /// <summary>
    /// Gets or sets the source people value (JSON array of {Name, Role, Type}).
    /// </summary>
    public string? SourcePeopleValue { get; set; }

    /// <summary>
    /// Gets or sets the local people value (JSON array of {Name, Role, Type}).
    /// </summary>
    public string? LocalPeopleValue { get; set; }

    // ===== Studios Category =====

    /// <summary>
    /// Gets or sets the source studios value (JSON array of studio names).
    /// </summary>
    public string? SourceStudiosValue { get; set; }

    /// <summary>
    /// Gets or sets the local studios value (JSON array of studio names).
    /// </summary>
    public string? LocalStudiosValue { get; set; }

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
    /// Gets a value indicating whether metadata has changes.
    /// </summary>
    public bool HasMetadataChanges
    {
        get
        {
            if (string.IsNullOrEmpty(LocalItemId) || string.IsNullOrEmpty(SourceMetadataValue))
            {
                return false;
            }

            return !JsonComparisonUtility.JsonEquals(SourceMetadataValue, LocalMetadataValue);
        }
    }

    /// <summary>
    /// Gets a value indicating whether images have changes.
    /// </summary>
    public bool HasImagesChanges
    {
        get
        {
            if (string.IsNullOrEmpty(LocalItemId) || string.IsNullOrEmpty(SourceImagesHash))
            {
                return false;
            }

            // If we've never synced images, there are changes
            if (string.IsNullOrEmpty(SyncedImagesHash))
            {
                return true;
            }

            return !string.Equals(SourceImagesHash, SyncedImagesHash, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Gets a value indicating whether people have changes.
    /// Compares by Name, Role, and Type (not GUID).
    /// </summary>
    public bool HasPeopleChanges
    {
        get
        {
            if (string.IsNullOrEmpty(LocalItemId) || string.IsNullOrEmpty(SourcePeopleValue))
            {
                return false;
            }

            return !JsonComparisonUtility.JsonEquals(SourcePeopleValue, LocalPeopleValue);
        }
    }

    /// <summary>
    /// Gets a value indicating whether studios have changes.
    /// Compares by studio name only. Only flags changes if source has studios to sync.
    /// </summary>
    public bool HasStudiosChanges
    {
        get
        {
            // No local item to sync to
            if (string.IsNullOrEmpty(LocalItemId))
            {
                return false;
            }

            // No source studios data, or source has empty array - nothing to sync
            if (string.IsNullOrEmpty(SourceStudiosValue) || SourceStudiosValue == "[]")
            {
                return false;
            }

            return !JsonComparisonUtility.JsonEquals(SourceStudiosValue, LocalStudiosValue);
        }
    }

    /// <summary>
    /// Gets a value indicating whether there are any changes to sync.
    /// </summary>
    public bool HasChanges => HasMetadataChanges || HasImagesChanges || HasPeopleChanges || HasStudiosChanges;

    /// <summary>
    /// Gets a display-friendly summary of the changes.
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

            var changes = new System.Collections.Generic.List<string>();

            if (HasMetadataChanges)
            {
                var diffCount = JsonComparisonUtility.CountDifferences(SourceMetadataValue, LocalMetadataValue);
                changes.Add(diffCount == 1 ? "1 metadata field" : $"{diffCount} metadata fields");
            }

            if (HasImagesChanges)
            {
                changes.Add("Images");
            }

            if (HasPeopleChanges)
            {
                changes.Add("People");
            }

            if (HasStudiosChanges)
            {
                changes.Add("Studios");
            }

            return string.Join(", ", changes);
        }
    }
}
