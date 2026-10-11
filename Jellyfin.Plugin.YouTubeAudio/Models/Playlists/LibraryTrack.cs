using System;

namespace Jellyfin.Plugin.YouTubeAudio.Models;

/// <summary>
/// A track already in the Jellyfin library, used to skip duplicates and upgrade singles.
/// </summary>
public class LibraryTrack
{
    /// <summary>Gets or sets the track title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the primary artist.</summary>
    public string? Artist { get; set; }

    /// <summary>Gets or sets the album artist.</summary>
    public string? AlbumArtist { get; set; }

    /// <summary>Gets or sets the album name, as tagged.</summary>
    public string? Album { get; set; }

    /// <summary>Gets or sets the album year.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets the Jellyfin item ID.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the file path.</summary>
    public string? Path { get; set; }
}
