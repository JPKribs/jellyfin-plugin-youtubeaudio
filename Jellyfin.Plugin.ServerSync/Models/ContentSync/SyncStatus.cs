namespace Jellyfin.Plugin.ServerSync.Models.ContentSync;

/// <summary>
/// Represents the sync status of a tracked item.
/// </summary>
public enum SyncStatus
{
    /// <summary>Item awaiting approval; see PendingType for specific operation.</summary>
    Pending = 0,

    /// <summary>Item approved and queued for download/sync.</summary>
    Queued = 1,

    /// <summary>Item successfully synced to local server.</summary>
    Synced = 2,

    /// <summary>Item encountered an error during sync.</summary>
    Errored = 3,

    /// <summary>Item explicitly ignored by user.</summary>
    Ignored = 4,

    /// <summary>Item queued for deletion from local server.</summary>
    Deleting = 5
}
