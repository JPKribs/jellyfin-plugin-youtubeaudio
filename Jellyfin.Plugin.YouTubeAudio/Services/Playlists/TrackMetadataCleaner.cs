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

    // Separators between several genres in one value ("Classical; Baroque", "Pop/Rock").
    private static readonly char[] _genreSeparators = [';', ',', '/', '|'];

    // Genre names that contain a separator but are one genre.
    private static readonly string[] _compoundGenres = ["Singer/Songwriter"];

    // Genre words written in capitals (or a fixed form) rather than title case.
    private static readonly Dictionary<string, string> _genreWordForms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["r&b"] = "R&B", ["rnb"] = "R&B", ["edm"] = "EDM", ["idm"] = "IDM", ["ebm"] = "EBM", ["uk"] = "UK", ["us"] = "US",
        ["dj"] = "DJ", ["ost"] = "OST", ["mpb"] = "MPB", ["k-pop"] = "K-Pop", ["j-pop"] = "J-Pop", ["c-pop"] = "C-Pop",
        ["j-rock"] = "J-Rock", ["lo-fi"] = "Lo-Fi", ["hi-fi"] = "Hi-Fi", ["a cappella"] = "A Cappella"
    };

    // Spellings of one genre (or one genre that stands for several) mapped to the standard names.
    private static readonly Dictionary<string, string[]> _genreAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hip hop"] = ["Hip-Hop"], ["hip-hop"] = ["Hip-Hop"], ["hiphop"] = ["Hip-Hop"],
        ["r&b"] = ["R&B"], ["rnb"] = ["R&B"], ["r and b"] = ["R&B"], ["rhythm and blues"] = ["R&B"], ["rhythm & blues"] = ["R&B"],
        ["electronica"] = ["Electronic"], ["electro"] = ["Electronic"], ["electronic music"] = ["Electronic"],
        ["edm"] = ["Electronic", "Dance"], ["electronic dance music"] = ["Electronic", "Dance"],
        ["drum and bass"] = ["Drum & Bass"], ["drum & bass"] = ["Drum & Bass"], ["drum n bass"] = ["Drum & Bass"], ["dnb"] = ["Drum & Bass"], ["d&b"] = ["Drum & Bass"],
        ["lofi"] = ["Lo-Fi"], ["lo fi"] = ["Lo-Fi"], ["lo-fi"] = ["Lo-Fi"],
        ["singer-songwriter"] = ["Singer/Songwriter"], ["singer songwriter"] = ["Singer/Songwriter"],
        ["ost"] = ["Soundtrack"], ["film score"] = ["Soundtrack"], ["soundtracks"] = ["Soundtrack"],
        ["alt"] = ["Alternative"]
    };

    // Standard genres that compound genres are built from. A genre made only of these words
    // ("alt-pop", "Indie Rock", "Pop Punk") is split into them; anything else is kept whole.
    private static readonly Dictionary<string, string> _baseGenreWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["alt"] = "Alternative", ["alternative"] = "Alternative", ["indie"] = "Indie", ["pop"] = "Pop", ["rock"] = "Rock",
        ["metal"] = "Metal", ["punk"] = "Punk", ["folk"] = "Folk", ["jazz"] = "Jazz", ["soul"] = "Soul", ["funk"] = "Funk",
        ["blues"] = "Blues", ["country"] = "Country", ["dance"] = "Dance", ["electronic"] = "Electronic", ["electro"] = "Electronic",
        ["electronica"] = "Electronic", ["house"] = "House", ["techno"] = "Techno", ["trance"] = "Trance", ["ambient"] = "Ambient",
        ["classical"] = "Classical", ["gospel"] = "Gospel", ["reggae"] = "Reggae", ["latin"] = "Latin", ["disco"] = "Disco",
        ["grunge"] = "Grunge", ["emo"] = "Emo", ["rap"] = "Rap", ["opera"] = "Opera", ["choral"] = "Choral", ["orchestral"] = "Orchestral"
    };

    private static readonly char[] _genreWordSeparators = [' ', '-'];

    // Short words kept lowercase inside a genre name ("Drum and Bass", "Music of the Night").
    private static readonly HashSet<string> _genreMinorWords = new(StringComparer.OrdinalIgnoreCase) { "and", "of", "the", "n" };

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

        var genres = CleanGenres(track.Genre);

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
            Genre = genres.Length > 0 ? string.Join("; ", genres) : null,
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
    /// Splits a genre value into separate, consistently capitalized, standard genres:
    /// "classical;baroque" becomes "Classical" and "Baroque", "Pop/Rock" becomes "Pop" and "Rock",
    /// a compound of standard genres is broken down ("alt-pop" becomes "Alternative" and "Pop"),
    /// and spellings of one genre are unified ("electronica" becomes "Electronic"). Genres that are
    /// not built only from standard ones ("Synthwave", "Contemporary Classical") stay whole,
    /// compound names ("Singer/Songwriter", "Drum &amp; Bass") are kept, and duplicates are dropped.
    /// </summary>
    public static string[] CleanGenres(string? genre)
    {
        if (string.IsNullOrWhiteSpace(genre))
        {
            return [];
        }

        // Protect compound names from the separator split.
        var text = genre;
        foreach (var compound in _compoundGenres)
        {
            text = text.Replace(compound, compound.Replace('/', '\u0001'), StringComparison.OrdinalIgnoreCase);
        }

        var result = new List<string>();
        foreach (var part in text.Split(_genreSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var name in StandardizeGenre(CollapseWhitespace(part.Replace('\u0001', '/'))))
            {
                if (name.Length > 0 && !result.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(name);
                }
            }
        }

        return result.ToArray();
    }

    /// <summary>
    /// Maps one genre to its standard name(s): a known alias, else a breakdown into standard
    /// genres when every word is one ("Alt Pop" -> Alternative, Pop), else the genre itself,
    /// capitalized.
    /// </summary>
    private static string[] StandardizeGenre(string genre)
    {
        if (genre.Length == 0)
        {
            return [];
        }

        if (_genreAliases.TryGetValue(genre, out var alias))
        {
            return alias;
        }

        var words = genre.Split(_genreWordSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 1 && words.All(_baseGenreWords.ContainsKey))
        {
            return words.Select(w => _baseGenreWords[w]).ToArray();
        }

        return [CapitalizeGenre(genre)];
    }

    private static string CapitalizeGenre(string genre)
    {
        if (_genreWordForms.TryGetValue(genre, out var whole))
        {
            return whole;
        }

        var words = genre.Split(' ');
        for (var i = 0; i < words.Length; i++)
        {
            var word = words[i];
            if (_genreWordForms.TryGetValue(word, out var form))
            {
                words[i] = form;
            }
            else if (i > 0 && _genreMinorWords.Contains(word))
            {
                words[i] = word.ToLowerInvariant();
            }
            else if (word.Length is >= 2 and <= 4 && word.All(char.IsUpper))
            {
                // A short all-capitals word is an acronym ("CCM", "IDM"): keep it as written.
                words[i] = word;
            }
            else
            {
                // Capitalize each joined piece: "hip-hop" -> "Hip-Hop", "singer/songwriter" -> "Singer/Songwriter".
                var chars = word.ToLowerInvariant().ToCharArray();
                for (var c = 0; c < chars.Length; c++)
                {
                    if (c == 0 || chars[c - 1] is '-' or '&' or '(' or '/')
                    {
                        chars[c] = char.ToUpperInvariant(chars[c]);
                    }
                }

                words[i] = new string(chars);
            }
        }

        return string.Join(' ', words);
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
