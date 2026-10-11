namespace Jellyfin.Plugin.YouTubeAudio.Models;

/// <summary>
/// Read-only view of a queue item for the public queue page. Deliberately omits URLs, file
/// names and raw error text, which can carry server details.
/// </summary>
public class PublicQueueItem
{
    /// <summary>Gets or sets the track title, or null before it is known.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the artist, when known.</summary>
    public string? Artist { get; set; }

    /// <summary>Gets or sets the album, when known.</summary>
    public string? Album { get; set; }

    /// <summary>Gets or sets the status code (see <see cref="QueueStatus"/>).</summary>
    public int StatusCode { get; set; }

    /// <summary>Gets or sets a friendly status description.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets where the item came from ("YouTube" or a playlist source name).</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Gets or sets when the item was queued (ISO 8601).</summary>
    public string CreatedAt { get; set; } = string.Empty;
}
