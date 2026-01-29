using System;

namespace Jellyfin.Plugin.ServerSync.Models.ContentSync;

/// <summary>
/// Detailed sync statistics for health dashboard.
/// </summary>
public class SyncStatsResponse
{
    public int TotalItems { get; set; }

    public int SyncedItems { get; set; }

    public int QueuedItems { get; set; }

    public int ErroredItems { get; set; }

    public int PendingItems { get; set; }

    public int PendingDownloadItems { get; set; }

    public int PendingReplacementItems { get; set; }

    public int PendingDeletionItems { get; set; }

    public int IgnoredItems { get; set; }

    public long TotalSyncedBytes { get; set; }

    public long TotalQueuedBytes { get; set; }

    public DateTime? LastSyncTime { get; set; }

    public DateTime? LastSyncStartTime { get; set; }

    public DateTime? LastSyncEndTime { get; set; }

    public long FreeDiskSpaceBytes { get; set; }

    public long MinimumRequiredBytes { get; set; }

    public bool HasSufficientDiskSpace { get; set; }
}
