using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.YouTubeAudio.Models;
using Jellyfin.Plugin.YouTubeAudio.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.YouTubeAudio.Tests;

/// <summary>
/// Tests for the queue database schema and the playlist-import columns.
/// </summary>
public sealed class QueueDatabaseTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), $"yta-db-{Guid.NewGuid():N}");

    public QueueDatabaseTests() => Directory.CreateDirectory(_dataPath);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dataPath, recursive: true);
    }

    private QueueDatabase Open() => new(NullLogger<QueueDatabase>.Instance, _dataPath);

    private static QueueItem NewItem(string url = "") => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Url = url,
        Title = "Clair de lune",
        UserTitled = true,
        FileName = "a.opus",
        CreatedAt = "2026-10-11T00:00:00Z",
        UpdatedAt = "2026-10-11T00:00:00Z",
        Artist = "Lang Lang",
        Source = "applemusic",
        SourceTrack = new PlaylistTrack { Title = "Clair de lune", Artist = "Lang Lang", TrackNumber = 3, DurationSeconds = 408 }
    };

    [Fact]
    public void PlaylistTrack_RoundTripsAndResolvedUrlIsRecorded()
    {
        using var db = Open();
        var item = NewItem();
        db.AddItem(item);

        db.UpdateResolvedUrl(item.Id, "https://www.youtube.com/watch?v=85aTjlh66WA", "Length differs by 5s");

        var read = db.GetItemById(item.Id)!;
        Assert.Equal("applemusic", read.Source);
        Assert.Equal(3, read.SourceTrack!.TrackNumber);
        Assert.Equal(408, read.SourceTrack.DurationSeconds);
        Assert.Equal("https://www.youtube.com/watch?v=85aTjlh66WA", read.Url);
        Assert.Equal("Length differs by 5s", read.MatchNote);
    }

    [Fact]
    public void Migration_FromV3_AddsPlaylistColumnsAndKeepsRows()
    {
        var dbPath = Path.Combine(_dataPath, "youtubeaudio", "queue.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        using (var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE QueueItems (
                    Id TEXT PRIMARY KEY, Url TEXT NOT NULL, Title TEXT, FileName TEXT NOT NULL,
                    Status INTEGER NOT NULL DEFAULT 0, ErrorMessage TEXT, CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL, Artist TEXT, Album TEXT, Year INTEGER,
                    UserTitled INTEGER NOT NULL DEFAULT 0);
                INSERT INTO QueueItems (Id, Url, FileName, CreatedAt, UpdatedAt)
                VALUES ('old', 'https://youtu.be/abc', 'b.opus', '2026-01-01', '2026-01-01');
                PRAGMA user_version = 3;";
            cmd.ExecuteNonQuery();
        }

        using var db = Open();

        var old = db.GetItemById("old")!;
        Assert.Equal("https://youtu.be/abc", old.Url);
        Assert.Null(old.Source);
        Assert.Null(old.SourceTrack);

        var item = NewItem();
        db.AddItem(item);
        Assert.Equal("applemusic", db.GetItemById(item.Id)!.Source);
    }

    private string DbPath => Path.Combine(_dataPath, "youtubeaudio", "queue.db");

    private static long Scalar(string dbPath, string sql)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Execute(string dbPath, string sql)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void ExistingV5Database_KeepsRowsAndVersion()
    {
        var item = NewItem();
        using (var db = Open())
        {
            db.AddItem(item);
        }

        // Make it look like a database from a build before the reader marker.
        Execute(DbPath, "DROP TABLE SchemaInfo");

        using (var db = Open())
        {
            Assert.NotNull(db.GetItemById(item.Id));
        }

        Assert.Equal(5, Scalar(DbPath, "PRAGMA user_version"));
        Assert.Equal(1, Scalar(DbPath, "SELECT Value FROM SchemaInfo WHERE Key = 'MinReaderVersion'"));
    }

    [Fact]
    public void NewerReadableDatabase_IsUsedUntouched()
    {
        var item = NewItem();
        using (var db = Open())
        {
            db.AddItem(item);
        }

        Execute(DbPath, "PRAGMA user_version = 9; UPDATE SchemaInfo SET Value = 4");

        using (var db = Open())
        {
            Assert.NotNull(db.GetItemById(item.Id));
        }

        Assert.Equal(9, Scalar(DbPath, "PRAGMA user_version"));
    }

    [Fact]
    public void NewerUnreadableDatabase_IsSetAsideAndReplaced()
    {
        var item = NewItem();
        using (var db = Open())
        {
            db.AddItem(item);
        }

        Execute(DbPath, "PRAGMA user_version = 9; UPDATE SchemaInfo SET Value = 8");

        using (var db = Open())
        {
            Assert.Null(db.GetItemById(item.Id));
        }

        Assert.Equal(5, Scalar(DbPath, "PRAGMA user_version"));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(DbPath)!, "queue.db.newer-*"), f => !f.EndsWith("-wal", StringComparison.Ordinal) && !f.EndsWith("-shm", StringComparison.Ordinal));
    }

    // MARK: Playlist targets

    private static PlaylistEntry Entry(string key, string title, int position = 0) => new() { SongKey = key, Title = title, Position = position };

    [Fact]
    public void PlaylistTarget_IsReusedForSameUserAndNameIgnoringCase()
    {
        using var db = Open();

        var first = db.GetOrCreatePlaylistTarget("user1", "Study Session", "applemusic");
        var again = db.GetOrCreatePlaylistTarget("user1", "study session", "applemusic");
        var otherUser = db.GetOrCreatePlaylistTarget("user2", "Study Session", "applemusic");

        Assert.Equal(first.Id, again.Id);
        Assert.Equal("Study Session", again.Name);
        Assert.NotEqual(first.Id, otherUser.Id);
    }

    [Fact]
    public void PlaylistEntries_ReimportAddsNewSongsAndTakesTheNewOrder()
    {
        using var db = Open();
        var target = db.GetOrCreatePlaylistTarget("user1", "Mix", null);

        Assert.Equal(2, db.AddPlaylistEntries(target.Id, [Entry("a|one", "One", 0), Entry("a|two", "Two", 1)]));
        db.MarkPlaylistEntriesAdded(target.Id, ["a|one"]);

        // The source playlist was re-ordered and gained a song.
        Assert.Equal(1, db.AddPlaylistEntries(target.Id, [Entry("a|three", "Three", 0), Entry("a|two", "Two", 1), Entry("a|one", "One", 2)]));

        var entries = db.GetPlaylistEntries(target.Id);
        Assert.Equal(["Three", "Two", "One"], entries.Select(e => e.Title));
        Assert.NotNull(entries.Single(e => e.Title == "One").AddedAt);
    }

    [Fact]
    public void PlaylistEntries_ImportMarkAndDeletion()
    {
        using var db = Open();
        var target = db.GetOrCreatePlaylistTarget("user1", "Mix", null);
        db.AddPlaylistEntries(target.Id, [Entry("a|one", "One", 0), Entry("a|two", "Two", 1)]);

        db.MarkPlaylistEntriesImported("a|one");
        Assert.NotNull(db.GetPlaylistEntries(target.Id).Single(e => e.Title == "One").ImportedAt);

        db.DeletePlaylistEntries(target.Id, ["a|two"]);
        Assert.Equal(["One"], db.GetPlaylistEntries(target.Id).Select(e => e.Title));
    }

    [Fact]
    public void PlaylistEntries_TrackPendingAndCanBeResetForAReplacedSong()
    {
        using var db = Open();
        var target = db.GetOrCreatePlaylistTarget("user1", "Mix", null);
        db.AddPlaylistEntries(target.Id, [Entry("a|one", "One"), Entry("a|two", "Two")]);

        db.MarkPlaylistEntriesAdded(target.Id, ["a|one"]);
        Assert.Equal(["Two"], db.GetPlaylistEntries(pendingOnly: true).Select(e => e.Title));

        db.ResetPlaylistEntriesForSong("a|one");
        Assert.Equal(2, db.GetPlaylistEntries(pendingOnly: true).Count);

        db.SetJellyfinPlaylistId(target.Id, "abc");
        Assert.Equal("abc", db.GetPlaylistTargets().Single().JellyfinPlaylistId);

        db.DeletePlaylistTarget(target.Id);
        Assert.Empty(db.GetPlaylistTargets());
        Assert.Empty(db.GetPlaylistEntries());
    }
}
