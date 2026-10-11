namespace Jellyfin.Plugin.YouTubeAudio.Models;

/// <summary>
/// API view of a playlist target and its progress.
/// </summary>
public class PlaylistTargetDto
{
    /// <summary>Gets or sets the identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the playlist name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the owning user's name.</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>Gets or sets the number of songs the playlist should contain.</summary>
    public int Total { get; set; }

    /// <summary>Gets or sets the number of songs already in the Jellyfin playlist.</summary>
    public int Added { get; set; }

    /// <summary>Gets or sets the Jellyfin playlist ID once it exists.</summary>
    public string? JellyfinPlaylistId { get; set; }
}
