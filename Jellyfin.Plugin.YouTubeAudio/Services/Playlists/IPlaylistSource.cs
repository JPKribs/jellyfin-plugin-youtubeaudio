using System.Collections.Generic;
using Jellyfin.Plugin.YouTubeAudio.Models;

namespace Jellyfin.Plugin.YouTubeAudio.Services.Playlists;

/// <summary>
/// A playlist export format the plugin can read. Implementations only turn a file into
/// <see cref="PlaylistTrack"/>s; queueing, YouTube matching and tagging are shared. To support a
/// new service, implement this and register it in <see cref="ServiceRegistrator"/>.
/// </summary>
public interface IPlaylistSource
{
    /// <summary>Gets the stable identifier stored on queue items (e.g. "applemusic").</summary>
    string Id { get; }

    /// <summary>Gets the display name.</summary>
    string Name { get; }

    /// <summary>Gets a short explanation of how to produce the export file.</summary>
    string Instructions { get; }

    /// <summary>Gets the accepted file extensions, including the dot.</summary>
    IReadOnlyList<string> Extensions { get; }

    /// <summary>
    /// Returns whether this source recognizes the file. Decide from the content, not just the
    /// extension, since several services export plain .txt or .csv files.
    /// </summary>
    /// <param name="fileName">The uploaded file name.</param>
    /// <param name="content">The decoded file text.</param>
    /// <returns>True if <see cref="Parse"/> can read the file.</returns>
    bool CanParse(string fileName, string content);

    /// <summary>
    /// Parses the file into playlists. Most exports hold one; a whole-library export holds
    /// several, each with an <see cref="ParsedPlaylist.Id"/> so the user can choose among them.
    /// </summary>
    /// <param name="fileName">The uploaded file name.</param>
    /// <param name="content">The decoded file text.</param>
    /// <returns>The parsed playlists.</returns>
    List<ParsedPlaylist> Parse(string fileName, string content);
}
