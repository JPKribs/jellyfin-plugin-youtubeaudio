using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.YouTubeAudio.Services.Playlists;
using Xunit;

namespace Jellyfin.Plugin.YouTubeAudio.Tests;

/// <summary>
/// Tests for Spotify exports made with Exportify: one CSV per playlist, or a ZIP of them.
/// The data is synthetic, in Exportify's exact column layout.
/// </summary>
public class SpotifyImportTests
{
    private const string Header = "Track URI,Track Name,Artist URI(s),Artist Name(s),Album URI,Album Name,Album Artist URI(s),Album Artist Name(s),Album Release Date,Album Image URL,Disc Number,Track Number,Track Duration (ms),Track Preview URL,Explicit,Popularity,ISRC,Added By,Added At";

    private static readonly string _evening = string.Join('\n',
        Header,
        "spotify:track:1,Clair de lune,spotify:artist:a1,Lang Lang,spotify:album:b1,Piano Book (Deluxe Edition),spotify:artist:a1,Lang Lang,2019-01-17,https://i.scdn.co/image/aaa,1,3,408000,,false,50,DEUM71900001,spotify:user:example,2024-01-01T00:00:00Z",
        "spotify:track:2,The Swan,\"spotify:artist:a2,spotify:artist:a3\",\"Yo-Yo Ma, Kathryn Stott\",spotify:album:b2,Songs from the Arc of Life,\"spotify:artist:a2,spotify:artist:a3\",\"Yo-Yo Ma, Kathryn Stott\",2015,https://i.scdn.co/image/bbb,1,14,174000,,false,40,USSM11500001,spotify:user:example,2024-01-02T00:00:00Z",
        "spotify:track:3,\"Partita No. 2, BWV 1004: V. Chaconne\",spotify:artist:a4,Hilary Hahn,spotify:album:b3,Bach: Violin Partitas,spotify:artist:a4,Hilary Hahn,1997-03,https://i.scdn.co/image/ccc,2,5,900000,,false,30,USSM19700001,spotify:user:example,2024-01-03T00:00:00Z");

    [Fact]
    public void CanParse_RecognizesExportifyCsv()
    {
        var source = new SpotifyPlaylistSource();

        Assert.True(source.CanParse("evening_piano.csv", _evening));
        Assert.False(source.CanParse("Evening Piano.txt", "Name\tArtist\tComposer\tAlbum\n"));
    }

    [Fact]
    public void Parse_ReadsEveryField()
    {
        var playlist = Assert.Single(new SpotifyPlaylistSource().Parse("evening_piano.csv", _evening));

        Assert.Equal("Evening Piano", playlist.Name);
        Assert.False(playlist.IsLibrary);
        Assert.Equal(3, playlist.Tracks.Count);

        var clair = playlist.Tracks[0];
        Assert.Equal("Clair de lune", clair.Title);
        Assert.Equal("Lang Lang", clair.Artist);
        Assert.Null(clair.FeaturedArtists);
        Assert.Equal("Piano Book (Deluxe Edition)", clair.Album);
        Assert.Equal("Lang Lang", clair.AlbumArtist);
        Assert.Equal(2019, clair.Year);
        Assert.Equal(3, clair.TrackNumber);
        Assert.Equal(1, clair.DiscNumber);
        Assert.Equal(408, clair.DurationSeconds);
        Assert.Equal("DEUM71900001", clair.Isrc);
        Assert.Equal("https://i.scdn.co/image/aaa", clair.ArtworkUrl);

        var swan = playlist.Tracks[1];
        Assert.Equal("Yo-Yo Ma", swan.Artist);
        Assert.Equal(["Kathryn Stott"], swan.FeaturedArtists);
        Assert.Equal(2015, swan.Year); // year-only release date

        var chaconne = playlist.Tracks[2];
        Assert.Equal("Partita No. 2, BWV 1004: V. Chaconne", chaconne.Title); // quoted comma
        Assert.Equal(1997, chaconne.Year); // year-month release date
        Assert.Equal(2, chaconne.DiscNumber);
    }

    [Theory]
    [InlineData("Yo-Yo Ma, Kathryn Stott", "spotify:artist:a,spotify:artist:b", "Yo-Yo Ma|Kathryn Stott")]
    [InlineData("Orchestra of the Age of Enlightenment, The", "spotify:artist:a", "Orchestra of the Age of Enlightenment, The")]
    [InlineData("Yo-Yo Ma, Silk Road, The Ensemble", "spotify:artist:a,spotify:artist:b", "Yo-Yo Ma|Silk Road, The Ensemble")]
    [InlineData("Lang Lang", "spotify:artist:a", "Lang Lang")]
    [InlineData("Les Arts\\, Florissants, Hilary Hahn", "spotify:artist:a,spotify:artist:b", "Les Arts, Florissants|Hilary Hahn")]
    public void SplitArtists_UsesTheArtistIdsToKeepCommasInNames(string names, string uris, string expected)
        => Assert.Equal(expected, string.Join('|', SpotifyPlaylistSource.SplitArtists(names, uris)));

    [Theory]
    [InlineData("evening_piano.csv", "Evening Piano")]
    [InlineData("wedding_(clara_&_felix).csv", "Wedding (Clara & Felix)")]
    [InlineData("i_can’t_believe_it’s_not_bach!.csv", "I Can’t Believe It’s Not Bach!")]
    [InlineData("“_“.csv", "Spotify Playlist")]
    [InlineData("Already Named.csv", "Already Named")]
    [InlineData("music_for_the_evening.csv", "Music for the Evening")]
    [InlineData("the_art_of_fugue.csv", "The Art of Fugue")]
    public void NameFromFileName_RebuildsThePlaylistName(string fileName, string expected)
        => Assert.Equal(expected, SpotifyPlaylistSource.NameFromFileName(fileName));

    [Fact]
    public void ReadCsv_HandlesQuotesAndLineBreaks()
    {
        var rows = SpotifyPlaylistSource.ReadCsv("a,\"b, \"\"quoted\"\"\",\"line\nbreak\"\r\nc,d,e\n");

        Assert.Equal(2, rows.Count);
        Assert.Equal(["a", "b, \"quoted\"", "line\nbreak"], rows[0]);
        Assert.Equal(["c", "d", "e"], rows[1]);
    }

    [Fact]
    public void ParseArchive_OffersEachPlaylistWithLikedSongsLast()
    {
        var liked = string.Join('\n', Header, _evening.Split('\n')[1]);
        var zip = Zip(("liked.csv", liked), ("evening_piano.csv", _evening), ("strings.csv", string.Join('\n', Header, _evening.Split('\n')[2])), ("notes.txt", "ignored"));

        var (source, playlists) = PlaylistImportService.ParseArchive([new AppleMusicPlaylistSource(), new SpotifyPlaylistSource()], zip);

        Assert.Equal("spotify", source.Id);
        Assert.Equal(["Evening Piano", "Strings", "Liked Songs"], playlists.Select(p => p.Name));
        Assert.True(playlists[2].IsLibrary);
        Assert.Equal(["evening_piano.csv", "strings.csv", "liked.csv"], playlists.Select(p => p.Id));
        Assert.True(PlaylistImportService.IsZip(zip));
    }

    [Fact]
    public void ParseArchive_RejectsAZipWithoutPlaylists()
        => Assert.Throws<ArgumentException>(() => PlaylistImportService.ParseArchive([new SpotifyPlaylistSource()], Zip(("notes.txt", "nothing here"))));

    private static byte[] Zip(params (string Name, string Text)[] files)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in files)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open(), Encoding.UTF8);
                writer.Write(text);
            }
        }

        return buffer.ToArray();
    }
}
