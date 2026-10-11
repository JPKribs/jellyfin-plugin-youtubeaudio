using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.YouTubeAudio.Models;
using Jellyfin.Plugin.YouTubeAudio.Services.Playlists;
using Xunit;

namespace Jellyfin.Plugin.YouTubeAudio.Tests;

/// <summary>
/// Tests for deciding which songs go into a user's Jellyfin playlist.
/// </summary>
public class PlaylistSyncTests
{
    private static readonly Guid _clairDeLuneAlbum = Guid.NewGuid();
    private static readonly Guid _clairDeLuneSingle = Guid.NewGuid();
    private static readonly Guid _furElise = Guid.NewGuid();

    private static List<LibraryTrack> Library() =>
    [
        new() { ItemId = _clairDeLuneSingle, Title = "Clair de lune", Artist = "Lang Lang", AlbumArtist = "Lang Lang", Album = "Clair de lune" },
        new() { ItemId = _clairDeLuneAlbum, Title = "Clair de lune", Artist = "Lang Lang", AlbumArtist = "Lang Lang", Album = "Piano Book" },
        new() { ItemId = _furElise, Title = "Für Elise", Artist = "Lang Lang", AlbumArtist = "Lang Lang", Album = "Piano Book" }
    ];

    private static PlaylistEntry Entry(int position, string artist, string title)
        => new() { Position = position, SongKey = TrackMetadataCleaner.SongKey(artist, title), Artist = artist, Title = title };

    [Fact]
    public void BuildLibraryIndex_PrefersTheAlbumVersion()
    {
        var index = PlaylistSyncService.BuildLibraryIndex(Library());

        Assert.Equal(_clairDeLuneAlbum, index[TrackMetadataCleaner.SongKey("Lang Lang", "Clair de lune")]);
    }

    [Fact]
    public void PlanAdditions_AddsAvailableSongsInOrderAndSkipsOnesThePlaylistHas()
    {
        var index = PlaylistSyncService.BuildLibraryIndex(Library());
        var pending = new[]
        {
            Entry(2, "Lang Lang", "Für Elise"),
            Entry(0, "Lang Lang", "Clair de lune"),
            Entry(1, "Lang Lang", "Gymnopédie No. 1"),
            Entry(3, "Lang Lang", "Rêverie")
        };
        var inPlaylist = new HashSet<string> { TrackMetadataCleaner.SongKey("Lang Lang", "Rêverie") };

        var (itemIds, doneKeys) = PlaylistSyncService.PlanAdditions(pending, index, inPlaylist);

        // Gymnopédie is not downloaded yet, so it stays pending; Rêverie is already there.
        Assert.Equal([_clairDeLuneAlbum, _furElise], itemIds);
        Assert.Equal(3, doneKeys.Count);
        Assert.DoesNotContain(TrackMetadataCleaner.SongKey("Lang Lang", "Gymnopédie No. 1"), doneKeys);
    }

    [Fact]
    public void PlanAdditions_TreatsAnyVersionInThePlaylistAsPresent()
    {
        // The playlist already holds the song under a "feat."-titled older import.
        var index = PlaylistSyncService.BuildLibraryIndex(
            [new LibraryTrack { ItemId = Guid.NewGuid(), Title = "Hungarian Rhapsody No. 2", Artist = "Lang Lang", Album = "Hungarian Rhapsody No. 2" }]);
        var inPlaylist = new HashSet<string> { TrackMetadataCleaner.SongKey("Lang Lang", "Hungarian Rhapsody No. 2 (feat. Yuja Wang)") };

        var (itemIds, doneKeys) = PlaylistSyncService.PlanAdditions([Entry(0, "Lang Lang", "Hungarian Rhapsody No. 2")], index, inPlaylist);

        Assert.Empty(itemIds);
        Assert.Single(doneKeys);
    }

    [Fact]
    public void BuildPlaylistEntries_ListsEachSongOnceInOrder()
    {
        var content = PlaylistImportService.DecodeText(File.ReadAllBytes(Path.Combine("TestData", "AppleMusicLibrary.xml")));
        var sundayMix = new AppleMusicLibrarySource().Parse("Library.xml", content).Single(p => p.Name == "Sunday Mix");

        var entries = PlaylistImportService.BuildPlaylistEntries(sundayMix);

        Assert.Equal(sundayMix.Tracks.Count, entries.Count);
        Assert.Equal(Enumerable.Range(0, entries.Count), entries.Select(e => e.Position));
        // "feat." is moved out of the title, as for queued tracks.
        Assert.Contains(entries, e => e.Artist == "Lang Lang" && e.Title == "Hungarian Rhapsody No. 2");
    }

    [Fact]
    public void LibraryExport_MarksOnlyTheWholeLibraryAsLibrary()
    {
        var content = PlaylistImportService.DecodeText(File.ReadAllBytes(Path.Combine("TestData", "AppleMusicLibrary.xml")));
        var playlists = new AppleMusicLibrarySource().Parse("Library.xml", content);

        Assert.Equal(["Entire Library"], playlists.Where(p => p.IsLibrary).Select(p => p.Name));
    }

    // MARK: Ordering

    [Fact]
    public void PlanOrder_PutsSourceSongsInSourceOrderAndLeavesUserSongsInPlace()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid(), mine = Guid.NewGuid();
        var current = new List<(Guid Id, string? Key)> { (c, "c"), (mine, "mine"), (a, "a"), (b, "b") };
        var positions = new Dictionary<string, int> { ["a"] = 0, ["b"] = 1, ["c"] = 2 };

        var desired = PlaylistSyncService.PlanOrder(current, positions);

        // Slots 0, 2, 3 hold source songs and are refilled a, b, c; the user's song stays at 1.
        Assert.Equal([a, mine, b, c], desired);
    }

    [Fact]
    public void PlanOrder_LeavesAnOrderedPlaylistUnchanged()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        var current = new List<(Guid Id, string? Key)> { (a, "a"), (b, "b") };

        Assert.Equal([a, b], PlaylistSyncService.PlanOrder(current, new Dictionary<string, int> { ["a"] = 0, ["b"] = 1 }));
    }

    // MARK: Abandoned songs

    [Fact]
    public void IsAbandoned_OnlyWhenNotInLibraryQueueOrAwaitingScan()
    {
        var now = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);
        var library = new Dictionary<string, Guid> { ["in|library"] = Guid.NewGuid() };
        var queue = new HashSet<string> { "in|queue" };

        Assert.False(PlaylistSyncService.IsAbandoned(new PlaylistEntry { SongKey = "in|library" }, library, queue, now));
        Assert.False(PlaylistSyncService.IsAbandoned(new PlaylistEntry { SongKey = "in|queue" }, library, queue, now));
        Assert.False(PlaylistSyncService.IsAbandoned(new PlaylistEntry { SongKey = "just|imported", ImportedAt = now.AddMinutes(-5).ToString("o") }, library, queue, now));
        Assert.True(PlaylistSyncService.IsAbandoned(new PlaylistEntry { SongKey = "long|ago", ImportedAt = now.AddHours(-2).ToString("o") }, library, queue, now));
        Assert.True(PlaylistSyncService.IsAbandoned(new PlaylistEntry { SongKey = "failed|download" }, library, queue, now));
    }
}
