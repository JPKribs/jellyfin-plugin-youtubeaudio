using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Jellyfin.Plugin.YouTubeAudio.Models;

namespace Jellyfin.Plugin.YouTubeAudio.Services.Playlists;

/// <summary>
/// Reads the Music app's "File → Library → Export Playlist…" plain-text export: tab-separated,
/// one header row, one row per track. The app writes it as UTF-16 with a byte-order mark and
/// bare CR line endings; decoding is handled before this class sees the text.
/// </summary>
public sealed class AppleMusicPlaylistSource : IPlaylistSource
{
    /// <summary>The source identifier stored on queue items.</summary>
    public const string SourceId = "applemusic";

    // The Music app always writes these 31 columns in this order. Header names are localized
    // to the app's language, so when they are not the English ones below, columns are read by
    // position instead.
    private const int StandardColumnCount = 31;

    private static readonly string[] _lineSeparators = ["\r\n", "\r", "\n"];

    private static readonly Dictionary<Field, (string Header, int Position)> _columns = new()
    {
        [Field.Name] = ("Name", 0),
        [Field.Artist] = ("Artist", 1),
        [Field.Composer] = ("Composer", 2),
        [Field.Album] = ("Album", 3),
        [Field.Genre] = ("Genre", 9),
        [Field.Time] = ("Time", 11),
        [Field.DiscNumber] = ("Disc Number", 12),
        [Field.DiscCount] = ("Disc Count", 13),
        [Field.TrackNumber] = ("Track Number", 14),
        [Field.TrackCount] = ("Track Count", 15),
        [Field.Year] = ("Year", 16)
    };

    private enum Field
    {
        Name,
        Artist,
        Composer,
        Album,
        Genre,
        Time,
        DiscNumber,
        DiscCount,
        TrackNumber,
        TrackCount,
        Year
    }

    /// <inheritdoc />
    public string Id => SourceId;

    /// <inheritdoc />
    public string Name => "Apple Music";

    /// <inheritdoc />
    public string Instructions => "In the Music app, select the playlist, then choose File → Library → Export Playlist… and save as Unicode Text.";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions => [".txt"];

    /// <inheritdoc />
    public bool CanParse(string fileName, string content)
        => ResolveColumns(ReadHeader(content)) != null;

    /// <inheritdoc />
    public List<ParsedPlaylist> Parse(string fileName, string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var lines = content.Split(_lineSeparators, StringSplitOptions.None);
        var columns = ResolveColumns(lines.Length > 0 ? lines[0].Split('\t') : [])
            ?? throw new ArgumentException("The file is not an Apple Music playlist export.");

        var playlist = new ParsedPlaylist
        {
            Id = "playlist",
            Name = string.IsNullOrWhiteSpace(fileName) ? null : Path.GetFileNameWithoutExtension(fileName)
        };

        for (var i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                continue;
            }

            // Rows can be shorter than the header: an empty trailing column (Location, for
            // streaming tracks) is dropped, so a missing cell simply reads as empty.
            var cells = lines[i].Split('\t');
            string? Cell(Field field)
            {
                var index = columns[field];
                if (index < 0 || index >= cells.Length)
                {
                    return null;
                }

                var value = cells[index].Trim();
                return value.Length == 0 ? null : value;
            }

            var title = Cell(Field.Name);
            if (title == null)
            {
                playlist.SkippedRows++;
                continue;
            }

            playlist.Tracks.Add(new PlaylistTrack
            {
                Title = title,
                Artist = Cell(Field.Artist),
                // Kept raw: the " - Single" / " - EP" suffix tells the import which release this is.
                Album = Cell(Field.Album),
                Composer = Cell(Field.Composer),
                Genre = Cell(Field.Genre),
                Year = ParsePositiveInt(Cell(Field.Year)),
                TrackNumber = ParsePositiveInt(Cell(Field.TrackNumber)),
                TrackCount = ParsePositiveInt(Cell(Field.TrackCount)),
                DiscNumber = ParsePositiveInt(Cell(Field.DiscNumber)),
                DiscCount = ParsePositiveInt(Cell(Field.DiscCount)),
                DurationSeconds = ParsePositiveInt(Cell(Field.Time))
            });
        }

        return [playlist];
    }

    private static string[] ReadHeader(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return [];
        }

        var end = content.IndexOfAny(['\r', '\n']);
        return (end < 0 ? content : content[..end]).Split('\t');
    }

    /// <summary>
    /// Maps each field to its column index, by English header name when present, otherwise by
    /// the standard position. Returns null when the header is not an Apple Music export.
    /// </summary>
    private static Dictionary<Field, int>? ResolveColumns(string[] header)
    {
        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Length; i++)
        {
            byName.TryAdd(header[i].Trim(), i);
        }

        if (byName.ContainsKey(_columns[Field.Name].Header) && byName.ContainsKey(_columns[Field.Artist].Header))
        {
            var mapped = new Dictionary<Field, int>();
            foreach (var (field, column) in _columns)
            {
                mapped[field] = byName.TryGetValue(column.Header, out var index) ? index : -1;
            }

            return mapped;
        }

        if (header.Length == StandardColumnCount)
        {
            var positional = new Dictionary<Field, int>();
            foreach (var (field, column) in _columns)
            {
                positional[field] = column.Position;
            }

            return positional;
        }

        return null;
    }

    private static int? ParsePositiveInt(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : null;
}
