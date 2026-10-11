using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.YouTubeAudio.Models;

namespace Jellyfin.Plugin.YouTubeAudio.Services.Playlists;

/// <summary>
/// Normalizes track metadata from any playlist source: strips edition designations from album
/// names so every edition groups as one album, moves "feat." artists out of titles into
/// <see cref="PlaylistTrack.FeaturedArtists"/>, and classifies the release (album, single…) so
/// the best version of a song can be kept. Also builds the keys used to detect duplicates.
/// </summary>
public static partial class TrackMetadataCleaner
{
    private const string VariousArtists = "Various Artists";

    private static readonly string[] _featuredSeparators = [", ", " & ", " and "];

    /// <summary>
    /// Returns a cleaned copy of the track with its release type set. The source track is not
    /// modified.
    /// </summary>
    public static PlaylistTrack Clean(PlaylistTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);

        var (artist, artistFeatured) = SplitArtist(track.Artist);
        var (title, titleFeatured) = ExtractFeatured(track.Title);
        title = StripTitleDesignations(title);
        if (title.Length == 0)
        {
            title = track.Title.Trim();
        }

        var (albumArtist, _) = SplitArtist(track.AlbumArtist);
        var album = CleanAlbum(track.Album);

        var featured = (track.FeaturedArtists ?? Enumerable.Empty<string>())
            .Concat(artistFeatured)
            .Concat(titleFeatured)
            .Where(name => !string.Equals(name, artist, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new PlaylistTrack
        {
            Title = title,
            Artist = artist,
            FeaturedArtists = featured.Count > 0 ? featured : null,
            AlbumArtist = albumArtist,
            Album = album,
            IsCompilation = track.IsCompilation,
            ReleaseType = track.ReleaseType ?? DetectReleaseType(track.Album, track.AlbumArtist, track.IsCompilation, track.TrackCount),
            Composer = track.Composer,
            Genre = track.Genre,
            Year = track.Year,
            TrackNumber = track.TrackNumber,
            TrackCount = track.TrackCount,
            DiscNumber = track.DiscNumber,
            DiscCount = track.DiscCount,
            DurationSeconds = track.DurationSeconds,
            ReplacesFiles = track.ReplacesFiles
        };
    }

    /// <summary>
    /// Strips release-type suffixes (" - Single", " - EP"), edition designations ("(Deluxe
    /// Edition)", "[Remastered]", " - 20th Anniversary Edition"…) and "feat." credits from an
    /// album name. Distinct recordings such as "(Live)" or "(Orchestral Version)" are kept.
    /// </summary>
    public static string? CleanAlbum(string? album)
    {
        if (string.IsNullOrWhiteSpace(album))
        {
            return null;
        }

        var cleaned = ReleaseSuffixRegex().Replace(album.Trim(), string.Empty);
        cleaned = FeaturedBracketRegex().Replace(cleaned, string.Empty);
        cleaned = BracketRegex().Replace(cleaned, match => IsAlbumDesignation(match.Groups["inner"].Value) ? string.Empty : match.Value);
        cleaned = DashSuffixRegex().Replace(cleaned, match => IsAlbumDesignation(match.Groups["inner"].Value) ? string.Empty : match.Value);
        cleaned = CollapseWhitespace(cleaned);

        return cleaned.Length == 0 ? album.Trim() : cleaned;
    }

    /// <summary>
    /// Removes "feat." credits from a title and returns them as a list of artists. Handles
    /// "(feat. X)", "[ft. X]", "(featuring X)", "(with X)" and a trailing unbracketed "feat. X".
    /// </summary>
    public static (string Title, List<string> Featured) ExtractFeatured(string? title)
    {
        var featured = new List<string>();
        if (string.IsNullOrWhiteSpace(title))
        {
            return (string.Empty, featured);
        }

        var cleaned = FeaturedBracketRegex().Replace(title, match =>
        {
            featured.AddRange(SplitArtistList(match.Groups["artists"].Value));
            return string.Empty;
        });
        cleaned = FeaturedTrailingRegex().Replace(cleaned, match =>
        {
            featured.AddRange(SplitArtistList(match.Groups["artists"].Value));
            return string.Empty;
        });

        return (CollapseWhitespace(cleaned), featured);
    }

    /// <summary>
    /// Splits "Artist feat. Other" into the primary artist and the featured artists. Joint
    /// billing such as "Martha Argerich &amp; Daniel Barenboim" is kept as one artist.
    /// </summary>
    public static (string? Artist, List<string> Featured) SplitArtist(string? artist)
    {
        var featured = new List<string>();
        if (string.IsNullOrWhiteSpace(artist))
        {
            return (null, featured);
        }

        var match = ArtistFeaturedRegex().Match(artist);
        if (!match.Success)
        {
            return (artist.Trim(), featured);
        }

        featured.AddRange(SplitArtistList(match.Groups["artists"].Value));
        var primary = artist[..match.Index].Trim();
        return (primary.Length == 0 ? artist.Trim() : primary, featured);
    }

    /// <summary>
    /// Classifies a release from its raw album name and flags. Apple marks singles and EPs
    /// with an album suffix; compilations are flagged, credited to Various Artists, or named
    /// like one ("Greatest Hits", "The Beethoven Collection").
    /// </summary>
    public static ReleaseType DetectReleaseType(string? rawAlbum, string? albumArtist, bool isCompilation, int? trackCount)
    {
        var album = rawAlbum?.Trim() ?? string.Empty;
        if (album.EndsWith(" - Single", StringComparison.OrdinalIgnoreCase))
        {
            return ReleaseType.Single;
        }

        if (album.EndsWith(" - EP", StringComparison.OrdinalIgnoreCase))
        {
            return ReleaseType.EP;
        }

        if (isCompilation || IsCompilationName(album) || string.Equals(albumArtist?.Trim(), VariousArtists, StringComparison.OrdinalIgnoreCase))
        {
            return ReleaseType.Compilation;
        }

        return trackCount == 1 ? ReleaseType.Single : ReleaseType.Album;
    }

    /// <summary>
    /// Returns whether an album name reads like a compilation.
    /// </summary>
    public static bool IsCompilationName(string? album)
        => !string.IsNullOrEmpty(album) && CompilationNameRegex().IsMatch(album);

    /// <summary>
    /// Builds the key identifying a song regardless of release: primary artist plus title, with
    /// "feat." credits and remaster labels removed, compared case- and punctuation-insensitively.
    /// </summary>
    public static string SongKey(string? artist, string? title)
    {
        var (primary, _) = SplitArtist(artist);
        var (cleanTitle, _) = ExtractFeatured(title);
        return Normalize(primary) + "|" + Normalize(StripTitleDesignations(cleanTitle));
    }

    /// <summary>
    /// Builds the key identifying an album across editions: album artist (or artist) plus the
    /// cleaned album name, compared case- and punctuation-insensitively.
    /// </summary>
    public static string AlbumKey(string? albumArtist, string? artist, string? album)
    {
        var (primary, _) = SplitArtist(string.IsNullOrWhiteSpace(albumArtist) ? artist : albumArtist);
        return Normalize(primary) + "|" + Normalize(CleanAlbum(album));
    }

    /// <summary>
    /// Lowercases and keeps only letters and digits, so "Clair De Lune" and "Clair de lune"
    /// compare equal.
    /// </summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString();
    }

    private static string StripTitleDesignations(string title)
    {
        var cleaned = BracketRegex().Replace(title, match => IsTitleDesignation(match.Groups["inner"].Value) ? string.Empty : match.Value);
        cleaned = DashSuffixRegex().Replace(cleaned, match => IsTitleDesignation(match.Groups["inner"].Value) ? string.Empty : match.Value);
        return CollapseWhitespace(cleaned);
    }

    // Album edition labels. Labels naming a different recording ("Acoustic Version",
    // "Live Edition", a performer's "… 's Version") stay, so those albums are not merged.
    private static bool IsAlbumDesignation(string inner)
        => AlbumDesignationRegex().IsMatch(inner) && !DistinctRecordingRegex().IsMatch(inner);

    // Only remaster and bonus labels are noise in a title; Live, Remix, Acoustic, Radio Edit
    // and the like name a different recording and are kept.
    private static bool IsTitleDesignation(string inner)
        => TitleDesignationRegex().IsMatch(inner);

    private static string[] SplitArtistList(string artists)
        => artists.Split(_featuredSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string CollapseWhitespace(string value)
        => WhitespaceRegex().Replace(value, " ").Trim();

    [GeneratedRegex(@"\s+-\s+(Single|EP)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ReleaseSuffixRegex();

    [GeneratedRegex(@"\s*[\(\[](?<inner>[^\(\)\[\]]*)[\)\]]")]
    private static partial Regex BracketRegex();

    [GeneratedRegex(@"\s+-\s+(?<inner>[^-]+)$")]
    private static partial Regex DashSuffixRegex();

    [GeneratedRegex(@"\s*[\(\[](?:feat\.?|ft\.?|featuring|with)\s+(?<artists>[^\(\)\[\]]+)[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex FeaturedBracketRegex();

    [GeneratedRegex(@"\s+(?:feat\.|ft\.|featuring)\s+(?<artists>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex FeaturedTrailingRegex();

    [GeneratedRegex(@"\s+(?:feat\.?|ft\.?|featuring)\s+(?<artists>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ArtistFeaturedRegex();

    [GeneratedRegex(@"\b(deluxe|edition|remaster(ed)?|bonus|expanded|anniversary|special|collector'?s|reissue|legacy|platinum|explicit|clean|version)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AlbumDesignationRegex();

    [GeneratedRegex(@"\b(live|acoustic|instrumental|remix(es)?|demos?|unplugged|orchestral|a cappella)\b|\w+'s\s+version", RegexOptions.IgnoreCase)]
    private static partial Regex DistinctRecordingRegex();

    [GeneratedRegex(@"\b(remaster(ed)?|bonus track)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TitleDesignationRegex();

    [GeneratedRegex(@"\b(greatest hits|best of|hits|collection|anthology|essentials?|singles|retrospective|studio albums)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CompilationNameRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
