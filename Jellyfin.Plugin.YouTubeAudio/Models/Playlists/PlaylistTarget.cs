namespace Jellyfin.Plugin.YouTubeAudio.Models;

/// <summary>
/// A Jellyfin playlist an import fills: owned by one user, named after the source playlist.
/// Its songs are added as they appear in the library.
/// </summary>
public class PlaylistTarget
{
    /// <summary>Gets or sets the identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the owning Jellyfin user ID.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Gets or sets the playlist name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the playlist source ID the target was imported from.</summary>
    public string? Source { get; set; }

    /// <summary>Gets or sets the Jellyfin playlist ID once it exists.</summary>
    public string? JellyfinPlaylistId { get; set; }

    /// <summary>Gets or sets when the target was created (ISO 8601).</summary>
    public string CreatedAt { get; set; } = string.Empty;
}
