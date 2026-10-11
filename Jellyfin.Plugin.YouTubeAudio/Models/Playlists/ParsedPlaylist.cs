using System.Collections.Generic;

namespace Jellyfin.Plugin.YouTubeAudio.Models;

/// <summary>
/// One playlist read from an export file. A file may hold several (a whole-library export).
/// </summary>
public class ParsedPlaylist
{
    /// <summary>Gets or sets an ID unique within the file, used to select this playlist.</summary>
    public string? Id { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this entry is the whole library rather than a
    /// playlist. Its songs are downloaded, but no Jellyfin playlist is made for it.
    /// </summary>
    public bool IsLibrary { get; set; }

    /// <summary>Gets or sets the playlist name, when the export carries one.</summary>
    public string? Name { get; set; }

    /// <summary>Gets the tracks in playlist order.</summary>
    public List<PlaylistTrack> Tracks { get; } = new();

    /// <summary>Gets or sets the number of entries skipped because they had no usable title.</summary>
    public int SkippedRows { get; set; }
}
