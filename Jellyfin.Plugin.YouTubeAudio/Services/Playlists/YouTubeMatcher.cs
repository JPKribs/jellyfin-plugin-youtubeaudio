using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.YouTubeAudio.Models;

namespace Jellyfin.Plugin.YouTubeAudio.Services.Playlists;

/// <summary>
/// A YouTube search result considered as a match for a playlist track.
/// </summary>
/// <param name="Id">The video ID.</param>
/// <param name="Title">The video title.</param>
/// <param name="Channel">The uploading channel, when known.</param>
/// <param name="DurationSeconds">The video length, when known.</param>
/// <param name="Album">The album YouTube Music lists the song on, when known.</param>
public sealed record MatchCandidate(string Id, string Title, string? Channel, double? DurationSeconds, string? Album = null);

/// <summary>
/// The chosen candidate for a playlist track.
/// </summary>
/// <param name="Candidate">The chosen search result.</param>
/// <param name="DurationDeltaSeconds">The absolute length difference, when both lengths are known.</param>
/// <param name="ChannelMatchesArtist">Whether the upload came from the artist's own channel.</param>
public sealed record MatchResult(MatchCandidate Candidate, double? DurationDeltaSeconds, bool ChannelMatchesArtist);

/// <summary>
/// Picks the YouTube upload that best matches a playlist track. Pure scoring logic, kept apart
/// from yt-dlp so it can be tested against captured search results.
/// </summary>
public static partial class YouTubeMatcher
{
    /// <summary>How many search results to consider per track.</summary>
    internal const int SearchResultCount = 5;

    /// <summary>How many YouTube Music song results to look at when the regular search finds nothing.</summary>
    internal const int MusicSearchResultCount = 3;

    /// <summary>Candidates whose length differs by more than this are rejected outright.</summary>
    internal const double MaxDurationDeltaSeconds = 8;

    /// <summary>Accepted matches whose length differs by more than this get a review note.</summary>
    internal const double NoteDurationDeltaSeconds = 3;

    // Words marking a longer or looped edit. Penalized unless the source title contains them too.
    private static readonly string[] _alternateVersionWords = ["extended", "loop"];

    /// <summary>
    /// Words marking a different recording or an altered copy: live and concert recordings,
    /// remixes, covers, acoustic, instrumental and karaoke versions, and re-pitched or sped-up
    /// uploads. Never matched unless the source title names that version itself.
    /// </summary>
    internal static readonly string[] OtherVersionWords =
    [
        "live", "concert", "unplugged", "remix", "rmx", "cover", "acoustic", "instrumental", "karaoke",
        "reaction", "dj set", "slowed", "reverb", "432hz", "432 hz", "528hz", "sped up", "speed up", "8d", "nightcore"
    ];

    /// <summary>A song by an artist written in another alphabet must be this close in length when its album differs.</summary>
    internal const double OtherScriptMaxDeltaSeconds = 3;

    private static readonly string[] _artistSeparators = ["&", ",", "/", " x ", " and ", " feat. ", " feat ", " ft. ", " with "];

    /// <summary>
    /// Builds the YouTube search text for a track.
    /// </summary>
    public static string BuildSearchQuery(PlaylistTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);

        var query = string.IsNullOrWhiteSpace(track.Artist)
            ? track.Title
            : track.Artist + " - " + track.Title;

        // yt-dlp receives the query on its command line; quotes (or a backslash escaping the
        // closing quote) would break the argument.
        return query.Replace("\"", string.Empty, StringComparison.Ordinal).Replace("\\", string.Empty, StringComparison.Ordinal).Trim();
    }

    /// <summary>
    /// Builds a YouTube Music search for a track's song results. YouTube Music lists the
    /// official audio (the album track's exact length) first, where a regular search often
    /// shows only music videos with intros, or nothing at all for words YouTube filters when
    /// signed out.
    /// </summary>
    public static string BuildMusicSearchUrl(PlaylistTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        var query = string.IsNullOrWhiteSpace(track.Artist) ? track.Title : track.Artist + " " + track.Title;
        return "https://music.youtube.com/search?q=" + Uri.EscapeDataString(query.Trim()) + "#songs";
    }

    /// <summary>
    /// Returns the best candidate, or null when none is acceptable. A candidate must be close
    /// in length (when both lengths are known) and come from the artist's channel, or name the
    /// artist in a title that carries the track title (or, for classical titles, the movement). Among the rest, the artist's own channel and the closest length win;
    /// alternate versions (slowed, live, cover…) lose unless the source title asks for them.
    /// </summary>
    public static MatchResult? PickBest(PlaylistTrack track, IEnumerable<MatchCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(candidates);

        var artistKeys = GetArtistKeys(track.Artist);
        foreach (var featured in track.FeaturedArtists ?? [])
        {
            artistKeys.AddRange(GetArtistKeys(featured).Where(key => !artistKeys.Contains(key)));
        }

        var titleWords = Tokenize(track.Title);
        // The title without its bracketed parts: "Times Like These (Live Acoustic 2006)" is
        // still that song when an upload words the version differently.
        var titleCore = Tokenize(TitleBracketRegex().Replace(track.Title ?? string.Empty, " "));
        var sourceTitleText = " " + string.Join(' ', titleWords) + " ";
        var tailWords = GetDistinctiveTitleWords(track.Title);
        var artistNames = new[] { track.Artist }.Concat(track.FeaturedArtists ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .SelectMany(name => name!.Split(_artistSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Append(name!))
            .Select(Tokenize)
            .Where(words => words.Count > 0)
            .ToList();

        MatchResult? best = null;
        var bestScore = double.MinValue;

        foreach (var candidate in candidates)
        {
            double? delta = track.DurationSeconds.HasValue && candidate.DurationSeconds.HasValue
                ? Math.Abs(track.DurationSeconds.Value - candidate.DurationSeconds.Value)
                : null;
            if (delta > MaxDurationDeltaSeconds)
            {
                continue;
            }

            var (channelKey, isTopic) = NormalizeChannel(candidate.Channel);
            var channelMatch = channelKey.Length > 0 && artistKeys.Any(key => KeysMatch(key, channelKey));

            var candidateWords = Tokenize(candidate.Title);
            var titleMatch = titleWords.Count > 0 && titleWords.All(candidateWords.Contains);
            var coreMatch = !titleMatch && titleCore.Count > 0 && titleCore.All(candidateWords.Contains);

            // Classical uploads rarely repeat every catalogue detail ("Suite bergamasque, L. 75:
            // III. Clair de lune" uploaded as "Lang Lang - Debussy: Clair de lune"), so the
            // movement name alone can match the title.
            var tailMatch = !titleMatch && tailWords.Count > 0 && tailWords.All(candidateWords.Contains);
            var artistInTitle = !channelMatch && artistNames.Any(words => words.All(candidateWords.Contains));

            // YouTube Music may credit the artist in another alphabet ("ЛЮТИК" for "LUTIC"), which
            // no name comparison can match. Then the album, or a near-exact length, has to vouch.
            var otherScript = !channelMatch && !artistInTitle && WrittenInOtherScript(track.Artist, candidate.Channel)
                && (AlbumsMatch(track.Album, candidate.Album) || delta <= OtherScriptMaxDeltaSeconds);

            // An upload must be this song: the title (or its movement) has to appear, even on the
            // artist's own channel, where another song of a similar length would otherwise pass.
            // It must also show it is by the artist: their (or their label's) channel, or their
            // name in the title. A matching title alone may be another performer's recording.
            if (!(titleMatch || coreMatch || tailMatch) || (!channelMatch && !artistInTitle && !otherScript))
            {
                continue;
            }

            var candidateTitleText = " " + string.Join(' ', candidateWords) + " ";
            if (IsOtherVersion(candidate.Title, track.Title))
            {
                continue;
            }
            var alternateVersion = _alternateVersionWords.Any(word =>
                candidateTitleText.Contains(" " + word + " ", StringComparison.Ordinal)
                && !sourceTitleText.Contains(" " + word + " ", StringComparison.Ordinal));

            var score = 0.0;
            score += channelMatch ? 50 : 0;
            score += titleMatch ? 30 : 0;
            score += coreMatch ? 20 : 0;
            score += tailMatch ? 15 : 0;
            score += artistInTitle ? 25 : 0;
            score += otherScript ? 20 : 0;
            score += isTopic ? 10 : 0;
            score -= alternateVersion ? 25 : 0;
            score -= (delta ?? 0) * 3;
            score += candidateTitleText.Contains(" official audio ", StringComparison.Ordinal) ? 5 : 0;
            score -= candidateTitleText.Contains(" video ", StringComparison.Ordinal) ? 3 : 0;

            // Strictly greater keeps YouTube's own ranking as the tie-breaker.
            if (score > bestScore)
            {
                bestScore = score;
                best = new MatchResult(candidate, delta, channelMatch);
            }
        }

        return best;
    }

    /// <summary>
    /// Describes anything about a match worth a second look, or null when it looks right.
    /// </summary>
    public static string? DescribeConcerns(MatchResult match)
    {
        ArgumentNullException.ThrowIfNull(match);

        var notes = new List<string>();
        if (match.DurationDeltaSeconds > NoteDurationDeltaSeconds)
        {
            notes.Add(string.Format(CultureInfo.InvariantCulture, "Length differs by {0:0}s", match.DurationDeltaSeconds));
        }

        if (!match.ChannelMatchesArtist)
        {
            notes.Add("Not uploaded by the artist");
        }

        return notes.Count == 0 ? null : string.Join("; ", notes) + ". Check the match before importing.";
    }

    /// <summary>
    /// Gets the words that identify a piece within a long classical title: the part after the
    /// last colon (the movement or aria), without its numbering. "Suite bergamasque, L. 75: III.
    /// Clair de lune" gives "clair de lune"; a title without a colon gives nothing.
    /// </summary>
    internal static List<string> GetDistinctiveTitleWords(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return new List<string>();
        }

        var colon = title.LastIndexOf(':');
        if (colon < 0 || colon == title.Length - 1)
        {
            return new List<string>();
        }

        var words = Tokenize(title[(colon + 1)..]);
        while (words.Count > 1 && IsNumbering(words[0]))
        {
            words.RemoveAt(0);
        }

        return words.Count > 0 && words.Any(w => w.Length > 2 && !IsNumbering(w)) ? words : new List<string>();

        static bool IsNumbering(string word)
            => word == "no" || word.All(char.IsDigit) || (word.Length <= 5 && word.All(c => "ivxlc".Contains(c, StringComparison.Ordinal)));
    }

    /// <summary>
    /// Returns whether a candidate title names a version (live, remix, cover…) that the source
    /// title does not: "Clair de lune (Live)" is another recording of "Clair de lune", while
    /// "Times Like These (Live Acoustic 2006)" may match a live acoustic upload.
    /// </summary>
    internal static bool IsOtherVersion(string? candidateTitle, string? sourceTitle)
    {
        var candidate = " " + string.Join(' ', Tokenize(candidateTitle)) + " ";
        var source = " " + string.Join(' ', Tokenize(sourceTitle)) + " ";
        return OtherVersionWords.Any(word =>
            candidate.Contains(" " + word + " ", StringComparison.Ordinal)
            && !source.Contains(" " + word + " ", StringComparison.Ordinal));
    }

    /// <summary>
    /// Returns whether two names are written in different alphabets: one only in Latin letters,
    /// the other mostly in another script (Cyrillic, Greek, CJK…).
    /// </summary>
    internal static bool WrittenInOtherScript(string? a, string? b)
    {
        static bool? IsLatin(string? value)
        {
            var letters = (value ?? string.Empty).Where(char.IsLetter).ToList();
            if (letters.Count == 0)
            {
                return null;
            }

            var latin = letters.Count(c => c < 0x250);
            return latin == letters.Count ? true : latin * 2 < letters.Count ? false : null;
        }

        var la = IsLatin(a);
        var lb = IsLatin(b);
        return la.HasValue && lb.HasValue && la.Value != lb.Value;
    }

    /// <summary>
    /// Returns whether two album names name the same release, ignoring case, punctuation and
    /// edition labels such as " - Single".
    /// </summary>
    private static bool AlbumsMatch(string? a, string? b)
    {
        var keyA = Normalize(TrackMetadataCleaner.CleanAlbum(a) ?? string.Empty);
        var keyB = Normalize(TrackMetadataCleaner.CleanAlbum(b) ?? string.Empty);
        return keyA.Length > 0 && keyA == keyB;
    }

    private static List<string> GetArtistKeys(string? artist)
    {
        var keys = new List<string>();
        if (string.IsNullOrWhiteSpace(artist))
        {
            return keys;
        }

        AddKey(keys, artist);
        foreach (var part in artist.Split(_artistSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            AddKey(keys, part);
        }

        return keys;

        static void AddKey(List<string> keys, string value)
        {
            var key = Normalize(value);
            if (key.Length > 0 && !keys.Contains(key))
            {
                keys.Add(key);
            }
        }
    }

    /// <summary>
    /// Normalizes a channel name, stripping the " - Topic" (auto-generated artist channel) and
    /// "VEVO" suffixes, and reports whether it was a Topic channel.
    /// </summary>
    private static (string Key, bool IsTopic) NormalizeChannel(string? channel)
    {
        if (string.IsNullOrWhiteSpace(channel))
        {
            return (string.Empty, false);
        }

        var name = channel.Trim();
        var isTopic = name.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase);
        if (isTopic)
        {
            name = name[..^" - Topic".Length];
        }

        var key = Normalize(name);
        if (key.EndsWith("vevo", StringComparison.Ordinal) && key.Length > "vevo".Length)
        {
            key = key[..^"vevo".Length];
        }

        return (key, isTopic);
    }

    /// <summary>
    /// Matches an artist key against a channel key. Containment covers channels like
    /// "Khatia Buniatishvili official" or "Deutsche Grammophon - DG and Víkingur Ólafsson";
    /// the length floor stops short names from matching inside unrelated channels.
    /// </summary>
    private static bool KeysMatch(string artistKey, string channelKey)
        => artistKey == channelKey
            || (artistKey.Length >= 4 && channelKey.Contains(artistKey, StringComparison.Ordinal))
            || (channelKey.Length >= 4 && artistKey.Contains(channelKey, StringComparison.Ordinal));

    internal static string Normalize(string value)
    {
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

    internal static List<string> Tokenize(string? value)
    {
        var words = new List<string>();
        if (string.IsNullOrEmpty(value))
        {
            return words;
        }

        var sb = new StringBuilder();
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
            else if (sb.Length > 0)
            {
                words.Add(sb.ToString());
                sb.Clear();
            }
        }

        if (sb.Length > 0)
        {
            words.Add(sb.ToString());
        }

        return words;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"[\(\[][^\)\]]*[\)\]]")]
    private static partial System.Text.RegularExpressions.Regex TitleBracketRegex();
}
