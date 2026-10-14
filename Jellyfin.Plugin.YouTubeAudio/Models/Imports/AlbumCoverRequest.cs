namespace Jellyfin.Plugin.YouTubeAudio.Models;

/// <summary>
/// A library album whose cover a downloaded file should take.
/// </summary>
public class AlbumCoverRequest
{
    /// <summary>Gets or sets the album name.</summary>
    public string? Album { get; set; }

    /// <summary>Gets or sets the album artist, to tell apart albums with the same name.</summary>
    public string? AlbumArtist { get; set; }
}
