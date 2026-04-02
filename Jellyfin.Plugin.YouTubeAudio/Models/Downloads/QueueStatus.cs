namespace Jellyfin.Plugin.YouTubeAudio.Models;

/// <summary>
/// Status of a queue item in the download pipeline.
/// </summary>
public enum QueueStatus
{
    /// <summary>URL queued, awaiting download.</summary>
    Queued = 0,

    /// <summary>Currently being downloaded by yt-dlp.</summary>
    Downloading = 1,

    /// <summary>Download complete, awaiting import.</summary>
    Downloaded = 2,

    /// <summary>Imported into the Jellyfin library.</summary>
    Imported = 3,

    /// <summary>An error occurred during download.</summary>
    Error = 4
}
