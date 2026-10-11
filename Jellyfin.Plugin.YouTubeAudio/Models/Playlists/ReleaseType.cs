namespace Jellyfin.Plugin.YouTubeAudio.Models;

/// <summary>
/// The kind of release a track version comes from. Ordered by preference: when the same song
/// is available from several releases, the highest value wins.
/// </summary>
public enum ReleaseType
{
    /// <summary>A single.</summary>
    Single = 0,

    /// <summary>An EP.</summary>
    EP = 1,

    /// <summary>A compilation (greatest hits, box set, various artists).</summary>
    Compilation = 2,

    /// <summary>A regular album.</summary>
    Album = 3
}
