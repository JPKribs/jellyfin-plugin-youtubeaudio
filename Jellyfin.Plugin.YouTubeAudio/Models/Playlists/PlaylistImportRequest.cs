using System.Collections.Generic;

namespace Jellyfin.Plugin.YouTubeAudio.Models;

/// <summary>
/// Request body for importing a playlist export file into the download queue.
/// </summary>
public class PlaylistImportRequest
{
    /// <summary>Gets or sets the uploaded file's name, used for format detection and the playlist name.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Gets or sets the raw file bytes, base64 encoded so any text encoding survives intact.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the IDs of the playlists to queue, for files holding several. Leave empty
    /// on the first upload to get the list of playlists to choose from.
    /// </summary>
    public List<string>? PlaylistIds { get; set; }

    /// <summary>
    /// Gets or sets the Jellyfin user who gets a private playlist per imported playlist, filled
    /// as the songs reach the library. Empty to only download.
    /// </summary>
    public string? UserId { get; set; }
}
