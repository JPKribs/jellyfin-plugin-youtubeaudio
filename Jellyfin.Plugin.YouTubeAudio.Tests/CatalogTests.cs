using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.YouTubeAudio.Models;
using Jellyfin.Plugin.YouTubeAudio.Services;
using Jellyfin.Plugin.YouTubeAudio.Services.Catalog;
using Jellyfin.Plugin.YouTubeAudio.Services.Playlists;
using Xunit;

namespace Jellyfin.Plugin.YouTubeAudio.Tests;

/// <summary>
/// Tests for the Apple catalog lookup (against captured iTunes Search responses), artist
/// credits and the library's artist spellings.
/// </summary>
public class CatalogTests
{
    private static string Load(string name) => File.ReadAllText(Path.Combine("TestData", $"AppleCatalog-{name}.json"));

    // MARK: Parsing

    [Fact]
    public void ParseSongs_ReadsTheAlbumDetails()
    {
        var song = CatalogMatcher.ParseSongs(Load("elise")).First(s => s.ArtistName == "Lang Lang");

        Assert.Equal("Piano Book (Deluxe Edition)", song.AlbumName);
        Assert.Equal("Lang Lang", song.AlbumArtist);
        Assert.Equal(2019, song.Year);
        Assert.Equal(2, song.TrackNumber);
        Assert.Equal(41, song.TrackCount);
        Assert.Equal(1, song.DiscNumber);
        Assert.Equal("Classical", song.Genre);
        Assert.InRange(song.DurationSeconds ?? 0, 200, 210);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"results\": 5}")]
    public void ParseSongs_IgnoresUnreadableResponses(string json)
        => Assert.Empty(CatalogMatcher.ParseSongs(json));

    // MARK: Matching

    [Fact]
    public void PickBest_MatchesAShortTitleToApplesFullClassicalTitle()
    {
        // Apple: Bagatelle No. 25 in A Minor, WoO 59 "Für Elise" on Piano Book (Deluxe Edition).
        var song = CatalogMatcher.PickBest("Lang Lang", "Für Elise", 205, CatalogMatcher.ParseSongs(Load("elise")));

        Assert.NotNull(song);
        Assert.Equal("Lang Lang", song.ArtistName);
        Assert.Equal("Piano Book", CatalogMatcher.CleanAlbumName(song.AlbumName));
    }

    [Fact]
    public void PickBest_MatchesTheSamePieceWrittenAnotherWay()
    {
        // Lang Lang's channel: "Beethoven: Für Elise, Bagatelle in A Minor, WoO 59" (204 s).
        // Same-titled versions on Apple compilations are 5+ seconds longer and are rejected.
        var song = CatalogMatcher.PickBest("Lang Lang", "Beethoven: Für Elise, Bagatelle in A Minor, WoO 59", 204, CatalogMatcher.ParseSongs(Load("bagatelle")));

        Assert.Equal("Piano Book (Deluxe Edition)", song?.AlbumName);
        Assert.Equal(2, song?.TrackNumber);
    }

    [Fact]
    public void MostlyOverlaps_IgnoresShortTitles()
        => Assert.False(CatalogMatcher.MostlyOverlaps(["clair", "de", "lune"], ["clair", "de", "lune", "arr"]));

    [Fact]
    public void PickBest_RequiresCloseLengthsForAPartialTitle()
        => Assert.Null(CatalogMatcher.PickBest("Lang Lang", "Für Elise", 240, CatalogMatcher.ParseSongs(Load("elise"))));

    [Fact]
    public void PickBest_NeverPicksAnotherPerformer()
        // The search returns other pianists' Für Elise and Clair de lune, but no Lang Lang.
        => Assert.Null(CatalogMatcher.PickBest("Lang Lang", "Clair de lune", 300, CatalogMatcher.ParseSongs(Load("clair"))));

    [Fact]
    public void PickBest_MatchesTheRightPerformerOfTheSameTitle()
    {
        var song = CatalogMatcher.PickBest("Van Cliburn", "Für Elise", 187, CatalogMatcher.ParseSongs(Load("elise")));

        Assert.Equal("The World's Favorite Piano Music", song?.AlbumName);
    }

    [Fact]
    public void PickBest_PrefersTheAlbumOverACompilation()
    {
        // YouTube Music credits "Yo-Yo Ma, Kathryn Stott"; Apple credits "Yo-Yo Ma & Kathryn Stott".
        var song = CatalogMatcher.PickBest("Yo-Yo Ma, Kathryn Stott", "The Swan", 174, CatalogMatcher.ParseSongs(Load("swan")));

        Assert.Equal("Songs from the Arc of Life", song?.AlbumName);
    }

    [Fact]
    public void ListsArtist_TellsAnActFromAJointCredit()
    {
        Assert.True(CatalogMatcher.ListsArtist("Katia & Marielle Labèque", CatalogMatcher.ParseArtistNames(Load("labeque"))));
        Assert.False(CatalogMatcher.ListsArtist("Yo-Yo Ma & Kathryn Stott", CatalogMatcher.ParseArtistNames(Load("mastott"))));
    }

    [Fact]
    public void GetArtworkUrl_AsksForTheRequestedSize()
        => Assert.Equal(
            "https://is1-ssl.mzstatic.com/image/thumb/Music/v4/a/cover.jpg/1200x1200bb.jpg",
            CatalogMatcher.GetArtworkUrl("https://is1-ssl.mzstatic.com/image/thumb/Music/v4/a/cover.jpg/100x100bb.jpg", 1200));

    // MARK: Artist credits

    [Theory]
    [InlineData("Yo-Yo Ma & Kathryn Stott", "Yo-Yo Ma|Kathryn Stott")]
    [InlineData("Martha Argerich, Mischa Maisky & Gidon Kremer", "Martha Argerich|Mischa Maisky|Gidon Kremer")]
    [InlineData("Yo-Yo Ma;Kathryn Stott", "Yo-Yo Ma|Kathryn Stott")]
    [InlineData("Academy of St Martin in the Fields and Neville Marriner", "Academy of St Martin in the Fields and Neville Marriner")]
    [InlineData("Lang Lang", "Lang Lang")]
    public void SplitCredit_SplitsJointCredits(string credit, string expected)
        => Assert.Equal(expected, string.Join('|', TrackMetadataCleaner.SplitCredit(credit)));

    [Fact]
    public void SplitCredit_KeepsKnownActsWhole()
    {
        static bool IsAct(string name) => name == "Katia & Marielle Labèque";

        Assert.Equal(["Katia & Marielle Labèque"], TrackMetadataCleaner.SplitCredit("Katia & Marielle Labèque", IsAct));
        Assert.Equal(["Katia & Marielle Labèque", "Martha Argerich"], TrackMetadataCleaner.SplitCredit("Katia & Marielle Labèque, Martha Argerich", IsAct));
    }

    [Fact]
    public void SongKey_MatchesAJointCreditToItsSplitLibraryCopy()
    {
        // After a cleanup the library holds the artists separately; re-importing must not download again.
        Assert.Equal(TrackMetadataCleaner.SongKey("Yo-Yo Ma", "The Swan"), TrackMetadataCleaner.SongKey("Yo-Yo Ma & Kathryn Stott", "The Swan"));
        Assert.Equal(TrackMetadataCleaner.SongKey("Yo-Yo Ma", "The Swan"), TrackMetadataCleaner.SongKey("Yo-Yo Ma, Kathryn Stott", "The Swan"));
    }

    [Fact]
    public void BuildArtistSpellings_UsesTheMostCommonSpelling()
    {
        var spellings = LibraryService.BuildArtistSpellings(["Lang Lang", "LANG LANG", "Lang Lang", "Hilary Hahn"]);

        Assert.Equal("Lang Lang", spellings[TrackMetadataCleaner.Normalize("LANG LANG")]);
        Assert.Equal("Hilary Hahn", spellings[TrackMetadataCleaner.Normalize("hilary hahn")]);
    }

    // MARK: Release year

    [Fact]
    public void AppleLibrary_PrefersTheAlbumReleaseDateOverTheSongYear()
    {
        var plist = """
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0"><dict>
            <key>Tracks</key><dict>
              <key>1</key><dict><key>Track ID</key><integer>1</integer><key>Name</key><string>Clair de lune</string>
                <key>Artist</key><string>Lang Lang</string><key>Album</key><string>The Best of Lang Lang</string>
                <key>Year</key><integer>1905</integer><key>Release Date</key><date>2005-03-01T12:00:00Z</date></dict>
              <key>2</key><dict><key>Track ID</key><integer>2</integer><key>Name</key><string>Für Elise</string>
                <key>Artist</key><string>Lang Lang</string><key>Year</key><integer>2019</integer></dict>
            </dict>
            <key>Playlists</key><array><dict><key>Name</key><string>Evening Piano</string><key>Playlist ID</key><integer>9</integer>
              <key>Playlist Items</key><array><dict><key>Track ID</key><integer>1</integer></dict><dict><key>Track ID</key><integer>2</integer></dict></array></dict></array>
            </dict></plist>
            """;

        var tracks = Assert.Single(new AppleMusicLibrarySource().Parse("Library.xml", plist)).Tracks;

        Assert.Equal(2005, tracks[0].Year);
        Assert.Equal(2019, tracks[1].Year);
    }
}
