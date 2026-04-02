namespace Jellyfin.Plugin.YouTubeAudio.Models;

/// <summary>
/// Request body for queuing a YouTube URL.
/// </summary>
public class QueueUrlRequest
{
    /// <summary>Gets or sets the YouTube URL to queue.</summary>
    public string Url { get; set; } = string.Empty;
}
