using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.YouTubeAudio.Services.Playlists;

namespace Jellyfin.Plugin.YouTubeAudio.Services.Catalog;

/// <summary>
/// A song from Apple's music catalog.
/// </summary>
/// <param name="ArtistName">The credited artist(s), as Apple writes them.</param>
/// <param name="TrackName">The song title.</param>
/// <param name="AlbumName">The album the song is on.</param>
/// <param name="AlbumArtist">The album's artist ("Various Artists" for compilations).</param>
/// <param name="Year">The release year.</param>
/// <param name="TrackNumber">The track number.</param>
/// <param name="TrackCount">The number of tracks on the disc.</param>
/// <param name="DiscNumber">The disc number.</param>
/// <param name="DiscCount">The number of discs.</param>
/// <param name="Genre">Apple's genre.</param>
/// <param name="ArtworkUrl">The album cover (100 px; see <see cref="CatalogMatcher.GetArtworkUrl"/>).</param>
/// <param name="DurationSeconds">The song length.</param>
public sealed record CatalogSong(
    string ArtistName,
    string TrackName,
    string AlbumName,
    string AlbumArtist,
    int? Year,
    int? TrackNumber,
    int? TrackCount,
    int? DiscNumber,
    int? DiscCount,
    string? Genre,
    string? ArtworkUrl,
    double? DurationSeconds);

/// <summary>
/// Reads Apple catalog (iTunes Search API) responses and picks the song matching a download.
/// Pure logic, kept apart from the HTTP client so it can be tested against captured responses.
/// </summary>
public static partial class CatalogMatcher
{
    /// <summary>A match whose title only partly agrees must be this close in length.</summary>
    internal const double PartialTitleMaxDeltaSeconds = 3;

    /// <summary>
    /// Reads the songs from a search response. Returns an empty list for anything unreadable.
    /// </summary>
    public static List<CatalogSong> ParseSongs(string? json)
    {
        var songs = new List<CatalogSong>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return songs;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            {
                return songs;
            }

            foreach (var r in results.EnumerateArray())
            {
                if (GetString(r, "kind") != "song" || GetString(r, "trackName") is not { } trackName || GetString(r, "artistName") is not { } artistName)
                {
                    continue;
                }

                var releaseDate = GetString(r, "releaseDate");
                songs.Add(new CatalogSong(
                    artistName,
                    trackName,
                    GetString(r, "collectionName") ?? string.Empty,
                    GetString(r, "collectionArtistName") ?? artistName,
                    releaseDate?.Length >= 4 && int.TryParse(releaseDate[..4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) ? year : null,
                    GetInt(r, "trackNumber"),
                    GetInt(r, "trackCount"),
                    GetInt(r, "discNumber"),
                    GetInt(r, "discCount"),
                    GetString(r, "primaryGenreName"),
                    GetString(r, "artworkUrl100"),
                    GetInt(r, "trackTimeMillis") is { } ms ? ms / 1000.0 : null));
            }
        }
        catch (JsonException)
        {
        }

        return songs;
    }

    /// <summary>
    /// Reads the artist names from an artist search response.
    /// </summary>
    public static List<string> ParseArtistNames(string? json)
    {
        var names = new List<string>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return names;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                names.AddRange(results.EnumerateArray().Select(r => GetString(r, "artistName")).OfType<string>());
            }
        }
        catch (JsonException)
        {
        }

        return names;
    }

    /// <summary>
    /// Returns the catalog song matching a download, or null when none is a confident match.
    /// The artist must match, and the title must match (allowing Apple's longer classical titles
    /// such as "Bagatelle No. 25 in A Minor, WoO 59 "Für Elise"" for "Für Elise", or the same
    /// piece written another way: see <see cref="MostlyOverlaps"/>). When both
    /// lengths are known they must agree: within 8 seconds for the same title, within 3 for a
    /// partial one. Album versions beat singles and compilations.
    /// </summary>
    public static CatalogSong? PickBest(string? artist, string? title, double? durationSeconds, IEnumerable<CatalogSong> songs)
    {
        ArgumentNullException.ThrowIfNull(songs);
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var artistKeys = ArtistKeys(artist);
        var titleWords = YouTubeMatcher.Tokenize(StripBrackets(title));
        if (artistKeys.Count == 0 || titleWords.Count == 0)
        {
            return null;
        }

        CatalogSong? best = null;
        var bestScore = double.MinValue;
        foreach (var song in songs)
        {
            var songKeys = ArtistKeys(song.ArtistName);
            if (!artistKeys.Any(a => songKeys.Any(s => KeysMatch(a, s))))
            {
                continue;
            }

            var candidateWords = YouTubeMatcher.Tokenize(song.TrackName);
            var candidateCore = YouTubeMatcher.Tokenize(StripBrackets(song.TrackName));
            var sameTitle = candidateCore.SequenceEqual(titleWords);
            var partialTitle = !sameTitle
                && (titleWords.All(candidateWords.Contains)
                    || (candidateCore.Count > 0 && candidateCore.All(titleWords.Contains))
                    || MostlyOverlaps(titleWords, candidateWords));
            if (!sameTitle && !partialTitle)
            {
                continue;
            }

            double? delta = durationSeconds.HasValue && song.DurationSeconds.HasValue
                ? Math.Abs(durationSeconds.Value - song.DurationSeconds.Value)
                : null;
            if (delta > (sameTitle ? YouTubeMatcher.MaxDurationDeltaSeconds : PartialTitleMaxDeltaSeconds))
            {
                continue;
            }

            var score = (sameTitle ? 30.0 : 10.0) - ((delta ?? 0) * 2);
            score -= IsCompilation(song) ? 15 : 0;
            score -= SingleOrEpRegex().IsMatch(song.AlbumName) ? 10 : 0;

            // Strictly greater keeps Apple's own ranking as the tie-breaker.
            if (score > bestScore)
            {
                bestScore = score;
                best = song;
            }
        }

        return best;
    }

    /// <summary>
    /// Returns whether an artist search lists exactly this name, meaning a name like
    /// "Katia &amp; Marielle Labèque" is one act rather than a collaboration credit.
    /// </summary>
    public static bool ListsArtist(string name, IEnumerable<string> artistNames)
    {
        ArgumentNullException.ThrowIfNull(artistNames);
        var key = TrackMetadataCleaner.Normalize(name);
        return key.Length > 0 && artistNames.Any(n => TrackMetadataCleaner.Normalize(n) == key);
    }

    /// <summary>
    /// Turns Apple's 100 px artwork link into a square one of the given size (Apple serves any
    /// size from the same path; "cc" crops to the centre, so covers that are not quite square
    /// still come back 1:1).
    /// </summary>
    public static string? GetArtworkUrl(string? artworkUrl100, int size)
        => string.IsNullOrEmpty(artworkUrl100)
            ? null
            : ArtworkSizeRegex().Replace(artworkUrl100, string.Create(CultureInfo.InvariantCulture, $"/{size}x{size}cc."));

    /// <summary>
    /// Strips Apple's " - Single" and " - EP" suffixes and edition labels from an album name.
    /// </summary>
    public static string CleanAlbumName(string album) => TrackMetadataCleaner.CleanAlbum(album) ?? album;

    /// <summary>
    /// Returns whether two titles share at least 80% of the shorter one's words, which covers
    /// the same classical piece written two ways ("Beethoven: Für Elise, Bagatelle in A Minor,
    /// WoO 59" and "Bagatelle No. 25 in A Minor, WoO 59 "Für Elise""). Short titles never
    /// qualify; they must match outright.
    /// </summary>
    internal static bool MostlyOverlaps(IReadOnlyCollection<string> a, IReadOnlyCollection<string> b)
    {
        var setA = a.ToHashSet(StringComparer.Ordinal);
        var setB = b.ToHashSet(StringComparer.Ordinal);
        var smaller = Math.Min(setA.Count, setB.Count);
        return smaller >= 4 && setA.Count(setB.Contains) >= smaller * 0.8;
    }

    private static bool IsCompilation(CatalogSong song)
        => string.Equals(song.AlbumArtist, "Various Artists", StringComparison.OrdinalIgnoreCase);

    private static List<string> ArtistKeys(string artist)
        => TrackMetadataCleaner.SplitCredit(artist).Append(artist)
            .Select(TrackMetadataCleaner.Normalize)
            .Where(k => k.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static bool KeysMatch(string a, string b)
        => a == b || (a.Length >= 4 && b.Contains(a, StringComparison.Ordinal)) || (b.Length >= 4 && a.Contains(b, StringComparison.Ordinal));

    private static string StripBrackets(string value) => BracketRegex().Replace(value, " ");

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? GetInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number > 0 ? number : null;

    [GeneratedRegex(@"\s*[\(\[][^\)\]]*[\)\]]")]
    private static partial Regex BracketRegex();

    [GeneratedRegex(@"\s+-\s+(Single|EP)$", RegexOptions.IgnoreCase)]
    private static partial Regex SingleOrEpRegex();

    [GeneratedRegex(@"/\d+x\d+(bb|cc)\.")]
    private static partial Regex ArtworkSizeRegex();
}
