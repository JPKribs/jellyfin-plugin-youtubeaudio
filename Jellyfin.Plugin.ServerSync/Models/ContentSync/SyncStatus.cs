using Jellyfin.Plugin.ServerSync.Models.Common;

namespace Jellyfin.Plugin.ServerSync.Models.ContentSync;

/// <summary>
/// Represents the sync status of a tracked content item.
/// Values 0-4 match <see cref="BaseSyncStatus"/> for consistency across sync modules.
/// </summary>
public enum SyncStatus
{
    /// <summary>Item awaiting approval; see PendingType for specific operation.</summary>
    /// <remarks>Matches <see cref="BaseSyncStatus.Pending"/>.</remarks>
    Pending = 0,

    /// <summary>Item approved and queued for download/sync.</summary>
    /// <remarks>Matches <see cref="BaseSyncStatus.Queued"/>.</remarks>
    Queued = 1,

    /// <summary>Item successfully synced to local server.</summary>
    /// <remarks>Matches <see cref="BaseSyncStatus.Synced"/>.</remarks>
    Synced = 2,

    /// <summary>Item encountered an error during sync.</summary>
    /// <remarks>Matches <see cref="BaseSyncStatus.Errored"/>.</remarks>
    Errored = 3,

    /// <summary>Item explicitly ignored by user.</summary>
    /// <remarks>Matches <see cref="BaseSyncStatus.Ignored"/>.</remarks>
    Ignored = 4,

    /// <summary>Item queued for deletion from local server.</summary>
    /// <remarks>Content sync specific - not in BaseSyncStatus.</remarks>
    Deleting = 5
}
