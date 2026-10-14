using System;

namespace Jellyfin.Plugin.YouTubeAudio.Models;

/// <summary>
/// A library song to replace with the audio from a YouTube link.
/// </summary>
public class ReplaceSongRequest
{
    /// <summary>Gets or sets the Jellyfin item ID of the song.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the YouTube link with the correct audio.</summary>
    public string Url { get; set; } = string.Empty;
}
