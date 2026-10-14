using System.Collections.Generic;

namespace Jellyfin.Plugin.YouTubeAudio.Models;

/// <summary>
/// One track read from a playlist export, in a source-neutral shape. Every playlist source
/// (Apple Music, and any added later) maps its own format onto this, and the queue stores it so
/// the YouTube search and the final tags work the same regardless of where the track came from.
/// </summary>
public class PlaylistTrack
{
    /// <summary>Gets or sets the track title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the artist. Sources set it verbatim; the import cleanup reduces it to the
    /// primary artist and moves "feat." artists to <see cref="FeaturedArtists"/>.
    /// </summary>
    public string? Artist { get; set; }

    /// <summary>Gets or sets featured artists, taken from "feat." in the artist or title.</summary>
    public List<string>? FeaturedArtists { get; set; }

    /// <summary>Gets or sets the album artist, when the source provides one (e.g. "Various Artists").</summary>
    public string? AlbumArtist { get; set; }

    /// <summary>
    /// Gets or sets the album name. Sources set it verbatim (including suffixes such as
    /// " - Single" or "(Deluxe Edition)"); the import cleanup strips them.
    /// </summary>
    public string? Album { get; set; }

    /// <summary>Gets or sets a value indicating whether the source flags the album as a compilation.</summary>
    public bool IsCompilation { get; set; }

    /// <summary>Gets or sets the release type, determined by the import cleanup from the raw album.</summary>
    public ReleaseType? ReleaseType { get; set; }

    /// <summary>
    /// Gets or sets library files holding a lesser version of this song (e.g. the single when
    /// this is the album version). They are deleted once this track is imported.
    /// </summary>
    public List<string>? ReplacesFiles { get; set; }

    /// <summary>Gets or sets the composer.</summary>
    public string? Composer { get; set; }

    /// <summary>Gets or sets the genre.</summary>
    public string? Genre { get; set; }

    /// <summary>Gets or sets the release year.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets the track's position on its album.</summary>
    public int? TrackNumber { get; set; }

    /// <summary>Gets or sets the number of tracks on the album.</summary>
    public int? TrackCount { get; set; }

    /// <summary>Gets or sets the disc number.</summary>
    public int? DiscNumber { get; set; }

    /// <summary>Gets or sets the number of discs in the release.</summary>
    public int? DiscCount { get; set; }

    /// <summary>Gets or sets the track length in seconds, used to verify the YouTube match.</summary>
    public double? DurationSeconds { get; set; }

    /// <summary>
    /// Gets or sets the recording's ISRC, an industry-wide recording ID. YouTube finds the
    /// official upload by it, so it is searched first when present.
    /// </summary>
    public string? Isrc { get; set; }

    /// <summary>Gets or sets a link to the album cover the source provides (square), embedded on download.</summary>
    public string? ArtworkUrl { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this download replaces the library file in
    /// <see cref="ReplacesFiles"/> in place: it keeps that file's tags and cover, and is written
    /// to the same path, so the song keeps its place in the library and in playlists.
    /// </summary>
    public bool ReplaceInPlace { get; set; }
}
