using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.YouTubeAudio.Models;
using Jellyfin.Plugin.YouTubeAudio.Services;
using Jellyfin.Plugin.YouTubeAudio.Services.Playlists;
using Xunit;

namespace Jellyfin.Plugin.YouTubeAudio.Tests;

/// <summary>
/// Tests for reading playlist exports and choosing YouTube matches.
/// </summary>
public class PlaylistImportTests
{
    private static readonly IPlaylistSource[] _sources = [new AppleMusicPlaylistSource(), new AppleMusicLibrarySource()];

    // TestData/AppleMusicPlaylist.txt is a synthetic 12-track classical playlist written in the
    // Music app's exact export format: UTF-16 LE with BOM, bare CR line endings, and data rows
    // one column shorter than the header.
    private static ParsedPlaylist ParseFixture()
    {
        var bytes = File.ReadAllBytes(Path.Combine("TestData", "AppleMusicPlaylist.txt"));
        var (source, playlists) = PlaylistImportService.Parse(_sources, "Study Session.txt", PlaylistImportService.DecodeText(bytes));
        Assert.Equal(AppleMusicPlaylistSource.SourceId, source.Id);
        return Assert.Single(playlists);
    }

    private static string AppleExport(string header, params string[] rows)
        => string.Join('\r', new[] { header }.Concat(rows));

    // MARK: Apple Music export parsing

    [Fact]
    public void AppleMusic_ParsesEveryTrackFromExport()
    {
        var playlist = ParseFixture();

        Assert.Equal("Study Session", playlist.Name);
        Assert.Equal(12, playlist.Tracks.Count);
        Assert.Equal(0, playlist.SkippedRows);

        var first = playlist.Tracks[0];
        Assert.Equal("Clair de lune", first.Title);
        Assert.Equal("Lang Lang", first.Artist);
        Assert.Equal("Claude Debussy", first.Composer);
        Assert.Equal("Classical", first.Genre);
        Assert.Equal(2019, first.Year);
        Assert.Equal(408, first.DurationSeconds);
    }

    [Fact]
    public void AppleMusic_ReadsAlbumPositionAndKeepsMultiArtistVerbatim()
    {
        var tracks = ParseFixture().Tracks;

        var liebestraum = tracks.Single(t => t.Title.StartsWith("Liebestraum", StringComparison.Ordinal));
        Assert.Equal("Liszt", liebestraum.Album);
        Assert.Equal(10, liebestraum.TrackNumber);
        Assert.Equal(12, liebestraum.TrackCount);
        Assert.Equal(1, liebestraum.DiscNumber);
        Assert.Equal(1, liebestraum.DiscCount);

        Assert.Equal("Sheku Kanneh-Mason & Isata Kanneh-Mason", tracks.Single(t => t.Title == "The Swan").Artist);
    }

    [Fact]
    public void AppleMusic_KeepsRawAlbumSoTheImportCanTellTheReleaseType()
    {
        var tracks = ParseFixture().Tracks;

        Assert.Equal("Méditation - Single", tracks.Single(t => t.Title == "Méditation").Album);
        Assert.Equal("The Swan - EP", tracks.Single(t => t.Title == "The Swan").Album);
    }

    [Fact]
    public void AppleMusic_FallsBackToColumnPositionsForLocalizedHeaders()
    {
        // A German Music app writes translated headers but the same 31-column layout.
        var header = string.Join('\t', new[] { "Name", "Künstler" }.Concat(Enumerable.Range(0, 29).Select(i => "Spalte" + i)));
        var cells = new string[31];
        cells[0] = "Clair de lune";
        cells[1] = "Lang Lang";
        cells[3] = "Piano Book";
        cells[11] = "408";
        cells[14] = "3";
        cells[16] = "2019";
        var content = AppleExport(header, string.Join('\t', cells.Select(c => c ?? string.Empty)));

        var source = new AppleMusicPlaylistSource();
        Assert.True(source.CanParse("Playlist.txt", content));
        var track = Assert.Single(Assert.Single(source.Parse("Playlist.txt", content)).Tracks);
        Assert.Equal("Lang Lang", track.Artist);
        Assert.Equal("Piano Book", track.Album);
        Assert.Equal(408, track.DurationSeconds);
        Assert.Equal(3, track.TrackNumber);
        Assert.Equal(2019, track.Year);
    }

    [Fact]
    public void AppleMusic_SkipsRowsWithoutTitleAndToleratesShortRows()
    {
        var content = AppleExport(
            "Name\tArtist\tComposer\tAlbum",
            "Für Elise\tLang Lang",
            "\tNo Title Artist\t\tAlbum",
            string.Empty);

        var playlist = Assert.Single(new AppleMusicPlaylistSource().Parse("p.txt", content));

        var track = Assert.Single(playlist.Tracks);
        Assert.Equal("Für Elise", track.Title);
        Assert.Null(track.Album);
        Assert.Null(track.DurationSeconds);
        Assert.Equal(1, playlist.SkippedRows);
    }

    [Theory]
    [InlineData("Track Name,Artist Name(s),Album Name\nClair de lune,Lang Lang,Piano Book")]
    [InlineData("just some notes")]
    [InlineData("")]
    public void Parse_RejectsUnsupportedFiles(string content)
        => Assert.Throws<ArgumentException>(() => PlaylistImportService.Parse(_sources, "file.csv", content));

    [Fact]
    public void DecodeText_HandlesUtf8WithoutBom()
        => Assert.Equal("Name\tArtist", PlaylistImportService.DecodeText(Encoding.UTF8.GetBytes("Name\tArtist")));

    // MARK: Apple Music library export

    // TestData/AppleMusicLibrary.xml is a synthetic classical library in the "Export Library…"
    // property-list format: the Library and Music built-ins, the "Study Session" playlist,
    // "Sunday Mix" (two Various Artists compilation tracks, a single, overlap with Study Session,
    // and a dangling track ID), an empty playlist and a folder.
    private static List<ParsedPlaylist> ParseLibraryFixture()
    {
        var content = PlaylistImportService.DecodeText(File.ReadAllBytes(Path.Combine("TestData", "AppleMusicLibrary.xml")));
        var (source, playlists) = PlaylistImportService.Parse(_sources, "Library.xml", content);
        Assert.Equal(AppleMusicLibrarySource.SourceId, source.Id);
        return playlists;
    }

    [Fact]
    public void AppleLibrary_ListsUserPlaylistsThenEntireLibrary()
    {
        var playlists = ParseLibraryFixture();

        // Built-in views, empty playlists and folders are left out.
        Assert.Equal(["Study Session", "Sunday Mix", "Entire Library"], playlists.Select(p => p.Name));
        Assert.Equal(AppleMusicLibrarySource.EntireLibraryId, playlists[^1].Id);
        Assert.Equal(12, playlists[0].Tracks.Count);
        Assert.Equal(17, playlists[^1].Tracks.Count);
    }

    [Fact]
    public void AppleLibrary_ReadsAlbumArtistAndMillisecondLengths()
    {
        var sundayMix = ParseLibraryFixture().Single(p => p.Name == "Sunday Mix");

        var compilation = sundayMix.Tracks[0];
        Assert.Equal("Canon in D", compilation.Title);
        Assert.Equal("Academy of St Martin in the Fields & Sir Neville Marriner", compilation.Artist);
        Assert.Equal("Various Artists", compilation.AlbumArtist);
        Assert.Equal("Classical Favourites", compilation.Album);
        Assert.Equal(176.373, compilation.DurationSeconds!.Value, precision: 3);
        Assert.True(compilation.IsCompilation);

        Assert.Equal("Gnossienne No. 1 - Single", sundayMix.Tracks.Single(t => t.Title == "Gnossienne No. 1").Album);
        Assert.Equal(1, sundayMix.SkippedRows);
    }

    [Fact]
    public void AppleLibrary_ReadsSinglePlaylistXmlExport()
    {
        // "Export Playlist…" as XML has the same shape with one playlist and no built-ins.
        var content = PlaylistImportService.DecodeText(File.ReadAllBytes(Path.Combine("TestData", "AppleMusicLibrary.xml")));
        var root = System.Xml.Linq.XDocument.Parse(content);
        var playlistArray = root.Descendants("array").First();
        foreach (var dict in playlistArray.Elements("dict").Where((_, index) => index != 2).ToList())
        {
            dict.Remove();
        }
        var playlists = new AppleMusicLibrarySource().Parse("Study Session.xml", root.ToString());

        var single = Assert.Single(playlists);
        Assert.Equal("Study Session", single.Name);
        Assert.Same(single, Assert.Single(PlaylistImportService.SelectPlaylists(playlists, null)!));
    }

    [Fact]
    public void AppleLibrary_TextExportIsNotMistakenForLibrary()
    {
        var content = PlaylistImportService.DecodeText(File.ReadAllBytes(Path.Combine("TestData", "AppleMusicPlaylist.txt")));
        Assert.False(new AppleMusicLibrarySource().CanParse("Study Session.txt", content));
    }

    // MARK: Playlist selection

    [Fact]
    public void SelectPlaylists_RequiresAChoiceWhenFileHoldsSeveral()
    {
        var playlists = ParseLibraryFixture();

        Assert.Null(PlaylistImportService.SelectPlaylists(playlists, null));
        Assert.Null(PlaylistImportService.SelectPlaylists(playlists, [string.Empty]));

        var chosen = PlaylistImportService.SelectPlaylists(playlists, [playlists[1].Id!, playlists[0].Id!, playlists[1].Id!])!;
        Assert.Equal(["Sunday Mix", "Study Session"], chosen.Select(p => p.Name));

        Assert.Throws<ArgumentException>(() => PlaylistImportService.SelectPlaylists(playlists, ["nope"]));
    }

    [Fact]
    public void Plan_QueuesEachSongOnceAcrossSelectedPlaylists()
    {
        var playlists = ParseLibraryFixture();

        var plan = ImportPlanner.Plan(playlists.Take(2).SelectMany(p => p.Tracks), [], []);

        // Sunday Mix shares two tracks with Study Session: 12 + 7 - 2.
        Assert.Equal(17, plan.Tracks.Count);
        Assert.Equal(2, plan.Duplicates);
        Assert.Equal("Clair de lune", plan.Tracks[0].Title);
        Assert.Equal("Piano Book", plan.Tracks[0].Album);
    }

    // MARK: YouTube matching

    // Real ytsearch5 results (id, title, channel, duration) captured for tracks in the fixture,
    // with the upload that should be chosen: the artist's own or Topic channel, closest in length.
    public static TheoryData<string, string, MatchCandidate[]> CapturedSearches => new()
    {
        {
            "Clair de lune", "85aTjlh66WA",
            [
                new("fZrm9h3JRGs", "Lang Lang – Debussy: Suite bergamasque, L.75: III. Clair de lune", "Deutsche Grammophon - DG", 406),
                new("j0tK9F_6g2w", "Suite bergamasque, L. 75: 3. Clair de lune (Live from Hôtel de Ville, Paris / 2022)", "Lang Lang", 389),
                new("Z3P1vxRIIhM", "Lang Lang - Clair de Lune, Debussy – West Eastern Divan Orchestra – 2022", "ZK Lofi Tone", 458),
                new("85aTjlh66WA", "Debussy: Clair de lune, CD 82 No. 3", "Lang Lang", 408),
                new("Nw5LWwf8i3w", "Lang Lang - Debussy Clair de Lune", "Richard", 367)
            ]
        },
        {
            "Für Elise", "k_ZLxAPro3w",
            [
                new("s71I_EWJk7I", "\"Für Elise\" Performed by Lang Lang", "Steinway & Sons", 230),
                new("q5oSa4itomE", "Lang Lang performs Beethoven's \"Für Elise\"", "WQXR", 225),
                new("FW_KCau2iTI", "Lang Lang – Beethoven: \"Für Elise\" Bagatelle No. 25 in A Minor, WoO 59 (Live in Paris, 2019)", "Deutsche Grammophon - DG", 241),
                new("k_ZLxAPro3w", "Beethoven: Für Elise, Bagatelle in A Minor, WoO 59", "Lang Lang", 204),
                new("vBcZwleAG-Q", "Lang Lang - Fur Elise (Track by Track)", "Lang Lang", 164)
            ]
        },
        {
            "Cello Suite No. 1 in G Major, BWV 1007: I. Prélude", "noW9fM9vgJs",
            [
                new("1prweT95Mo0", "Yo-Yo Ma - Bach: Cello Suite No. 1 in G Major, Prélude (Official Video)", "Yo-Yo Ma", 196),
                new("rGgG-0lOJjk", "Yo-Yo Ma Bach Cello Suite No.1 in G Major", "Thanyaphong Na nakorn", 1099),
                new("PCicM6i59_I", "Bach Cello Suite No.1 - Prelude (Yo-Yo Ma)", "Radu Rîcă", 166),
                new("-4WUHJRhvrM", "Unaccompanied Cello Suite No. 1 in G Major, BWV 1007: I. Prélude", "Yo-Yo Ma", 152),
                new("noW9fM9vgJs", "Cello Suite No. 1 in G Major, BWV 1007: I. Prélude", "Yo-Yo Ma", 153)
            ]
        },
        {
            // The label's channel credits the artist too ("… and Víkingur Ólafsson"), but only the
            // artist's own upload carries the full title.
            "Prelude in E Minor, BWV 855", "XTuF30fXHm4",
            [
                new("rtT__umjFVY", "Víkingur Ólafsson – Bach: Prelude & Fugue, BWV 855a: 1. Prelude No. 10 in B Minor (Transcr. Siloti)", "Deutsche Grammophon - DG and Víkingur Ólafsson", 181),
                new("aL8rWu7uGc0", "Víkingur Ólafsson – Bach: Prelude & Fugue, BWV 855a: 1. Prelude No.10 in B Minor (Transcr. Siloti)", "Deutsche Grammophon - DG and Víkingur Ólafsson", 185),
                new("XTuF30fXHm4", "J.S. Bach: Prelude in E Minor, BWV 855/I (Arr. Siloti in B Minor)", "Víkingur Ólafsson", 181),
                new("CGVL5j6BEKs", "Víkingur Ólafsson – Bach: Prelude & Fugue in C minor, BWV 847", "Deutsche Grammophon - DG and Víkingur Ólafsson", 195)
            ]
        },
        {
            "Liebestraum No. 3 in A-Flat Major, S. 541/3", "CSVyDfJST1M",
            [
                new("FZ651tNXp0Y", "Khatia Buniatishvili plays Liebestraum No. 3 from Franz Liszt | Verbier Festival 2011", "DW Classical Music", 333),
                new("CSVyDfJST1M", "Liebestraum No. 3 in A-Flat Major, S. 541/3", "Khatia Buniatishvili official", 329),
                new("Hy-yDyYubL4", "Khatia Buniatishvili - Liszt: Liebestraum No.3 \"Dreams of Love\" A flat major. Encore on concert 2017", "The Best of Classical Music", 410),
                new("qH08xIe4e2I", "Khatia Buniatishvili - Liebestraum No. 3 (Liszt) (iTunes Festival 2014)", "rarethen9", 341)
            ]
        },
        {
            "Méditation", "718UdiQh0CQ",
            [
                new("718UdiQh0CQ", "Massenet: Thaïs: Méditation", "itzhakperlman", 288),
                new("NLhvMgucWns", "Itzhak Perlman - J. Massenet \"Thais\" Meditation", "fairlytaleofnewyork", 280),
                new("EXWDak8d8YM", "Meditation Thais Opera Itzhak Perlman", "MrJesusingo", 135),
                new("EKZwiCJW_CI", "Thaïs, Act 2: Méditation", "itzhakperlman", 288)
            ]
        },
        {
            "Goldberg Variations, BWV 988: Aria", "43sTxRVpRBM",
            [
                new("Gv94m_S3QDo", "Bach - Goldberg Variations: Aria (Glenn Gould)", "kanfoosj", 174),
                new("43sTxRVpRBM", "Goldberg Variations, BWV 988: Aria", "Glenn Gould", 185),
                new("NOS5CJTOOKE", "Glenn Gould plays Bach. The Goldberg Variations. Bruno Monsaingeon, 1981", "GLENN GOULD", 3060),
                new("CguLbkZn6fo", "Glenn Gould - Bach - Goldberg Variations BWV 988 - Aria Da Capo", "Gouldberg CFX", 226),
                new("Xb1GM3Efc-w", "Goldberg Aria BWV 988 - 10 Hours (Glen Gould)", "Marsin Na", 36114)
            ]
        },
        {
            "The Swan", "41lwDMP4NjQ",
            [
                new("41lwDMP4NjQ", "Sheku Kanneh-Mason - The Swan (From Carnival of the Animals)", "Sheku Kanneh-Mason", 156),
                new("10Z1eNq21yc", "Saint-Saëns: Carnival of the Animals - The Swan", "Sheku Kanneh-Mason", 153),
                new("CsZdG8vt0Q4", "Sheku Kanneh-Mason - The Swan - From Carnival of the Animals (Studio Session)", "Sheku Kanneh-Mason", 161),
                new("CqRLuWCn3rc", "Sheku Kanneh-Mason performs The Swan from the Carnival of the Animals (BBC Proms 2021)", "BBC Music", 159)
            ]
        },
        {
            "Kinderszenen, Op. 15: VII. Träumerei", "OippNH2lREU",
            [
                new("QMSB87ICaEM", "Martha Argerich - Traumerie, Scenes of Childhood - Schumann", "Mae L", 168),
                new("OippNH2lREU", "Schumann: Kinderszenen, Op. 15: VII. Träumerei", "Martha Argerich - Topic", 176),
                new("M1_Yjqj9xSY", "Schumann: Von fremden Ländern und Menschen / Argerich", "Berliner Philharmoniker", 121),
                new("5_oQK1KGPaI", "Kinderszenen, Op. 15: No. 7, Träumerei", "Martha Argerich - Topic", 167)
            ]
        }
    };

    [Theory]
    [MemberData(nameof(CapturedSearches))]
    public void PickBest_ChoosesTheRightUploadFromRealSearchResults(string title, string expectedId, MatchCandidate[] candidates)
    {
        var track = ParseFixture().Tracks.Single(t => t.Title == title);

        var match = YouTubeMatcher.PickBest(track, candidates);

        Assert.NotNull(match);
        Assert.Equal(expectedId, match.Candidate.Id);
        Assert.True(match.ChannelMatchesArtist);
    }

    // Real ytsearch5 results for an Apple-style classical title. "Lang Lang - Debussy Clair de
    // Lune" (a fan channel) omits the catalogue details, so it only matches through the artist's
    // name plus the movement name; the DW upload names a different pianist and must not match.
    private static readonly MatchCandidate[] _clairDeLuneSearch =
    [
        new("U3u4pQ4WKOk", "Seong-Jin Cho – Debussy: Suite bergamasque, L.75: III. Clair de lune", "Deutsche Grammophon - DG", 334),
        new("Nw5LWwf8i3w", "Lang Lang - Debussy Clair de Lune", "Richard", 367),
        new("-Bxpm0EmOMU", "Debussy: Clair de lune | Menahem Pressler, piano", "DW Classical Music", 327)
    ];

    [Fact]
    public void PickBest_MatchesClassicalMovementWhenArtistIsInTheTitle()
    {
        var track = new PlaylistTrack { Title = "Suite bergamasque, L. 75: III. Clair de lune", Artist = "Lang Lang", DurationSeconds = 367 };

        var match = YouTubeMatcher.PickBest(track, _clairDeLuneSearch);

        Assert.Equal("Nw5LWwf8i3w", match?.Candidate.Id);
        Assert.False(match!.ChannelMatchesArtist);
    }

    [Fact]
    public void PickBest_DoesNotMatchMovementByAnotherPerformer()
    {
        // Same length as the Pressler upload, but neither his channel nor his name is the artist.
        var track = new PlaylistTrack { Title = "Suite bergamasque, L. 75: III. Clair de lune", Artist = "Lang Lang", DurationSeconds = 327 };

        Assert.Null(YouTubeMatcher.PickBest(track, _clairDeLuneSearch));
    }

    [Theory]
    [InlineData("Suite bergamasque, L. 75: III. Clair de lune", "clair de lune")]
    [InlineData("Kinderszenen, Op. 15: No. 7, Träumerei", "träumerei")]
    [InlineData("Goldberg Variations, BWV 988: Variation 1", "variation 1")]
    [InlineData("Clair de lune", "")]
    [InlineData("Suite: III.", "")]
    public void GetDistinctiveTitleWords_TakesTheMovementName(string title, string expected)
        => Assert.Equal(expected, string.Join(' ', YouTubeMatcher.GetDistinctiveTitleWords(title)));

    [Fact]
    public void PickBest_RejectsCandidatesTooFarOffInLength()
    {
        var track = new PlaylistTrack { Title = "Goldberg Variations, BWV 988: Aria", Artist = "Glenn Gould", DurationSeconds = 185 };

        Assert.Null(YouTubeMatcher.PickBest(track, [new MatchCandidate("a", "Goldberg Variations, BWV 988: Aria (10 Hours)", "Glenn Gould", 36000)]));
    }

    [Fact]
    public void PickBest_RejectsCandidatesWithNeitherArtistChannelNorTitle()
    {
        var track = new PlaylistTrack { Title = "Für Elise", Artist = "Lang Lang", DurationSeconds = 204 };

        Assert.Null(YouTubeMatcher.PickBest(track, [new MatchCandidate("a", "Something Else", "Someone", 204)]));
    }

    [Fact]
    public void PickBest_AllowsAlternateVersionWordsTheSourceTitleHas()
    {
        var track = new PlaylistTrack { Title = "Clair de lune (Live)", Artist = "Lang Lang", DurationSeconds = 389 };
        MatchCandidate[] candidates =
        [
            new("studio", "Clair de lune", "Lang Lang", 389),
            new("live", "Clair de lune (Live)", "Lang Lang", 389)
        ];

        Assert.Equal("live", YouTubeMatcher.PickBest(track, candidates)!.Candidate.Id);
    }

    [Fact]
    public void PickBest_PrefersTopicChannelAndHandlesMissingDuration()
    {
        var track = new PlaylistTrack { Title = "Träumerei", Artist = "Martha Argerich" };
        MatchCandidate[] candidates =
        [
            new("fan", "Martha Argerich - Träumerei", "Some Fan", null),
            new("topic", "Träumerei", "Martha Argerich - Topic", null)
        ];

        Assert.Equal("topic", YouTubeMatcher.PickBest(track, candidates)!.Candidate.Id);
    }

    [Fact]
    public void DescribeConcerns_NotesLengthDifferenceAndThirdPartyUploads()
    {
        var close = new MatchResult(new MatchCandidate("a", "t", "c", 199), 1, ChannelMatchesArtist: true);
        Assert.Null(YouTubeMatcher.DescribeConcerns(close));

        var off = new MatchResult(new MatchCandidate("a", "t", "c", 178), 6, ChannelMatchesArtist: false);
        var note = YouTubeMatcher.DescribeConcerns(off);
        Assert.Contains("Length differs by 6s", note, StringComparison.Ordinal);
        Assert.Contains("Not uploaded by the artist", note, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Lang Lang", "Für Elise", "Lang Lang - Für Elise")]
    [InlineData(null, "Für Elise", "Für Elise")]
    [InlineData("Saint-Saëns' Players", "\"The Swan\"", "Saint-Saëns' Players - The Swan")]
    public void BuildSearchQuery_CombinesArtistAndTitle(string? artist, string title, string expected)
        => Assert.Equal(expected, YouTubeMatcher.BuildSearchQuery(new PlaylistTrack { Title = title, Artist = artist }));

    // MARK: Tagging

    [Fact]
    public void ApplySourceTrackTags_UsesAlbumArtistForCompilations()
    {
        var tag = new TagLib.Id3v2.Tag { AlbumArtists = ["London Symphony Orchestra"] };

        DownloadService.ApplySourceTrackTags(tag, new PlaylistTrack { Title = "t", Artist = "London Symphony Orchestra", AlbumArtist = "Various Artists" });

        Assert.Equal(["Various Artists"], tag.AlbumArtists);
    }

    [Fact]
    public void ApplySourceTrackTags_WritesExtraFieldsToFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"yta-tags-{Guid.NewGuid():N}.opus");
        File.Copy(Path.Combine("TestData", "silence.opus"), path);
        try
        {
            var track = ParseFixture().Tracks.Single(t => t.Title.StartsWith("Liebestraum", StringComparison.Ordinal));
            using (var file = TagLib.File.Create(path))
            {
                DownloadService.ApplySourceTrackTags(file.Tag, track);
                file.Save();
            }

            using var reread = TagLib.File.Create(path);
            Assert.Equal(10u, reread.Tag.Track);
            Assert.Equal(12u, reread.Tag.TrackCount);
            Assert.Equal(1u, reread.Tag.Disc);
            Assert.Equal(["Classical"], reread.Tag.Genres);
            Assert.Equal(["Franz Liszt"], reread.Tag.Composers);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
