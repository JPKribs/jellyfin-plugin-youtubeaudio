using System;

namespace Jellyfin.Plugin.ServerSync.Models.MetadataSync;

/// <summary>
/// Metadata sync item information for API responses.
/// </summary>
public class MetadataSyncItemDto
{
    /// <summary>
    /// Gets or sets the database ID.
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
    /// Gets or sets the source library name (for display).
    /// </summary>
    public string? SourceLibraryName { get; set; }

    /// <summary>
    /// Gets or sets the local library name (for display).
    /// </summary>
    public string? LocalLibraryName { get; set; }

    /// <summary>
    /// Gets or sets the source item ID.
    /// </summary>
    public string SourceItemId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the local item ID (if matched).
    /// </summary>
    public string? LocalItemId { get; set; }

    /// <summary>
    /// Gets or sets the item name for display.
    /// </summary>
    public string? ItemName { get; set; }

    /// <summary>
    /// Gets or sets the source file path.
    /// </summary>
    public string? SourcePath { get; set; }

    /// <summary>
    /// Gets or sets the local file path.
    /// </summary>
    public string? LocalPath { get; set; }

    // ===== Property Category =====

    /// <summary>
    /// Gets or sets the property category (Metadata, Images, People).
    /// </summary>
    public string PropertyCategory { get; set; } = string.Empty;

    // ===== Change Detection =====

    /// <summary>
    /// Gets or sets whether there are changes to sync.
    /// </summary>
    public bool HasChanges { get; set; }

    /// <summary>
    /// Gets or sets a summary of the changes.
    /// </summary>
    public string? ChangesSummary { get; set; }

    // ===== Sync Status =====

    /// <summary>
    /// Gets or sets the sync status.
    /// </summary>
    public string Status { get; set; } = string.Empty;

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
}
