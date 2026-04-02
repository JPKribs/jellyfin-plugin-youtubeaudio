namespace Jellyfin.Plugin.YouTubeAudio.Models;

/// <summary>
/// Request body for updating tags on a downloaded file.
/// </summary>
public class TagUpdateRequest
{
    /// <summary>Gets or sets the queue item ID.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the track title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the primary artist name.</summary>
    public string Artist { get; set; } = string.Empty;

    /// <summary>Gets or sets the featured artist name(s), semicolon-delimited.</summary>
    public string FeaturedArtist { get; set; } = string.Empty;

    /// <summary>Gets or sets the album artist (defaults to Artist if empty).</summary>
    public string AlbumArtist { get; set; } = string.Empty;

    /// <summary>Gets or sets the album name.</summary>
    public string Album { get; set; } = string.Empty;

    /// <summary>Gets or sets the track number.</summary>
    public int? TrackNumber { get; set; }

    /// <summary>Gets or sets the year.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets the genre.</summary>
    public string Genre { get; set; } = string.Empty;
}
