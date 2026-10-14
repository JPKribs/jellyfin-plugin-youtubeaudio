using System;

namespace Jellyfin.Plugin.YouTubeAudio.Models;

/// <summary>
/// A song in the Jellyfin library, as offered when choosing one to replace.
/// </summary>
public class LibrarySong
{
    /// <summary>Gets or sets the Jellyfin item ID.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the artists, joined for display.</summary>
    public string? Artist { get; set; }

    /// <summary>Gets or sets the album.</summary>
    public string? Album { get; set; }

    /// <summary>Gets or sets the file path.</summary>
    public string? Path { get; set; }
}
