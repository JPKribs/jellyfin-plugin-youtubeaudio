namespace Jellyfin.Plugin.YouTubeAudio.Models;

/// <summary>
/// A playlist in an uploaded export file, offered for selection.
/// </summary>
public class PlaylistChoice
{
    /// <summary>Gets or sets the playlist ID within the file.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the playlist name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether this is the whole library (songs only, no Jellyfin playlist).</summary>
    public bool IsLibrary { get; set; }

    /// <summary>Gets or sets the number of tracks in the playlist.</summary>
    public int TrackCount { get; set; }
}
