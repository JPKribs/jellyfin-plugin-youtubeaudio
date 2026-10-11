using System.Collections.Generic;

namespace Jellyfin.Plugin.YouTubeAudio.Models;

/// <summary>
/// Response for a playlist import.
/// </summary>
public class PlaylistImportResult
{
    /// <summary>Gets or sets the display name of the source that parsed the file.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Gets or sets the queued playlist names, when known.</summary>
    public string? PlaylistName { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the file holds several playlists and nothing was
    /// queued yet. Resend the file with <see cref="PlaylistImportRequest.PlaylistIds"/> chosen
    /// from <see cref="Playlists"/>.
    /// </summary>
    public bool NeedsSelection { get; set; }

    /// <summary>Gets or sets the playlists in the file.</summary>
    public List<PlaylistChoice> Playlists { get; set; } = new();

    /// <summary>Gets or sets the number of entries left out as repeats or lesser versions (e.g. the single when the album version is also selected).</summary>
    public int DuplicateTracks { get; set; }

    /// <summary>Gets or sets the number of songs skipped because the library already has them.</summary>
    public int AlreadyInLibrary { get; set; }

    /// <summary>Gets or sets the number of songs skipped because they are already queued.</summary>
    public int AlreadyQueued { get; set; }

    /// <summary>Gets or sets the number of queued tracks that will replace a single or other lesser version in the library.</summary>
    public int LibraryUpgrades { get; set; }

    /// <summary>Gets or sets the number of Jellyfin playlists that will be created or appended to for the chosen user.</summary>
    public int PlaylistsTracked { get; set; }

    /// <summary>Gets or sets the queue items created, one per track.</summary>
    public List<QueueItemDto> Items { get; set; } = new();

    /// <summary>Gets or sets the number of entries skipped because they had no usable title.</summary>
    public int SkippedRows { get; set; }
}
