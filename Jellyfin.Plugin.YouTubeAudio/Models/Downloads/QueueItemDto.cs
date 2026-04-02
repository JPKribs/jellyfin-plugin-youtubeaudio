namespace Jellyfin.Plugin.YouTubeAudio.Models;

/// <summary>
/// API response model for a queue item. Extends database model with metadata.
/// </summary>
public class QueueItemDto
{
    /// <summary>Gets or sets the unique identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the original YouTube URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the video title.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the filename.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Gets or sets the status as a string for display.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets the status as an integer for filtering.</summary>
    public int StatusCode { get; set; }

    /// <summary>Gets or sets the error message.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Gets or sets when this item was created.</summary>
    public string CreatedAt { get; set; } = string.Empty;

    /// <summary>Gets or sets when this item was last updated.</summary>
    public string UpdatedAt { get; set; } = string.Empty;

    // -- Metadata fields populated for Downloaded items --

    /// <summary>Gets or sets the track title from tags/info.json.</summary>
    public string MetadataTitle { get; set; } = string.Empty;

    /// <summary>Gets or sets the primary artist from tags/info.json.</summary>
    public string Artist { get; set; } = string.Empty;

    /// <summary>Gets or sets featured artist(s), semicolon-delimited.</summary>
    public string FeaturedArtist { get; set; } = string.Empty;

    /// <summary>Gets or sets the album artist (folder-level artist for library organization).</summary>
    public string AlbumArtist { get; set; } = string.Empty;

    /// <summary>Gets or sets the album from tags/info.json.</summary>
    public string Album { get; set; } = string.Empty;

    /// <summary>Gets or sets the track number.</summary>
    public int? TrackNumber { get; set; }

    /// <summary>Gets or sets the year.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets the genre.</summary>
    public string Genre { get; set; } = string.Empty;

    /// <summary>Gets or sets the file size in bytes.</summary>
    public long FileSize { get; set; }

    /// <summary>Gets or sets the duration in seconds.</summary>
    public double? Duration { get; set; }
}
