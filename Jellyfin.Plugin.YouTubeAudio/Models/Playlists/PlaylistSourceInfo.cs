using System.Collections.Generic;

namespace Jellyfin.Plugin.YouTubeAudio.Models;

/// <summary>
/// Describes a supported playlist source for the UI.
/// </summary>
public class PlaylistSourceInfo
{
    /// <summary>Gets or sets the stable source identifier stored on queue items.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a short explanation of how to produce the export file.</summary>
    public string Instructions { get; set; } = string.Empty;

    /// <summary>Gets or sets the accepted file extensions, including the dot.</summary>
    public IReadOnlyList<string> Extensions { get; set; } = [];
}
