using System;

namespace Jellyfin.Plugin.ServerSync.Models.ContentSync;

/// <summary>
/// Represents a tracked sync item in the database.
/// </summary>
public class SyncItem
{
    public long Id { get; set; }

    public string SourceLibraryId { get; set; } = string.Empty;

    public string LocalLibraryId { get; set; } = string.Empty;

    public string SourceItemId { get; set; } = string.Empty;

    public string SourcePath { get; set; } = string.Empty;

    public long SourceSize { get; set; }

    public DateTime SourceCreateDate { get; set; }

    /// <summary>ETag from source server used to detect file changes without re-downloading.</summary>
    public string? SourceETag { get; set; }

    public string? LocalItemId { get; set; }

    public string? LocalPath { get; set; }

    public DateTime StatusDate { get; set; }

    public SyncStatus Status { get; set; }

    /// <summary>Specifies the pending operation type; null when Status is not Pending.</summary>
    public PendingType? PendingType { get; set; }

    public DateTime? LastSyncTime { get; set; }

    public string? ErrorMessage { get; set; }

    public int RetryCount { get; set; }
}
