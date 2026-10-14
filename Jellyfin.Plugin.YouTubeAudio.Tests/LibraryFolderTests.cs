using System;
using System.IO;
using Jellyfin.Plugin.YouTubeAudio.Services;
using Xunit;

namespace Jellyfin.Plugin.YouTubeAudio.Tests;

/// <summary>
/// Tests that imports join the library's existing artist and album folders when the names
/// differ only in case, punctuation or year.
/// </summary>
public sealed class LibraryFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "yta-folders-" + Guid.NewGuid().ToString("N"));

    public LibraryFolderTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, true);

    private string Dir(string relative) => Directory.CreateDirectory(Path.Combine(_root, relative)).FullName;

    [Fact]
    public void FindMatchingFolder_IgnoresCaseOfArtist()
    {
        var existing = Dir("Wiener Philharmoniker");
        Assert.Equal(existing, DownloadService.FindMatchingFolder(_root, "WIENER PHILHARMONIKER", false));
    }

    [Fact]
    public void FindMatchingFolder_IgnoresCaseAndYearOfAlbum()
    {
        var artist = Dir("Glenn Gould");
        var existing = Dir("Glenn Gould/Goldberg Variations (1981)");
        Assert.Equal(existing, DownloadService.FindMatchingFolder(artist, "GoLdBeRg Variations (1982)", true));
        Assert.Equal(existing, DownloadService.FindMatchingFolder(artist, "Goldberg Variations", true));
    }

    [Fact]
    public void FindMatchingFolder_KeepsYearForArtists()
    {
        Dir("Ensemble");
        Assert.Null(DownloadService.FindMatchingFolder(_root, "Ensemble (1990)", false));
    }

    [Fact]
    public void FindMatchingFolder_PrefersExactName()
    {
        Dir("Arvo Part");
        var exact = Dir("Arvo Pärt");
        Assert.Equal(exact, DownloadService.FindMatchingFolder(_root, "Arvo Pärt", false));
    }

    [Fact]
    public void FindMatchingFolder_DoesNotMatchDifferentAlbums()
    {
        var artist = Dir("Glenn Gould");
        Dir("Glenn Gould/Goldberg Variations (1981)");
        Assert.Null(DownloadService.FindMatchingFolder(artist, "Goldberg Variations Live", true));
        Assert.Null(DownloadService.FindMatchingFolder(artist, "!!!", true));
        Assert.Null(DownloadService.FindMatchingFolder(Path.Combine(_root, "Missing"), "Anything", false));
    }

    [Fact]
    public void AdoptAlbumTags_TakesExistingAlbumNameYearAndAlbumArtist()
    {
        var albumDir = Dir("Glenn Gould/Goldberg Variations (1981)");
        var existing = Path.Combine(albumDir, "Aria.opus");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "TestData", "silence.opus"), existing);
        using (var file = TagLib.File.Create(existing))
        {
            file.Tag.Album = "Goldberg Variations";
            file.Tag.Year = 1981;
            file.Tag.AlbumArtists = ["Glenn Gould"];
            file.Save();
        }

        var tag = new TagLib.Id3v2.Tag { Album = "GOLDBERG variations", Year = 1982, AlbumArtists = ["GLENN GOULD"] };
        Assert.True(DownloadService.AdoptAlbumTags(tag, albumDir));
        Assert.Equal("Goldberg Variations", tag.Album);
        Assert.Equal(1981u, tag.Year);
        Assert.Equal(["Glenn Gould"], tag.AlbumArtists);

        // Already matching: nothing to change.
        Assert.False(DownloadService.AdoptAlbumTags(tag, albumDir));
    }
}
