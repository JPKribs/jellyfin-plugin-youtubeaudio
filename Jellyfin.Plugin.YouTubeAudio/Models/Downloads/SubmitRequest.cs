namespace Jellyfin.Plugin.YouTubeAudio.Models;

/// <summary>
/// Request body for an approved user submitting a download link with metadata.
/// </summary>
public class SubmitRequest
{
    /// <summary>Gets or sets the YouTube URL to queue.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the artist applied to every resulting track.</summary>
    public string Artist { get; set; } = string.Empty;

    /// <summary>Gets or sets the album applied to every resulting track.</summary>
    public string Album { get; set; } = string.Empty;

    /// <summary>Gets or sets the release year applied to every resulting track.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets an optional song title, applied only when the URL resolves to a single track.</summary>
    public string? Title { get; set; }
}
