using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.YouTubeAudio.Models;

namespace Jellyfin.Plugin.YouTubeAudio.Services.Playlists;

/// <summary>
/// Reads the Music app's XML exports: "File → Library → Export Library…" (every playlist) and
/// "Export Playlist…" saved as XML (one playlist). Both are property lists holding a Tracks
/// dictionary and a Playlists array whose items reference tracks by ID. Richer than the text
/// export: it carries the album artist and millisecond track lengths.
/// </summary>
public sealed class AppleMusicLibrarySource : IPlaylistSource
{
    /// <summary>The source identifier stored on queue items.</summary>
    public const string SourceId = "applemusiclibrary";

    /// <summary>The playlist ID used for the whole library.</summary>
    internal const string EntireLibraryId = "library";

    /// <inheritdoc />
    public string Id => SourceId;

    /// <inheritdoc />
    public string Name => "Apple Music Library";

    /// <inheritdoc />
    public string Instructions => "In the Music app, choose File → Library → Export Library… to pick from all your playlists, or export a single playlist as XML.";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions => [".xml"];

    /// <inheritdoc />
    public bool CanParse(string fileName, string content)
        => PlistReader.LooksLikePlist(content)
            && content.Contains("<key>Tracks</key>", StringComparison.Ordinal);

    /// <inheritdoc />
    public List<ParsedPlaylist> Parse(string fileName, string content)
    {
        if (PlistReader.Parse(content) is not Dictionary<string, object?> root
            || root.GetValueOrDefault("Tracks") is not Dictionary<string, object?> trackTable)
        {
            throw new ArgumentException("The file is not an Apple Music library export.");
        }

        var tracks = new Dictionary<string, PlaylistTrack?>(StringComparer.Ordinal);
        foreach (var (id, value) in trackTable)
        {
            tracks[id] = value is Dictionary<string, object?> entry ? ReadTrack(entry) : null;
        }

        var playlists = new List<ParsedPlaylist>();
        var playlistEntries = root.GetValueOrDefault("Playlists") as List<object?> ?? new List<object?>();
        foreach (var value in playlistEntries)
        {
            if (value is not Dictionary<string, object?> entry
                || entry.ContainsKey("Distinguished Kind")
                || GetBool(entry, "Folder")
                || entry.GetValueOrDefault("Playlist Items") is not List<object?> items
                || items.Count == 0)
            {
                // Built-in views (Music, Podcasts…) duplicate the library; folders hold no tracks.
                continue;
            }

            var isLibrary = GetBool(entry, "Master");
            var playlist = new ParsedPlaylist
            {
                IsLibrary = isLibrary,
                Id = isLibrary ? EntireLibraryId : GetString(entry, "Playlist Persistent ID") ?? GetLong(entry, "Playlist ID")?.ToString(CultureInfo.InvariantCulture),
                Name = isLibrary ? "Entire Library" : GetString(entry, "Name")
            };

            foreach (var item in items)
            {
                var trackId = item is Dictionary<string, object?> reference ? GetLong(reference, "Track ID") : null;
                var track = trackId.HasValue ? tracks.GetValueOrDefault(trackId.Value.ToString(CultureInfo.InvariantCulture)) : null;
                if (track == null)
                {
                    playlist.SkippedRows++;
                    continue;
                }

                playlist.Tracks.Add(track);
            }

            playlists.Add(playlist);
        }

        // List the whole library last, after the user's own playlists.
        return playlists.OrderBy(p => p.Id == EntireLibraryId).ToList();
    }

    private static PlaylistTrack? ReadTrack(Dictionary<string, object?> entry)
    {
        var title = GetString(entry, "Name");
        if (title == null)
        {
            return null;
        }

        var totalTime = GetLong(entry, "Total Time");
        return new PlaylistTrack
        {
            Title = title,
            Artist = GetString(entry, "Artist"),
            AlbumArtist = GetString(entry, "Album Artist"),
            Album = GetString(entry, "Album"),
            IsCompilation = GetBool(entry, "Compilation"),
            Composer = GetString(entry, "Composer"),
            Genre = GetString(entry, "Genre"),
            // "Release Date" is the album's release; "Year" can be the song's original year,
            // which would file a 2002 greatest-hits album under 1972.
            Year = GetReleaseYear(entry) ?? GetPositiveInt(entry, "Year"),
            TrackNumber = GetPositiveInt(entry, "Track Number"),
            TrackCount = GetPositiveInt(entry, "Track Count"),
            DiscNumber = GetPositiveInt(entry, "Disc Number"),
            DiscCount = GetPositiveInt(entry, "Disc Count"),
            DurationSeconds = totalTime > 0 ? totalTime.Value / 1000.0 : null
        };
    }

    private static string? GetString(Dictionary<string, object?> entry, string key)
        => entry.GetValueOrDefault(key) is string value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static long? GetLong(Dictionary<string, object?> entry, string key)
        => entry.GetValueOrDefault(key) is long value ? value : null;

    private static int? GetPositiveInt(Dictionary<string, object?> entry, string key)
        => GetLong(entry, key) is > 0 and <= int.MaxValue and var value ? (int)value : null;

    private static int? GetReleaseYear(Dictionary<string, object?> entry)
        => entry.GetValueOrDefault("Release Date") is DateTime date && date.Year > 1900 ? date.Year : null;

    private static bool GetBool(Dictionary<string, object?> entry, string key)
        => entry.GetValueOrDefault(key) is true;
}
