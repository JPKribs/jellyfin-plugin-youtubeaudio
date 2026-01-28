namespace Jellyfin.Plugin.ServerSync.Models;

// SyncStatus
// Represents the sync status of a tracked item.
public enum SyncStatus
{
    Pending = 0,
    Queued = 1,
    Synced = 2,
    Errored = 3,
    Ignored = 4,
    PendingDeletion = 5
}
