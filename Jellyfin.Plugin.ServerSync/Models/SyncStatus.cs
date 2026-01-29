namespace Jellyfin.Plugin.ServerSync.Models;

// SyncStatus
// Represents the sync status of a tracked item.
public enum SyncStatus
{
    // New item awaiting approval for download.
    Pending = 0,

    // Item approved and queued for download/sync.
    Queued = 1,

    // Item successfully synced to local server.
    Synced = 2,

    // Item encountered an error during sync.
    Errored = 3,

    // Item explicitly ignored by user.
    Ignored = 4,

    // Item awaiting approval for deletion (no longer exists on source).
    PendingDeletion = 5,

    // Existing item awaiting approval for replacement with updated version.
    PendingReplacement = 6
}
