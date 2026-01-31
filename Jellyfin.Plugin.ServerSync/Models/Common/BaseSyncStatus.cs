namespace Jellyfin.Plugin.ServerSync.Models.Common;

/// <summary>
/// Base sync status values shared across all sync modules.
/// </summary>
public enum BaseSyncStatus
{
    /// <summary>
    /// Item is pending review or processing.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// Item is queued for sync.
    /// </summary>
    Queued = 1,

    /// <summary>
    /// Item has been synced successfully.
    /// </summary>
    Synced = 2,

    /// <summary>
    /// Item sync failed with an error.
    /// </summary>
    Errored = 3,

    /// <summary>
    /// Item is ignored and will not be synced.
    /// </summary>
    Ignored = 4
}
