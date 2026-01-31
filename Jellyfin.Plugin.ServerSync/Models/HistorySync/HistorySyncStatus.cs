using Jellyfin.Plugin.ServerSync.Models.Common;

namespace Jellyfin.Plugin.ServerSync.Models.HistorySync;

/// <summary>
/// Status of a history sync item.
/// Values match <see cref="BaseSyncStatus"/> for consistency across sync modules.
/// </summary>
public enum HistorySyncStatus
{
    /// <summary>Item needs review/approval before syncing.</summary>
    /// <remarks>Matches <see cref="BaseSyncStatus.Pending"/>.</remarks>
    Pending = 0,

    /// <summary>Item is approved and ready to sync.</summary>
    /// <remarks>Matches <see cref="BaseSyncStatus.Queued"/>.</remarks>
    Queued = 1,

    /// <summary>Item has been successfully synced.</summary>
    /// <remarks>Matches <see cref="BaseSyncStatus.Synced"/>.</remarks>
    Synced = 2,

    /// <summary>An error occurred during sync.</summary>
    /// <remarks>Matches <see cref="BaseSyncStatus.Errored"/>.</remarks>
    Errored = 3,

    /// <summary>User chose to ignore this item.</summary>
    /// <remarks>Matches <see cref="BaseSyncStatus.Ignored"/>.</remarks>
    Ignored = 4
}
