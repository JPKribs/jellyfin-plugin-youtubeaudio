namespace Jellyfin.Plugin.YouTubeAudio.Models;

/// <summary>
/// Database model representing a queue item.
/// </summary>
public class QueueItem
{
    /// <summary>Gets or sets the unique identifier (GUID as string).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the original YouTube URL. Empty for a playlist-imported track until its
    /// YouTube match is found at download time.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the playlist source ID (e.g. "applemusic") for imported tracks, otherwise null.</summary>
    public string? Source { get; set; }

    /// <summary>Gets or sets the track as read from the playlist export, for imported tracks.</summary>
    public PlaylistTrack? SourceTrack { get; set; }

    /// <summary>Gets or sets a note about a YouTube match worth reviewing, or null.</summary>
    public string? MatchNote { get; set; }

    /// <summary>Gets or sets the YouTube video title.</summary>
    public string? Title { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the title was supplied by the user at submit time.
    /// User titles are never refined from info.json or cleaned; derived titles are.
    /// </summary>
    public bool UserTitled { get; set; }

    /// <summary>Gets or sets the GUID-based filename (e.g., "a1b2c3d4.opus").</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Gets or sets the current status.</summary>
    public QueueStatus Status { get; set; } = QueueStatus.Queued;

    /// <summary>Gets or sets the error message if status is Error.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Gets or sets the artist supplied at submit time, applied as a tag after download.</summary>
    public string? Artist { get; set; }

    /// <summary>Gets or sets the album supplied at submit time, applied as a tag after download.</summary>
    public string? Album { get; set; }

    /// <summary>Gets or sets the release year supplied at submit time, applied as a tag after download.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets when this item was created (ISO 8601).</summary>
    public string CreatedAt { get; set; } = string.Empty;

    /// <summary>Gets or sets when this item was last updated (ISO 8601).</summary>
    public string UpdatedAt { get; set; } = string.Empty;
}
