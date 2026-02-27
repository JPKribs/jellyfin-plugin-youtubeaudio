namespace Jellyfin.Plugin.YouTubeAudio.Models;

/// <summary>
/// Information about a Jellyfin music library for the Settings dropdown.
/// </summary>
public class LibraryInfo
{
    /// <summary>Gets or sets the library ID.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the library display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the library path.</summary>
    public string Path { get; set; } = string.Empty;
}
