namespace Jellyfin.Plugin.ServerSync.Models.ContentSync;

/// <summary>
/// Status counts for API responses.
/// </summary>
public class SyncStatusResponse
{
    public int Pending { get; set; }

    public int PendingDownload { get; set; }

    public int PendingReplacement { get; set; }

    public int PendingDeletion { get; set; }

    public int Queued { get; set; }

    public int Synced { get; set; }

    public int Errored { get; set; }

    public int Ignored { get; set; }

    public int Deleting { get; set; }
}
