using Jellyfin.Plugin.ServerSync.Models.Common;

namespace Jellyfin.Plugin.ServerSync.Models.ContentSync;

/// <summary>
/// Status counts for content sync API responses.
/// Extends BaseSyncStatusResponse with content-specific pending sub-types.
/// </summary>
public class SyncStatusResponse : BaseSyncStatusResponse
{
    /// <summary>
    /// Gets or sets the count of items pending download approval.
    /// </summary>
    public int PendingDownload { get; set; }

    /// <summary>
    /// Gets or sets the count of items pending replacement approval.
    /// </summary>
    public int PendingReplacement { get; set; }

    /// <summary>
    /// Gets or sets the count of items pending deletion approval.
    /// </summary>
    public int PendingDeletion { get; set; }

    /// <summary>
    /// Gets or sets the count of items currently being deleted.
    /// </summary>
    public int Deleting { get; set; }

    /// <summary>
    /// Gets the total count including content-specific statuses.
    /// </summary>
    public override int Total => base.Total + PendingDownload + PendingReplacement + PendingDeletion + Deleting;
}
