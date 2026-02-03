namespace Jellyfin.Plugin.ServerSync.Models.ContentSync;

/// <summary>
/// Response containing pending sync size information.
/// </summary>
public class PendingSizeResponse
{
    /// <summary>
    /// Gets or sets the total bytes for items pending download.
    /// </summary>
    public long PendingDownloadBytes { get; set; }

    /// <summary>
    /// Gets or sets the total bytes for items pending replacement.
    /// </summary>
    public long PendingReplacementBytes { get; set; }

    /// <summary>
    /// Gets or sets the total bytes for items pending deletion.
    /// </summary>
    public long PendingDeletionBytes { get; set; }

    /// <summary>
    /// Gets or sets the total bytes for queued items.
    /// </summary>
    public long QueuedBytes { get; set; }

    /// <summary>
    /// Gets or sets the total pending bytes (Download + Replacement + Queued - Deletion).
    /// </summary>
    public long TotalPendingBytes { get; set; }
}
