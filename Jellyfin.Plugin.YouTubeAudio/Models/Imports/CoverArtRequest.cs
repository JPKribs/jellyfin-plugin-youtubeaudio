namespace Jellyfin.Plugin.YouTubeAudio.Models;

/// <summary>
/// A cover image to embed in a downloaded file.
/// </summary>
public class CoverArtRequest
{
    /// <summary>Gets or sets the image (a square JPEG or PNG), base64-encoded.</summary>
    public string Data { get; set; } = string.Empty;
}
