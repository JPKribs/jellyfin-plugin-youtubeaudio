namespace Jellyfin.Plugin.YouTubeAudio.Models;

/// <summary>
/// A track already in the download queue, used to avoid queueing it twice.
/// </summary>
public class QueuedTrack
{
    /// <summary>Gets or sets the queue item ID.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the track title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the artist.</summary>
    public string? Artist { get; set; }

    /// <summary>Gets or sets the queue status.</summary>
    public QueueStatus Status { get; set; }

    /// <summary>Gets or sets the release type, when known (playlist imports).</summary>
    public ReleaseType? ReleaseType { get; set; }
}
