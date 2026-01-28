using System;

namespace Jellyfin.Plugin.ServerSync.Models;

// SyncItem
// Represents a tracked sync item in the database.
public class SyncItem
{
    public long Id { get; set; }

    public string SourceLibraryId { get; set; } = string.Empty;

    public string LocalLibraryId { get; set; } = string.Empty;

    public string SourceItemId { get; set; } = string.Empty;

    public string SourcePath { get; set; } = string.Empty;

    public long SourceSize { get; set; }

    public DateTime SourceCreateDate { get; set; }

    public DateTime SourceModifyDate { get; set; }

    // SourceETag
    // ETag from source server, derived from file's DateModified.
    // Used to detect actual file changes without re-downloading.
    public string? SourceETag { get; set; }

    public string? LocalItemId { get; set; }

    public string? LocalPath { get; set; }

    public DateTime StatusDate { get; set; }

    public SyncStatus Status { get; set; }

    public DateTime? LastSyncTime { get; set; }

    public string? ErrorMessage { get; set; }

    public int RetryCount { get; set; }
}
