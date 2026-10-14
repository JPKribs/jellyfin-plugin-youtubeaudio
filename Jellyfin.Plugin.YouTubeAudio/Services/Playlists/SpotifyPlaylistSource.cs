using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.YouTubeAudio.Models;

namespace Jellyfin.Plugin.YouTubeAudio.Services.Playlists;

/// <summary>
/// Reads Spotify playlists exported with Exportify (https://github.com/watsonbox/exportify): one
/// CSV per playlist, or a ZIP of them (unpacked by <see cref="PlaylistImportService"/>). Each row
/// carries the track, its artists with their Spotify IDs, the album, album artist, release date,
/// track and disc numbers, length, cover link and ISRC. Spotify exports no genre or composer.
/// </summary>
public sealed partial class SpotifyPlaylistSource : IPlaylistSource
{
    /// <summary>The source identifier stored on queue items.</summary>
    public const string SourceId = "spotify";

    // Joining words left lowercase in a rebuilt playlist name unless they start it.
    private static readonly HashSet<string> _smallWords = new(StringComparer.Ordinal) { "a", "an", "and", "as", "at", "but", "by", "for", "in", "of", "on", "or", "the", "to", "with" };

    /// <summary>The playlist ID used for Liked Songs, which is treated like a whole library.</summary>
    internal const string LikedSongsFile = "liked.csv";

    /// <inheritdoc />
    public string Id => SourceId;

    /// <inheritdoc />
    public string Name => "Spotify";

    /// <inheritdoc />
    public string Instructions => "Export with Exportify (github.com/watsonbox/exportify): one playlist as a CSV file, or all of them as a ZIP.";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions => [".csv", ".zip"];

    /// <inheritdoc />
    public bool CanParse(string fileName, string content)
    {
        var firstLine = (content ?? string.Empty).Split('\n', 2)[0];
        return firstLine.Contains("Track URI", StringComparison.Ordinal)
            && firstLine.Contains("Artist Name(s)", StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public List<ParsedPlaylist> Parse(string fileName, string content)
    {
        var rows = ReadCsv(content);
        if (rows.Count == 0)
        {
            throw new ArgumentException("The file is not a Spotify playlist export.");
        }

        var header = rows[0];
        int Column(string name) => header.FindIndex(h => string.Equals(h.Trim(), name, StringComparison.Ordinal));
        var col = new
        {
            Title = Column("Track Name"),
            ArtistUris = Column("Artist URI(s)"),
            Artists = Column("Artist Name(s)"),
            Album = Column("Album Name"),
            AlbumArtistUris = Column("Album Artist URI(s)"),
            AlbumArtists = Column("Album Artist Name(s)"),
            Released = Column("Album Release Date"),
            Image = Column("Album Image URL"),
            Disc = Column("Disc Number"),
            Track = Column("Track Number"),
            Length = Column("Track Duration (ms)"),
            Isrc = Column("ISRC")
        };
        if (col.Title < 0 || col.Artists < 0)
        {
            throw new ArgumentException("The file is not a Spotify playlist export.");
        }

        var baseName = Path.GetFileName(fileName ?? string.Empty);
        var isLiked = string.Equals(baseName, LikedSongsFile, StringComparison.OrdinalIgnoreCase);
        var playlist = new ParsedPlaylist
        {
            IsLibrary = isLiked,
            Name = isLiked ? "Liked Songs" : NameFromFileName(baseName)
        };

        foreach (var row in rows.Skip(1))
        {
            string? Cell(int index) => index >= 0 && index < row.Count && !string.IsNullOrWhiteSpace(row[index]) ? row[index].Trim() : null;

            var title = Cell(col.Title);
            var artists = SplitArtists(Cell(col.Artists), Cell(col.ArtistUris));
            if (title == null || artists.Count == 0)
            {
                playlist.SkippedRows++;
                continue;
            }

            var albumArtists = SplitArtists(Cell(col.AlbumArtists), Cell(col.AlbumArtistUris));
            var released = Cell(col.Released);
            playlist.Tracks.Add(new PlaylistTrack
            {
                Title = title,
                Artist = artists[0],
                FeaturedArtists = artists.Count > 1 ? artists.Skip(1).ToList() : null,
                AlbumArtist = albumArtists.Count > 0 ? albumArtists[0] : null,
                Album = Cell(col.Album),
                Year = released?.Length >= 4 && int.TryParse(released[..4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) ? year : null,
                TrackNumber = ParsePositiveInt(Cell(col.Track)),
                DiscNumber = ParsePositiveInt(Cell(col.Disc)),
                DurationSeconds = ParsePositiveInt(Cell(col.Length)) is { } ms ? ms / 1000.0 : null,
                Isrc = Cell(col.Isrc),
                ArtworkUrl = Cell(col.Image)
            });
        }

        return [playlist];
    }

    /// <summary>
    /// Splits Exportify's comma-separated artist names. A comma inside a name is escaped
    /// ("Sir\, Please"). For older exports without that escaping, the Spotify IDs alongside the
    /// names, one per artist, say how many artists there really are, and extra pieces are
    /// joined back to the name they belong to.
    /// </summary>
    internal static List<string> SplitArtists(string? names, string? uris)
    {
        var parts = EscapedCommaSplitRegex().Split(names ?? string.Empty)
            .Select(p => p.Replace("\\,", ",", StringComparison.Ordinal).Trim())
            .Where(p => p.Length > 0)
            .ToList();
        var count = (uris ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
        if (count == 1 && parts.Count > 1)
        {
            return [string.Join(", ", parts)];
        }

        while (count > 0 && parts.Count > count)
        {
            // Join the piece that looks most like the rest of a name ("The Creator", a lowercase
            // word) onto the one before it; otherwise join the first two.
            var index = parts.FindIndex(1, p => p.StartsWith("The ", StringComparison.Ordinal) || char.IsLower(p[0]));
            index = index < 1 ? 1 : index;
            parts[index - 1] = parts[index - 1] + ", " + parts[index];
            parts.RemoveAt(index);
        }

        return parts;
    }

    /// <summary>
    /// Rebuilds a playlist name from Exportify's file name, which lowercases it and uses
    /// underscores for spaces ("wedding_(clara_&amp;_felix).csv" gives "Wedding (Clara &amp; Felix)").
    /// </summary>
    internal static string NameFromFileName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName ?? string.Empty).Replace('_', ' ').Trim();
        if (!name.Any(char.IsLetter))
        {
            return "Spotify Playlist";
        }

        if (name.Any(char.IsUpper))
        {
            return name;
        }

        // Capitalize each word, except short joining words after the first ("Music for the Evening").
        var words = name.Split(' ');
        for (var i = 0; i < words.Length; i++)
        {
            var word = words[i];
            var letter = word.TakeWhile(c => !char.IsLetter(c)).Count();
            if (letter >= word.Length || (i > 0 && letter == 0 && _smallWords.Contains(word)))
            {
                continue;
            }

            words[i] = string.Concat(word.AsSpan(0, letter), char.ToUpperInvariant(word[letter]).ToString(), word.AsSpan(letter + 1));
        }

        return string.Join(' ', words);
    }

    /// <summary>
    /// Reads CSV text (RFC 4180: quoted fields may hold commas, quotes and line breaks).
    /// </summary>
    internal static List<List<string>> ReadCsv(string content)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var text = content ?? string.Empty;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                row.Add(field.ToString());
                field.Clear();
                if (row.Count > 1 || row[0].Length > 0)
                {
                    rows.Add(row);
                }

                row = new List<string>();
            }
            else
            {
                field.Append(c);
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }

    private static int? ParsePositiveInt(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : null;

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<!\\), ")]
    private static partial System.Text.RegularExpressions.Regex EscapedCommaSplitRegex();
}
