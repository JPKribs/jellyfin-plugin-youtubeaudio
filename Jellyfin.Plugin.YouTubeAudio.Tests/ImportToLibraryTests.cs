using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Plugin.YouTubeAudio.Models;
using Jellyfin.Plugin.YouTubeAudio.Services;
using Xunit;

namespace Jellyfin.Plugin.YouTubeAudio.Tests;

/// <summary>
/// Tests for moving downloads into the music library: same-titled songs, duplicates, and
/// removing the single an album version replaces.
/// </summary>
[Collection("Plugin")]
public sealed class ImportToLibraryTests
{
    private const string Artist = "Lang Lang";
    private const string Album = "Piano Book";

    [Fact]
    public async Task SecondSongWithTheSameTitle_IsImportedBesideTheFirst()
    {
        using var fx = new ServiceFixture();
        var first = fx.AddDownloaded("Interlude", Artist, Album, 3);
        var second = fx.AddDownloaded("Interlude", Artist, Album, 9);

        var result = await fx.Service.ImportToLibraryAsync([first.Id]);
        var firstPath = Path.Combine(fx.Library, Artist, $"{Album} (2019)", "Interlude.opus");
        var firstHash = ServiceFixture.Hash(firstPath);
        var second_ = await fx.Service.ImportToLibraryAsync([second.Id]);

        Assert.Equal([first.Id], result.ImportedIds);
        Assert.Equal([second.Id], second_.ImportedIds);
        Assert.Empty(second_.ReplacedIds);
        Assert.Equal(["Lang Lang/Piano Book (2019)/Interlude (2).opus", "Lang Lang/Piano Book (2019)/Interlude.opus"], fx.LibraryFiles());
        Assert.Equal(firstHash, ServiceFixture.Hash(firstPath));
        Assert.Equal(("Interlude", 3u), ServiceFixture.ReadTitleAndTrack(firstPath));
        Assert.Equal(("Interlude", 9u), ServiceFixture.ReadTitleAndTrack(Path.Combine(fx.Library, Artist, $"{Album} (2019)", "Interlude (2).opus")));
    }

    [Fact]
    public async Task SameTitledSongsInOneBatch_AreAllKept()
    {
        using var fx = new ServiceFixture();
        var ids = new List<string>();
        for (uint track = 1; track <= 3; track++)
        {
            ids.Add(fx.AddDownloaded("Reprise", Artist, Album, track).Id);
        }

        var result = await fx.Service.ImportToLibraryAsync(ids);

        Assert.Equal(ids, result.ImportedIds);
        Assert.Equal(
            ["Lang Lang/Piano Book (2019)/Reprise (2).opus", "Lang Lang/Piano Book (2019)/Reprise (3).opus", "Lang Lang/Piano Book (2019)/Reprise.opus"],
            fx.LibraryFiles());
    }

    [Fact]
    public async Task SameTrackAgain_ReplacesItWhenReplaceDuplicatesIsOn()
    {
        using var fx = new ServiceFixture(c => c.ReplaceDuplicates = true);
        var path = Path.Combine(fx.Library, Artist, $"{Album} (2019)", "Clair de lune.opus");
        ServiceFixture.WriteSong(path, "Clair de lune", Artist, Album, 3);
        var item = fx.AddDownloaded("Clair de lune", Artist, Album, 3);

        var result = await fx.Service.ImportToLibraryAsync([item.Id]);

        Assert.Equal([item.Id], result.ImportedIds);
        Assert.Equal([item.Id], result.ReplacedIds);
        Assert.Equal(["Lang Lang/Piano Book (2019)/Clair de lune.opus"], fx.LibraryFiles());
        Assert.False(File.Exists(Path.Combine(fx.CacheDir, item.FileName)));
    }

    [Fact]
    public async Task SameTrackAgain_IsSkippedWhenReplaceDuplicatesIsOff()
    {
        using var fx = new ServiceFixture(c => c.ReplaceDuplicates = false);
        var path = Path.Combine(fx.Library, Artist, $"{Album} (2019)", "Clair de lune.opus");
        ServiceFixture.WriteSong(path, "Clair de lune", Artist, Album, 3);
        var before = ServiceFixture.Hash(path);
        var item = fx.AddDownloaded("Clair de lune", Artist, Album, 3);

        var result = await fx.Service.ImportToLibraryAsync([item.Id]);

        Assert.Equal([item.Id], result.SkippedIds);
        Assert.Empty(result.ImportedIds);
        Assert.Equal(before, ServiceFixture.Hash(path));
        Assert.True(File.Exists(Path.Combine(fx.CacheDir, item.FileName)));
    }

    [Fact]
    public async Task AlbumVersion_RemovesTheSingleAndItsEmptyFolders_ButNothingOutsideTheLibrary()
    {
        using var fx = new ServiceFixture();
        var single = Path.Combine(fx.Library, Artist, "Clair de lune (2018)", "Clair de lune.opus");
        ServiceFixture.WriteSong(single, "Clair de lune", Artist, "Clair de lune", 1, 2018);
        var outside = Path.Combine(fx.Root, "elsewhere", "Clair de lune.opus");
        ServiceFixture.WriteSong(outside, "Clair de lune", Artist, "Clair de lune", 1, 2018);
        var item = fx.AddDownloaded("Clair de lune", Artist, Album, 3, sourceTrack: new PlaylistTrack
        {
            Title = "Clair de lune",
            Artist = Artist,
            ReplacesFiles = [single, outside]
        });

        var result = await fx.Service.ImportToLibraryAsync([item.Id]);

        Assert.Equal([item.Id], result.ImportedIds);
        Assert.Equal(1, result.RemovedFiles);
        Assert.Equal(["Lang Lang/Piano Book (2019)/Clair de lune.opus"], fx.LibraryFiles());
        Assert.False(Directory.Exists(Path.GetDirectoryName(single)));
        Assert.True(File.Exists(outside));
        Assert.Contains(single, result.ChangedPaths);
    }

    [Fact]
    public async Task AlbumVersion_KeepsAFolderThatStillHasOtherFiles()
    {
        using var fx = new ServiceFixture();
        var single = Path.Combine(fx.Library, Artist, "Clair de lune (2018)", "Clair de lune.opus");
        ServiceFixture.WriteSong(single, "Clair de lune", Artist, "Clair de lune", 1, 2018);
        var cover = Path.Combine(Path.GetDirectoryName(single)!, "folder.jpg");
        File.WriteAllBytes(cover, [1, 2, 3]);
        var item = fx.AddDownloaded("Clair de lune", Artist, Album, 3, sourceTrack: new PlaylistTrack { Title = "Clair de lune", Artist = Artist, ReplacesFiles = [single] });

        var result = await fx.Service.ImportToLibraryAsync([item.Id]);

        Assert.Equal(1, result.RemovedFiles);
        Assert.False(File.Exists(single));
        Assert.True(File.Exists(cover));
    }

    [Fact]
    public async Task NotDownloadedOrMissingItems_AreReportedAndNothingMoves()
    {
        using var fx = new ServiceFixture();
        var item = fx.AddDownloaded("Clair de lune", Artist, Album, 3);
        fx.Db.UpdateStatus(item.Id, QueueStatus.Queued);

        var result = await fx.Service.ImportToLibraryAsync([item.Id, "missing"]);

        Assert.Empty(result.ImportedIds);
        Assert.Equal(2, result.Errors.Count);
        Assert.Empty(fx.LibraryFiles());
    }

    [Fact]
    public async Task ReDownloadFromAnotherUpload_IsTheSameSong_NotACopy()
    {
        using var fx = new ServiceFixture(c => c.ReplaceDuplicates = true);
        var path = Path.Combine(fx.Library, Artist, $"{Album} (2019)", "Clair de lune.opus");

        // The library copy has no track number; the new upload (a music video with an intro,
        // so a different length) has one. It is still the same song.
        ServiceFixture.WriteSong(path, "Clair de lune", Artist, Album, 0);
        var item = fx.AddDownloaded("Clair de lune", Artist, Album, 3);

        var result = await fx.Service.ImportToLibraryAsync([item.Id]);

        Assert.Equal([item.Id], result.ReplacedIds);
        Assert.Equal(["Lang Lang/Piano Book (2019)/Clair de lune.opus"], fx.LibraryFiles());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnreadableLibraryFile_FollowsTheDuplicateSetting(bool replaceDuplicates)
    {
        using var fx = new ServiceFixture(c => c.ReplaceDuplicates = replaceDuplicates);
        var path = Path.Combine(fx.Library, Artist, $"{Album} (2019)", "Clair de lune.opus");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "not audio");
        var item = fx.AddDownloaded("Clair de lune", Artist, Album, 3);

        var result = await fx.Service.ImportToLibraryAsync([item.Id]);

        Assert.Empty(result.Errors);
        Assert.Equal(["Lang Lang/Piano Book (2019)/Clair de lune.opus"], fx.LibraryFiles());
        if (replaceDuplicates)
        {
            Assert.Equal([item.Id], result.ReplacedIds);
            Assert.NotEqual("not audio", File.ReadAllText(path));
        }
        else
        {
            Assert.Equal([item.Id], result.SkippedIds);
            Assert.Equal("not audio", File.ReadAllText(path));
        }
    }

    // MARK: Destination rules

    [Fact]
    public void IsDifferentTrack_OnlyWhenBothTrackNumbersDiffer()
    {
        using var fx = new ServiceFixture();
        var numbered = Path.Combine(fx.Root, "numbered.opus");
        ServiceFixture.WriteSong(numbered, "Interlude", Artist, Album, 3);
        var unnumbered = Path.Combine(fx.Root, "unnumbered.opus");
        ServiceFixture.WriteSong(unnumbered, "Interlude", Artist, Album, 0);

        Assert.True(DownloadService.IsDifferentTrack(numbered, Tag("Interlude", Artist, 9)));
        Assert.False(DownloadService.IsDifferentTrack(numbered, Tag("Interlude", Artist, 3)));
        Assert.False(DownloadService.IsDifferentTrack(numbered, Tag("Interlude", Artist, 0)));
        Assert.False(DownloadService.IsDifferentTrack(unnumbered, Tag("Interlude", Artist, 9)));
    }

    [Fact]
    public void IsDifferentTrack_ComparesDiscNumbersWhenBothHaveOne()
    {
        using var fx = new ServiceFixture();
        var path = Path.Combine(fx.Root, "song.opus");
        ServiceFixture.WriteSong(path, "Interlude", Artist, Album, 3);
        using (var file = TagLib.File.Create(path))
        {
            file.Tag.Disc = 1;
            file.Save();
        }

        var discTwo = Tag("Interlude", Artist, 3);
        discTwo.Disc = 2;
        var noDisc = Tag("Interlude", Artist, 3);

        Assert.True(DownloadService.IsDifferentTrack(path, discTwo));
        Assert.False(DownloadService.IsDifferentTrack(path, noDisc));
    }

    [Fact]
    public void IsDifferentTrack_UnreadableFile_IsTheSameTrack()
    {
        using var fx = new ServiceFixture();
        var path = Path.Combine(fx.Root, "broken.opus");
        File.WriteAllText(path, "not audio");

        Assert.False(DownloadService.IsDifferentTrack(path, Tag("Interlude", Artist, 3)));
        Assert.Equal((path, true), DownloadService.ChooseDestination(fx.Root, "broken", ".opus", Tag("Interlude", Artist, 3)));
    }

    [Fact]
    public void ChooseDestination_ReturnsTheExistingCopyOfTheSameTrack()
    {
        using var fx = new ServiceFixture();
        ServiceFixture.WriteSong(Path.Combine(fx.Root, "Interlude.opus"), "Interlude", Artist, Album, 3);
        ServiceFixture.WriteSong(Path.Combine(fx.Root, "Interlude (2).opus"), "Interlude", Artist, Album, 9);

        Assert.Equal((Path.Combine(fx.Root, "Interlude (2).opus"), true), DownloadService.ChooseDestination(fx.Root, "Interlude", ".opus", Tag("Interlude", Artist, 9)));
        Assert.Equal((Path.Combine(fx.Root, "Interlude (3).opus"), false), DownloadService.ChooseDestination(fx.Root, "Interlude", ".opus", Tag("Interlude", Artist, 12)));
        Assert.Equal((Path.Combine(fx.Root, "Interlude.opus"), true), DownloadService.ChooseDestination(fx.Root, "Interlude", ".opus", Tag("Interlude", Artist, 0)));
        Assert.Equal((Path.Combine(fx.Root, "Interlude.mp3"), false), DownloadService.ChooseDestination(fx.Root, "Interlude", ".mp3", Tag("Interlude", Artist, 12)));
    }

    private static TagLib.Tag Tag(string title, string artist, uint track)
        => new TagLib.Id3v2.Tag { Title = title, Performers = [artist], Track = track };
}
