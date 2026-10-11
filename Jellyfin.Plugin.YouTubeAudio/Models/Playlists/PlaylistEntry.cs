namespace Jellyfin.Plugin.YouTubeAudio.Models;

/// <summary>
/// One song a <see cref="PlaylistTarget"/> should contain.
/// </summary>
public class PlaylistEntry
{
    /// <summary>Gets or sets the owning target ID.</summary>
    public string TargetId { get; set; } = string.Empty;

    /// <summary>Gets or sets the position in the source playlist.</summary>
    public int Position { get; set; }

    /// <summary>Gets or sets the song key (see <c>TrackMetadataCleaner.SongKey</c>), matched against library tracks.</summary>
    public string SongKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the artist, for display.</summary>
    public string? Artist { get; set; }

    /// <summary>Gets or sets the title, for display.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets when the song was added to (or found already in) the Jellyfin playlist, or null while pending.</summary>
    public string? AddedAt { get; set; }

    /// <summary>Gets or sets when the song was moved into the library and is awaiting the scan, or null.</summary>
    public string? ImportedAt { get; set; }
}
