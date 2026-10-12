using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.YouTubeAudio.Models;
using Jellyfin.Plugin.YouTubeAudio.Services;
using Jellyfin.Plugin.YouTubeAudio.Services.Playlists;
using Xunit;

namespace Jellyfin.Plugin.YouTubeAudio.Tests;

/// <summary>
/// Tests for metadata cleanup (editions, feat.) and the duplicate rules applied to imports,
/// using classical releases named the way Apple Music names them.
/// </summary>
public class ImportCleanupTests
{
    // MARK: Album cleanup

    [Theory]
    [InlineData("Goldberg Variations (Bonus Track Version)", "Goldberg Variations")]
    [InlineData("The Four Seasons (Deluxe Edition)", "The Four Seasons")]
    [InlineData("Greatest Classical Hits (Remastered)", "Greatest Classical Hits")]
    [InlineData("Beethoven: Symphony No. 9 (Super Deluxe Edition)", "Beethoven: Symphony No. 9")]
    [InlineData("Mozart: Requiem (Expanded Version)", "Mozart: Requiem")]
    [InlineData("Chopin: Nocturnes (20th Anniversary Edition)", "Chopin: Nocturnes")]
    [InlineData("Holst: The Planets (Special Edition)", "Holst: The Planets")]
    [InlineData("Vivaldi: Gloria. Magnificat. (Deluxe Version)", "Vivaldi: Gloria. Magnificat.")]
    [InlineData("Brahms: Symphony No. 4 - Super Deluxe", "Brahms: Symphony No. 4")]
    [InlineData("Dvořák: Symphony No. 9 [2019 Remaster]", "Dvořák: Symphony No. 9")]
    [InlineData("Clair de lune - Single", "Clair de lune")]
    [InlineData("Debussy: Préludes - EP", "Debussy: Préludes")]
    [InlineData("Ave Maria (feat. Andrea Bocelli) - Single", "Ave Maria")]
    [InlineData("Bach: The Goldberg Variations (1955 Recording)", "Bach: The Goldberg Variations (1955 Recording)")]
    [InlineData("Cello Suites (Yo-Yo Ma's Version)", "Cello Suites (Yo-Yo Ma's Version)")]
    [InlineData("Rachmaninoff: Piano Concerto No. 2 (Live)", "Rachmaninoff: Piano Concerto No. 2 (Live)")]
    [InlineData("Gymnopédies (Orchestral Version)", "Gymnopédies (Orchestral Version)")]
    [InlineData("Liszt: Années de pèlerinage, Book 1: Suisse & Italie (Deluxe Version)", "Liszt: Années de pèlerinage, Book 1: Suisse & Italie")]
    public void CleanAlbum_StripsEditionLabelsButKeepsDistinctRecordings(string album, string expected)
        => Assert.Equal(expected, TrackMetadataCleaner.CleanAlbum(album));

    // MARK: feat.

    [Theory]
    [InlineData("Ave Maria (feat. Andrea Bocelli)", "Ave Maria", "Andrea Bocelli")]
    [InlineData("Panis Angelicus (feat. Luciano Pavarotti) [Live]", "Panis Angelicus [Live]", "Luciano Pavarotti")]
    [InlineData("Air on the G String (feat. Anne-Sophie Mutter) [Arr. for Violin]", "Air on the G String [Arr. for Violin]", "Anne-Sophie Mutter")]
    [InlineData("Pie Jesu (with Sarah Brightman)", "Pie Jesu", "Sarah Brightman")]
    [InlineData("Sonata [ft. A, B & C]", "Sonata", "A|B|C")]
    [InlineData("Nocturne feat. Someone", "Nocturne", "Someone")]
    [InlineData("Sonata for Violin With Piano", "Sonata for Violin With Piano", "")]
    public void ExtractFeatured_MovesFeaturedArtistsOutOfTitle(string title, string expectedTitle, string expectedFeatured)
    {
        var (cleaned, featured) = TrackMetadataCleaner.ExtractFeatured(title);

        Assert.Equal(expectedTitle, cleaned);
        Assert.Equal(expectedFeatured.Length == 0 ? [] : expectedFeatured.Split('|'), featured);
    }

    [Fact]
    public void Clean_SplitsArtistFeatAndKeepsJointBilling()
    {
        var featuring = TrackMetadataCleaner.Clean(new PlaylistTrack { Title = "Hungarian Rhapsody No. 2 (feat. Yuja Wang)", Artist = "Lang Lang feat. Yuja Wang" });
        Assert.Equal("Lang Lang", featuring.Artist);
        Assert.Equal(["Yuja Wang"], featuring.FeaturedArtists);
        Assert.Equal("Lang Lang - Hungarian Rhapsody No. 2", YouTubeMatcher.BuildSearchQuery(featuring));

        var joint = TrackMetadataCleaner.Clean(new PlaylistTrack { Title = "Sonata for Two Pianos in D Major, K. 448", Artist = "Martha Argerich & Daniel Barenboim" });
        Assert.Equal("Martha Argerich & Daniel Barenboim", joint.Artist);
        Assert.Null(joint.FeaturedArtists);
    }

    [Theory]
    [InlineData("Canon in D (Remastered 2017)", "Canon in D")]
    [InlineData("Clair de lune - 2004 Remaster", "Clair de lune")]
    [InlineData("Gymnopédie No. 1 (Bonus Track)", "Gymnopédie No. 1")]
    [InlineData("Boléro (Live)", "Boléro (Live)")]
    [InlineData("Asturias (Arr. for Guitar)", "Asturias (Arr. for Guitar)")]
    public void Clean_DropsRemasterLabelsFromTitlesOnly(string title, string expected)
        => Assert.Equal(expected, TrackMetadataCleaner.Clean(new PlaylistTrack { Title = title }).Title);

    // MARK: Genres

    [Theory]
    [InlineData("classical", "Classical")]
    [InlineData("CLASSICAL", "Classical")]
    [InlineData("classical;baroque", "Classical|Baroque")]
    [InlineData("Classical, Romantic", "Classical|Romantic")]
    [InlineData("Pop/Rock", "Pop|Rock")]
    [InlineData("hip-hop/rap", "Hip-Hop|Rap")]
    [InlineData("contemporary classical", "Contemporary Classical")]
    [InlineData("Singer/Songwriter", "Singer/Songwriter")]
    [InlineData("drum & bass", "Drum & Bass")]
    [InlineData("rhythm and blues", "R&B")]
    [InlineData("r&b; edm", "R&B|Electronic|Dance")]
    [InlineData("k-pop", "K-Pop")]
    [InlineData("Classical; classical ;BAROQUE", "Classical|Baroque")]
    [InlineData(" ; ", "")]
    public void CleanGenres_SplitsAndCapitalizes(string genre, string expected)
        => Assert.Equal(expected.Length == 0 ? [] : expected.Split('|'), TrackMetadataCleaner.CleanGenres(genre));

    [Theory]
    [InlineData("alt-pop", "Alternative|Pop")]
    [InlineData("Alternative Rock", "Alternative|Rock")]
    [InlineData("indie folk", "Indie|Folk")]
    [InlineData("Pop Punk; pop", "Pop|Punk")]
    [InlineData("electronica", "Electronic")]
    [InlineData("EDM", "Electronic|Dance")]
    [InlineData("hip hop/rap", "Hip-Hop|Rap")]
    [InlineData("rnb", "R&B")]
    [InlineData("dnb", "Drum & Bass")]
    [InlineData("OST", "Soundtrack")]
    [InlineData("singer-songwriter", "Singer/Songwriter")]
    [InlineData("classical; orchestral", "Classical|Orchestral")]
    [InlineData("Contemporary Classical", "Contemporary Classical")]
    [InlineData("synthwave", "Synthwave")]
    [InlineData("Classic Rock", "Classic Rock")]
    [InlineData("Baroque", "Baroque")]
    [InlineData("CCM", "CCM")]
    [InlineData("Death Metal/Black Metal", "Death Metal|Black Metal")]
    public void CleanGenres_BreaksCompoundsIntoStandardGenres(string genre, string expected)
        => Assert.Equal(expected.Split('|'), TrackMetadataCleaner.CleanGenres(genre));

    [Fact]
    public void Clean_NormalizesGenreForImportedTracks()
        => Assert.Equal("Classical; Baroque", TrackMetadataCleaner.Clean(new PlaylistTrack { Title = "Canon in D", Genre = "classical/baroque" }).Genre);

    // MARK: Release type

    [Theory]
    [InlineData("Clair de lune - Single", null, false, 1, ReleaseType.Single)]
    [InlineData("Debussy: Préludes - EP", null, false, 4, ReleaseType.EP)]
    [InlineData("The Beethoven Collection", "Ludwig van Beethoven", false, 150, ReleaseType.Compilation)]
    [InlineData("Classical Favourites", "Various Artists", false, 20, ReleaseType.Compilation)]
    [InlineData("Romantic Piano", "Lang Lang", true, 15, ReleaseType.Compilation)]
    [InlineData("Mozart's Greatest Hits", "Wolfgang Amadeus Mozart", false, 10, ReleaseType.Compilation)]
    [InlineData("Some Release", "Artist", false, 1, ReleaseType.Single)]
    [InlineData("Bach: The Goldberg Variations", "Glenn Gould", false, 32, ReleaseType.Album)]
    [InlineData("The Best Is Yet to Come", "Hilary Hahn", false, 12, ReleaseType.Album)]
    public void DetectReleaseType_ClassifiesFromAppleAlbumNames(string album, string? albumArtist, bool compilation, int trackCount, ReleaseType expected)
        => Assert.Equal(expected, TrackMetadataCleaner.DetectReleaseType(album, albumArtist, compilation, trackCount));

    // MARK: Duplicate rules

    private static PlaylistTrack ClairDeLune(string album, int trackCount, int year)
        => new() { Title = "Clair de lune", Artist = "Lang Lang", AlbumArtist = "Lang Lang", Album = album, TrackCount = trackCount, Year = year, DurationSeconds = 408 };

    private static PlaylistTrack PianoBook(string title)
        => new() { Title = title, Artist = "Lang Lang", AlbumArtist = "Lang Lang", Album = "Piano Book", TrackCount = 25, Year = 2019 };

    [Fact]
    public void Plan_KeepsAlbumVersionOverSingleWithinImport()
    {
        var plan = ImportPlanner.Plan(
            [
                PianoBook("Für Elise"),
                ClairDeLune("Clair de lune - Single", 1, 2018),
                ClairDeLune("Piano Book", 25, 2019)
            ],
            [],
            []);

        Assert.Equal(["Für Elise", "Clair de lune"], plan.Tracks.Select(t => t.Title));
        Assert.Equal("Piano Book", plan.Tracks[1].Album);
        Assert.Equal(ReleaseType.Album, plan.Tracks[1].ReleaseType);
        Assert.Equal(1, plan.Duplicates);
    }

    [Fact]
    public void Plan_UsesSingleWhenThereIsNoAlbumVersion()
    {
        var plan = ImportPlanner.Plan([new PlaylistTrack { Title = "Gnossienne No. 1", Artist = "Alice Sara Ott", Album = "Gnossienne No. 1 - Single", TrackCount = 1 }], [], []);

        var track = Assert.Single(plan.Tracks);
        Assert.Equal("Gnossienne No. 1", track.Album);
        Assert.Equal(ReleaseType.Single, track.ReleaseType);
    }

    [Fact]
    public void Plan_SkipsSongTheLibraryAlreadyHas()
    {
        var library = new List<LibraryTrack>
        {
            new() { Title = "Clair de lune", Artist = "Lang Lang", AlbumArtist = "Lang Lang", Album = "Piano Book", Path = "/music/Lang Lang/Piano Book (2019)/Clair de lune.opus" },
            new() { Title = "Für Elise", Artist = "Lang Lang", AlbumArtist = "Lang Lang", Album = "Piano Book" },
            // Older YouTube imports may still carry "feat." in the title.
            new() { Title = "Hungarian Rhapsody No. 2 (feat. Yuja Wang)", Artist = "Lang Lang", Album = "Hungarian Rhapsody No. 2" }
        };

        var plan = ImportPlanner.Plan(
            [
                ClairDeLune("Clair de lune - Single", 1, 2018),
                ClairDeLune("Piano Book", 25, 2019),
                new PlaylistTrack { Title = "HUNGARIAN RHAPSODY NO. 2", Artist = "Lang Lang feat. Yuja Wang", Album = "Hungarian Rhapsody No. 2 - Single", TrackCount = 1 }
            ],
            library,
            []);

        Assert.Empty(plan.Tracks);
        Assert.Equal(2, plan.AlreadyInLibrary);
    }

    [Fact]
    public void Plan_UpgradesLibrarySingleToAlbumVersion()
    {
        const string singlePath = "/music/Lang Lang/Clair de lune (2018)/Clair de lune.opus";
        var library = new List<LibraryTrack>
        {
            new() { Title = "Clair de lune", Artist = "Lang Lang", AlbumArtist = "Lang Lang", Album = "Clair de lune", Year = 2018, Path = singlePath }
        };

        var plan = ImportPlanner.Plan([ClairDeLune("Piano Book", 25, 2019)], library, []);

        var track = Assert.Single(plan.Tracks);
        Assert.Equal([singlePath], track.ReplacesFiles);
        Assert.Equal(1, plan.LibraryUpgrades);
        Assert.Equal(0, plan.AlreadyInLibrary);
    }

    [Fact]
    public void Plan_NeverUpgradesATitleTrackOnTheSameAlbum()
    {
        // Only "Rhapsody in Blue" from Rhapsody in Blue is in the library, so it looks like a
        // single, but it is the same release the import brings: replacing it would delete and
        // re-download it.
        var library = new List<LibraryTrack>
        {
            new() { Title = "Rhapsody in Blue", Artist = "Leonard Bernstein", AlbumArtist = "Leonard Bernstein", Album = "Rhapsody in Blue (Remastered)", Path = "/music/r.opus" }
        };

        var plan = ImportPlanner.Plan([new PlaylistTrack { Title = "Rhapsody in Blue", Artist = "Leonard Bernstein", AlbumArtist = "Leonard Bernstein", Album = "Rhapsody in Blue", TrackCount = 8 }], library, []);

        Assert.Empty(plan.Tracks);
        Assert.Equal(1, plan.AlreadyInLibrary);
        Assert.Equal(0, plan.LibraryUpgrades);
    }

    [Fact]
    public void Plan_DoesNotUpgradeLibrarySingleToCompilation()
    {
        var library = new List<LibraryTrack>
        {
            new() { Title = "Clair de lune", Artist = "Lang Lang", AlbumArtist = "Lang Lang", Album = "Clair de lune", Path = "/music/c.opus" }
        };

        var plan = ImportPlanner.Plan([ClairDeLune("The Best of Lang Lang", 18, 2005)], library, []);

        Assert.Empty(plan.Tracks);
        Assert.Equal(1, plan.AlreadyInLibrary);
    }

    [Fact]
    public void Plan_TreatsTitleTrackOnAFullLibraryAlbumAsAlbumVersion()
    {
        // "Boléro" on the album "Boléro": an album, not a single, because the library holds
        // other tracks from it.
        var library = new List<LibraryTrack>
        {
            new() { Title = "Boléro", Artist = "Berliner Philharmoniker", Album = "Boléro (Bonus Track Version)", Path = "/music/b.opus" },
            new() { Title = "La valse", Artist = "Berliner Philharmoniker", Album = "Boléro (Bonus Track Version)" },
            new() { Title = "Pavane pour une infante défunte", Artist = "Berliner Philharmoniker", Album = "Boléro (Bonus Track Version)" }
        };

        var plan = ImportPlanner.Plan([new PlaylistTrack { Title = "Boléro", Artist = "Berliner Philharmoniker", Album = "Boléro", TrackCount = 12 }], library, []);

        Assert.Empty(plan.Tracks);
        Assert.Equal(1, plan.AlreadyInLibrary);
    }

    [Fact]
    public void Plan_SkipsQueuedSongButSwapsAQueuedSingleForTheAlbum()
    {
        var queued = new List<QueuedTrack>
        {
            new() { Id = "downloaded", Title = "Für Elise", Artist = "Lang Lang", Status = QueueStatus.Downloaded, ReleaseType = ReleaseType.Single },
            new() { Id = "single", Title = "Clair de lune", Artist = "Lang Lang", Status = QueueStatus.Queued, ReleaseType = ReleaseType.Single },
            new() { Id = "failed", Title = "Gymnopédie No. 1", Artist = "Lang Lang", Status = QueueStatus.Error }
        };

        var plan = ImportPlanner.Plan(
            [
                PianoBook("Für Elise"),
                ClairDeLune("Piano Book", 25, 2019),
                PianoBook("Gymnopédie No. 1")
            ],
            [],
            queued);

        // The failed Gymnopédie download is replaced by a fresh attempt rather than duplicated.
        Assert.Equal(["Clair de lune", "Gymnopédie No. 1"], plan.Tracks.Select(t => t.Title));
        Assert.Equal(["single", "failed"], plan.QueueIdsToRemove);
        Assert.Equal(1, plan.AlreadyQueued);
    }

    [Fact]
    public void Plan_IgnoresLegacyImportedQueueRows()
    {
        // Older versions kept a row per imported song. If the song has since left the library,
        // that row must not stop it being queued again.
        var queued = new List<QueuedTrack> { new() { Id = "old", Title = "Für Elise", Artist = "Lang Lang", Status = QueueStatus.Imported } };

        var plan = ImportPlanner.Plan([PianoBook("Für Elise")], [], queued);

        Assert.Single(plan.Tracks);
        Assert.Equal(0, plan.AlreadyQueued);
        Assert.Empty(plan.QueueIdsToRemove);
    }

    [Fact]
    public void Plan_MergesAlbumEditionsIntoOneAlbumAndYear()
    {
        var plan = ImportPlanner.Plan(
            [
                new PlaylistTrack { Title = "Symphony No. 9 in D Minor, Op. 125: IV. Presto", Artist = "Berliner Philharmoniker", AlbumArtist = "Berliner Philharmoniker", Album = "Beethoven: Symphony No. 9 (Deluxe Edition)", Year = 2008, TrackCount = 6 },
                new PlaylistTrack { Title = "Symphony No. 9 in D Minor, Op. 125: II. Molto vivace", Artist = "Berliner Philharmoniker", AlbumArtist = "Berliner Philharmoniker", Album = "Beethoven: Symphony No. 9", Year = 2007, TrackCount = 4 }
            ],
            [],
            []);

        Assert.All(plan.Tracks, t => Assert.Equal("Beethoven: Symphony No. 9", t.Album));
        Assert.All(plan.Tracks, t => Assert.Equal(2007, t.Year));
    }

    [Fact]
    public void Plan_FilesIntoTheLibrarysExistingAlbumFolder()
    {
        var library = new List<LibraryTrack>
        {
            new() { Title = "Nocturne No. 1 in B-Flat Minor", Artist = "Maria João Pires", AlbumArtist = "Maria João Pires", Album = "Chopin: Nocturnes (Deluxe Edition)", Year = 2004 },
            new() { Title = "Nocturne No. 2 in E-Flat Major", Artist = "Maria João Pires", AlbumArtist = "Maria João Pires", Album = "Chopin: Nocturnes (Deluxe Edition)", Year = 2004 }
        };

        var plan = ImportPlanner.Plan(
            [new PlaylistTrack { Title = "Nocturne No. 20 in C-Sharp Minor", Artist = "Maria João Pires", AlbumArtist = "Maria João Pires", Album = "Chopin: Nocturnes", Year = 1996, TrackCount = 21 }],
            library,
            []);

        var track = Assert.Single(plan.Tracks);
        Assert.Equal("Chopin: Nocturnes (Deluxe Edition)", track.Album);
        Assert.Equal(2004, track.Year);
    }

    // MARK: Classical movements in the library

    private const string MovementTitle = "Suite bergamasque, L. 75: III. Clair de lune";

    [Fact]
    public void Plan_UsesALibraryCopyTaggedWithOnlyTheMovement()
    {
        var library = new List<LibraryTrack> { new() { Title = "Clair de lune", Artist = "Lang Lang", Album = "Piano Book", DurationSeconds = 300 } };

        var plan = ImportPlanner.Plan([new PlaylistTrack { Title = MovementTitle, Artist = "Lang Lang", Album = "Debussy", DurationSeconds = 302 }], library, []);

        Assert.Empty(plan.Tracks);
        Assert.Equal(1, plan.AlreadyInLibrary);
        Assert.Equal("langlang|clairdelune", plan.LibraryAliases[TrackMetadataCleaner.SongKey("Lang Lang", MovementTitle)]);
    }

    [Fact]
    public void Plan_UsesALibraryCopyTaggedWithTheWholeWork()
    {
        var library = new List<LibraryTrack> { new() { Title = MovementTitle, Artist = "Lang Lang", Album = "Debussy" } };

        var plan = ImportPlanner.Plan([new PlaylistTrack { Title = "Clair de lune", Artist = "Lang Lang", Album = "Piano Book" }], library, []);

        Assert.Empty(plan.Tracks);
        Assert.Equal(TrackMetadataCleaner.SongKey("Lang Lang", MovementTitle), plan.LibraryAliases["langlang|clairdelune"]);
    }

    [Theory]
    [InlineData("Lang Lang", "Clair de lune", 300, "Seong-Jin Cho")] // another performer
    [InlineData("Lang Lang", "Clair de lune", 420, "Lang Lang")] // a different recording length
    [InlineData("Yo-Yo Ma", "Cello Suite No. 2 in D Minor, BWV 1008: I. Prélude", 230, "Yo-Yo Ma")] // a tempo/form name alone
    public void Plan_DownloadsWhenTheMovementMatchIsNotCertain(string libraryArtist, string libraryTitle, double libraryLength, string trackArtist)
    {
        var title = trackArtist == "Yo-Yo Ma" ? "Cello Suite No. 1 in G Major, BWV 1007: I. Prélude" : MovementTitle;
        var library = new List<LibraryTrack> { new() { Title = libraryTitle, Artist = libraryArtist, DurationSeconds = libraryLength } };

        var plan = ImportPlanner.Plan([new PlaylistTrack { Title = title, Artist = trackArtist, DurationSeconds = 300 }], library, []);

        Assert.Single(plan.Tracks);
        Assert.Empty(plan.LibraryAliases);
    }

    [Fact]
    public void Plan_DownloadsWhenTheMovementNamesSeveralLibrarySongs()
    {
        // Two different works by the artist share the movement name "Romance": it identifies neither.
        var library = new List<LibraryTrack>
        {
            new() { Title = "Violin Sonata No. 5: II. Romanze", Artist = "Hilary Hahn" },
            new() { Title = "Romance in F Major, Op. 50: Romanze", Artist = "Hilary Hahn" }
        };

        var plan = ImportPlanner.Plan([new PlaylistTrack { Title = "Romanze", Artist = "Hilary Hahn" }], library, []);

        Assert.Single(plan.Tracks);
    }

    [Fact]
    public void BuildPlaylistEntries_UseTheLibraryKeyForAMovementMatch()
    {
        var playlist = new ParsedPlaylist { Name = "Evening Piano" };
        playlist.Tracks.Add(new PlaylistTrack { Title = MovementTitle, Artist = "Lang Lang" });
        var aliases = new Dictionary<string, string> { [TrackMetadataCleaner.SongKey("Lang Lang", MovementTitle)] = "langlang|clairdelune" };

        var entry = Assert.Single(PlaylistImportService.BuildPlaylistEntries(playlist, aliases));

        Assert.Equal("langlang|clairdelune", entry.SongKey);
        Assert.Equal(MovementTitle, entry.Title);
    }

    [Theory]
    [InlineData(MovementTitle, "langlang|clairdelune")]
    [InlineData("Cello Suite No. 1 in G Major, BWV 1007: I. Prélude", null)]
    [InlineData("Piano Sonata No. 14: III. Presto agitato", "langlang|prestoagitato")]
    [InlineData("Clair de lune", null)]
    public void MovementKey_KeepsOnlyDistinctiveMovements(string title, string? expected)
        => Assert.Equal(expected, TrackMetadataCleaner.MovementKey("Lang Lang", title));

    // MARK: Tagging

    [Fact]
    public void ApplySourceTrackTags_WritesPrimaryThenFeaturedPerformers()
    {
        var tag = new TagLib.Id3v2.Tag();
        var track = TrackMetadataCleaner.Clean(new PlaylistTrack { Title = "Carmen-Fantasie (feat. Anne-Sophie Mutter)", Artist = "Wiener Philharmoniker & Herbert von Karajan" });

        DownloadService.ApplySourceTrackTags(tag, track);

        Assert.Equal(["Wiener Philharmoniker & Herbert von Karajan", "Anne-Sophie Mutter"], tag.Performers);
    }
}
