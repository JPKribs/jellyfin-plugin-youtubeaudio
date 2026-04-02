namespace Jellyfin.Plugin.YouTubeAudio.Models;

/// <summary>
/// Database model representing a queue item.
/// </summary>
public class QueueItem
{
    /// <summary>Gets or sets the unique identifier (GUID as string).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the original YouTube URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the YouTube video title.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the GUID-based filename (e.g., "a1b2c3d4.opus").</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Gets or sets the current status.</summary>
    public QueueStatus Status { get; set; } = QueueStatus.Queued;

    /// <summary>Gets or sets the error message if status is Error.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Gets or sets when this item was created (ISO 8601).</summary>
    public string CreatedAt { get; set; } = string.Empty;

    /// <summary>Gets or sets when this item was last updated (ISO 8601).</summary>
    public string UpdatedAt { get; set; } = string.Empty;
}
