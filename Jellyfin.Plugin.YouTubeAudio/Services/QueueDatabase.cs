using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Jellyfin.Plugin.YouTubeAudio.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

#pragma warning disable CA2100 // SQL commands use only internal constants and safe parameterized queries

namespace Jellyfin.Plugin.YouTubeAudio.Services;

/// <summary>
/// SQLite database for tracking download queue items.
/// </summary>
public sealed class QueueDatabase : IDisposable
{
    private const int SchemaVersion = 5;

    // Playlist targets: one-time playlist imports in progress, filling a user's Jellyfin playlist
    // as songs reach the library. A target is deleted once nothing is left pending; re-importing
    // the playlist later finds the Jellyfin playlist by name. ImportedAt marks songs moved into
    // the library but not yet scanned, so they are not mistaken for abandoned downloads.
    private const string PlaylistSchema = @"
            CREATE TABLE IF NOT EXISTS PlaylistTargets (
                Id TEXT PRIMARY KEY,
                UserId TEXT NOT NULL,
                Name TEXT NOT NULL COLLATE NOCASE,
                Source TEXT,
                JellyfinPlaylistId TEXT,
                CreatedAt TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS idx_playlist_target_user_name ON PlaylistTargets(UserId, Name);
            CREATE TABLE IF NOT EXISTS PlaylistEntries (
                TargetId TEXT NOT NULL,
                Position INTEGER NOT NULL,
                SongKey TEXT NOT NULL,
                Artist TEXT,
                Title TEXT NOT NULL,
                AddedAt TEXT,
                ImportedAt TEXT,
                PRIMARY KEY (TargetId, SongKey)
            );
        ";

    // Column order must match ReadQueueItem.
    private const string SelectColumns = "Id, Url, Title, FileName, Status, ErrorMessage, CreatedAt, UpdatedAt, Artist, Album, Year, UserTitled, Source, SourceTrack, MatchNote";

    private readonly ILogger<QueueDatabase> _logger;
    private readonly string _dbPath;
    private readonly object _writeLock = new();
    private SqliteConnection? _connection;
    private volatile bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueueDatabase"/> class.
    /// </summary>
    public QueueDatabase(ILogger<QueueDatabase> logger, string dataPath)
    {
        _logger = logger;
        var dbDir = Path.Combine(dataPath, "youtubeaudio");
        Directory.CreateDirectory(dbDir);
        _dbPath = Path.Combine(dbDir, "queue.db");

        // Also create cache subdirectory
        Directory.CreateDirectory(Path.Combine(dbDir, "cache"));

        _logger.LogInformation("Queue database path: {DbPath}", _dbPath);

        InitializeDatabase();
    }

    /// <summary>
    /// Adds a new item to the queue.
    /// </summary>
    public void AddItem(QueueItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        lock (_writeLock)
        {
            ThrowIfDisposed();
            EnsureConnection();

            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO QueueItems (Id, Url, Title, FileName, Status, ErrorMessage, CreatedAt, UpdatedAt, Artist, Album, Year, UserTitled, Source, SourceTrack, MatchNote)
                VALUES (@Id, @Url, @Title, @FileName, @Status, @ErrorMessage, @CreatedAt, @UpdatedAt, @Artist, @Album, @Year, @UserTitled, @Source, @SourceTrack, @MatchNote)";
            cmd.Parameters.AddWithValue("@Id", item.Id);
            cmd.Parameters.AddWithValue("@Url", item.Url);
            cmd.Parameters.AddWithValue("@Title", (object?)item.Title ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@FileName", item.FileName);
            cmd.Parameters.AddWithValue("@Status", (int)item.Status);
            cmd.Parameters.AddWithValue("@ErrorMessage", (object?)item.ErrorMessage ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@CreatedAt", item.CreatedAt);
            cmd.Parameters.AddWithValue("@UpdatedAt", item.UpdatedAt);
            cmd.Parameters.AddWithValue("@Artist", (object?)item.Artist ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Album", (object?)item.Album ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Year", (object?)item.Year ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@UserTitled", item.UserTitled ? 1 : 0);
            cmd.Parameters.AddWithValue("@Source", (object?)item.Source ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@SourceTrack", item.SourceTrack == null ? DBNull.Value : JsonSerializer.Serialize(item.SourceTrack));
            cmd.Parameters.AddWithValue("@MatchNote", (object?)item.MatchNote ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Gets all queue items ordered by creation date descending.
    /// </summary>
    public List<QueueItem> GetAllItems()
    {
        return ExecuteRead(
            () =>
            {
                var items = new List<QueueItem>();
                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = $"SELECT {SelectColumns} FROM QueueItems ORDER BY CreatedAt DESC, rowid DESC";

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    items.Add(ReadQueueItem(reader));
                }

                return items;
            },
            new List<QueueItem>());
    }

    /// <summary>
    /// Gets queue items by status.
    /// </summary>
    public List<QueueItem> GetItemsByStatus(QueueStatus status)
    {
        return ExecuteRead(
            () =>
            {
                var items = new List<QueueItem>();
                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = $"SELECT {SelectColumns} FROM QueueItems WHERE Status = @Status ORDER BY CreatedAt ASC, rowid ASC";
                cmd.Parameters.AddWithValue("@Status", (int)status);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    items.Add(ReadQueueItem(reader));
                }

                return items;
            },
            new List<QueueItem>());
    }

    /// <summary>
    /// Gets a single queue item by ID.
    /// </summary>
    public QueueItem? GetItemById(string id)
    {
        return ExecuteRead(
            () =>
            {
                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = $"SELECT {SelectColumns} FROM QueueItems WHERE Id = @Id";
                cmd.Parameters.AddWithValue("@Id", id);

                using var reader = cmd.ExecuteReader();
                return reader.Read() ? ReadQueueItem(reader) : null;
            },
            null);
    }

    /// <summary>
    /// Updates the status of a queue item.
    /// </summary>
    public void UpdateStatus(string id, QueueStatus status, string? errorMessage = null)
    {
        lock (_writeLock)
        {
            ThrowIfDisposed();
            EnsureConnection();

            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "UPDATE QueueItems SET Status = @Status, ErrorMessage = @ErrorMessage, UpdatedAt = @UpdatedAt WHERE Id = @Id";
            cmd.Parameters.AddWithValue("@Status", (int)status);
            cmd.Parameters.AddWithValue("@ErrorMessage", (object?)errorMessage ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@UpdatedAt", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("@Id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Updates the title of a queue item.
    /// </summary>
    public void UpdateTitle(string id, string title)
    {
        lock (_writeLock)
        {
            ThrowIfDisposed();
            EnsureConnection();

            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "UPDATE QueueItems SET Title = @Title, UpdatedAt = @UpdatedAt WHERE Id = @Id";
            cmd.Parameters.AddWithValue("@Title", title);
            cmd.Parameters.AddWithValue("@UpdatedAt", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("@Id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Records the YouTube match found for a playlist-imported track.
    /// </summary>
    public void UpdateResolvedUrl(string id, string url, string? matchNote)
    {
        lock (_writeLock)
        {
            ThrowIfDisposed();
            EnsureConnection();

            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "UPDATE QueueItems SET Url = @Url, MatchNote = @MatchNote, UpdatedAt = @UpdatedAt WHERE Id = @Id";
            cmd.Parameters.AddWithValue("@Url", url);
            cmd.Parameters.AddWithValue("@MatchNote", (object?)matchNote ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@UpdatedAt", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("@Id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Returns the target for a user's playlist name, creating it if needed. Names compare
    /// case-insensitively, so re-importing "Study Session" reuses the same target.
    /// </summary>
    public PlaylistTarget GetOrCreatePlaylistTarget(string userId, string name, string? source)
    {
        lock (_writeLock)
        {
            ThrowIfDisposed();
            EnsureConnection();

            using (var insert = _connection!.CreateCommand())
            {
                insert.CommandText = @"
                    INSERT OR IGNORE INTO PlaylistTargets (Id, UserId, Name, Source, CreatedAt)
                    VALUES (@Id, @UserId, @Name, @Source, @CreatedAt)";
                insert.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString("N"));
                insert.Parameters.AddWithValue("@UserId", userId);
                insert.Parameters.AddWithValue("@Name", name);
                insert.Parameters.AddWithValue("@Source", (object?)source ?? DBNull.Value);
                insert.Parameters.AddWithValue("@CreatedAt", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                insert.ExecuteNonQuery();
            }

            using var select = _connection.CreateCommand();
            select.CommandText = "SELECT Id, UserId, Name, Source, JellyfinPlaylistId, CreatedAt FROM PlaylistTargets WHERE UserId = @UserId AND Name = @Name";
            select.Parameters.AddWithValue("@UserId", userId);
            select.Parameters.AddWithValue("@Name", name);
            using var reader = select.ExecuteReader();
            reader.Read();
            return ReadPlaylistTarget(reader);
        }
    }

    /// <summary>
    /// Records a playlist's songs on a target in source order. Songs the target already has
    /// keep their progress but take the new position, so the latest import defines the order.
    /// </summary>
    /// <returns>The number of new songs.</returns>
    public int AddPlaylistEntries(string targetId, IEnumerable<PlaylistEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        lock (_writeLock)
        {
            ThrowIfDisposed();
            EnsureConnection();

            using var transaction = _connection!.BeginTransaction();
            var added = 0;
            foreach (var entry in entries)
            {
                using var update = _connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE PlaylistEntries SET Position = @Position WHERE TargetId = @TargetId AND SongKey = @SongKey";
                update.Parameters.AddWithValue("@TargetId", targetId);
                update.Parameters.AddWithValue("@Position", entry.Position);
                update.Parameters.AddWithValue("@SongKey", entry.SongKey);
                if (update.ExecuteNonQuery() > 0)
                {
                    continue;
                }

                using var insert = _connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = @"
                    INSERT INTO PlaylistEntries (TargetId, Position, SongKey, Artist, Title)
                    VALUES (@TargetId, @Position, @SongKey, @Artist, @Title)";
                insert.Parameters.AddWithValue("@TargetId", targetId);
                insert.Parameters.AddWithValue("@Position", entry.Position);
                insert.Parameters.AddWithValue("@SongKey", entry.SongKey);
                insert.Parameters.AddWithValue("@Artist", (object?)entry.Artist ?? DBNull.Value);
                insert.Parameters.AddWithValue("@Title", entry.Title);
                insert.ExecuteNonQuery();
                added++;
            }

            transaction.Commit();
            return added;
        }
    }

    /// <summary>
    /// Removes songs from a target, e.g. ones whose download failed or was removed.
    /// </summary>
    public void DeletePlaylistEntries(string targetId, IEnumerable<string> songKeys)
    {
        ArgumentNullException.ThrowIfNull(songKeys);

        lock (_writeLock)
        {
            ThrowIfDisposed();
            EnsureConnection();

            using var transaction = _connection!.BeginTransaction();
            foreach (var key in songKeys)
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = "DELETE FROM PlaylistEntries WHERE TargetId = @TargetId AND SongKey = @SongKey";
                cmd.Parameters.AddWithValue("@TargetId", targetId);
                cmd.Parameters.AddWithValue("@SongKey", key);
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    /// <summary>
    /// Notes that a song was just moved into the library, so playlists wait for the scan
    /// instead of treating it as abandoned now that its queue row is gone.
    /// </summary>
    public void MarkPlaylistEntriesImported(string songKey)
    {
        lock (_writeLock)
        {
            ThrowIfDisposed();
            EnsureConnection();

            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "UPDATE PlaylistEntries SET ImportedAt = @Now WHERE SongKey = @SongKey AND AddedAt IS NULL";
            cmd.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("@SongKey", songKey);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Gets all playlist targets.
    /// </summary>
    public List<PlaylistTarget> GetPlaylistTargets()
    {
        return ExecuteRead(
            () =>
            {
                var targets = new List<PlaylistTarget>();
                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = "SELECT Id, UserId, Name, Source, JellyfinPlaylistId, CreatedAt FROM PlaylistTargets ORDER BY CreatedAt DESC";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    targets.Add(ReadPlaylistTarget(reader));
                }

                return targets;
            },
            new List<PlaylistTarget>());
    }

    /// <summary>
    /// Gets the entries of a target, or of all targets when null, in playlist order.
    /// </summary>
    public List<PlaylistEntry> GetPlaylistEntries(string? targetId = null, bool pendingOnly = false)
    {
        return ExecuteRead(
            () =>
            {
                var entries = new List<PlaylistEntry>();
                using var cmd = _connection!.CreateCommand();
                cmd.CommandText = "SELECT TargetId, Position, SongKey, Artist, Title, AddedAt, ImportedAt FROM PlaylistEntries WHERE 1 = 1"
                    + (targetId != null ? " AND TargetId = @TargetId" : string.Empty)
                    + (pendingOnly ? " AND AddedAt IS NULL" : string.Empty)
                    + " ORDER BY TargetId, Position";
                if (targetId != null)
                {
                    cmd.Parameters.AddWithValue("@TargetId", targetId);
                }

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    entries.Add(new PlaylistEntry
                    {
                        TargetId = reader.GetString(0),
                        Position = reader.GetInt32(1),
                        SongKey = reader.GetString(2),
                        Artist = reader.IsDBNull(3) ? null : reader.GetString(3),
                        Title = reader.GetString(4),
                        AddedAt = reader.IsDBNull(5) ? null : reader.GetString(5),
                        ImportedAt = reader.IsDBNull(6) ? null : reader.GetString(6)
                    });
                }

                return entries;
            },
            new List<PlaylistEntry>());
    }

    /// <summary>
    /// Marks songs as present in the target's Jellyfin playlist.
    /// </summary>
    public void MarkPlaylistEntriesAdded(string targetId, IEnumerable<string> songKeys)
    {
        ArgumentNullException.ThrowIfNull(songKeys);

        lock (_writeLock)
        {
            ThrowIfDisposed();
            EnsureConnection();

            var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            using var transaction = _connection!.BeginTransaction();
            foreach (var key in songKeys)
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = "UPDATE PlaylistEntries SET AddedAt = @AddedAt WHERE TargetId = @TargetId AND SongKey = @SongKey";
                cmd.Parameters.AddWithValue("@AddedAt", now);
                cmd.Parameters.AddWithValue("@TargetId", targetId);
                cmd.Parameters.AddWithValue("@SongKey", key);
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    /// <summary>
    /// Marks a song pending again in every target, e.g. after the library file it was added
    /// as was replaced by a better version, so the next sync adds the new one.
    /// </summary>
    public void ResetPlaylistEntriesForSong(string songKey)
    {
        lock (_writeLock)
        {
            ThrowIfDisposed();
            EnsureConnection();

            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "UPDATE PlaylistEntries SET AddedAt = NULL, ImportedAt = @Now WHERE SongKey = @SongKey";
            cmd.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("@SongKey", songKey);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Records the Jellyfin playlist a target fills.
    /// </summary>
    public void SetJellyfinPlaylistId(string targetId, string? playlistId)
    {
        lock (_writeLock)
        {
            ThrowIfDisposed();
            EnsureConnection();

            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "UPDATE PlaylistTargets SET JellyfinPlaylistId = @PlaylistId WHERE Id = @Id";
            cmd.Parameters.AddWithValue("@PlaylistId", (object?)playlistId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Id", targetId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Stops tracking a playlist target. The Jellyfin playlist itself is left alone.
    /// </summary>
    public void DeletePlaylistTarget(string targetId)
    {
        lock (_writeLock)
        {
            ThrowIfDisposed();
            EnsureConnection();

            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "DELETE FROM PlaylistEntries WHERE TargetId = @Id; DELETE FROM PlaylistTargets WHERE Id = @Id";
            cmd.Parameters.AddWithValue("@Id", targetId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Deletes a queue item by ID.
    /// </summary>
    public void DeleteItem(string id)
    {
        lock (_writeLock)
        {
            ThrowIfDisposed();
            EnsureConnection();

            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "DELETE FROM QueueItems WHERE Id = @Id";
            cmd.Parameters.AddWithValue("@Id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Deletes all queue items.
    /// </summary>
    public void ResetQueue()
    {
        lock (_writeLock)
        {
            ThrowIfDisposed();
            EnsureConnection();

            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "DELETE FROM QueueItems";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Gets all items with Downloaded status.
    /// </summary>
    public List<QueueItem> GetDownloadedItems()
    {
        return GetItemsByStatus(QueueStatus.Downloaded);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_writeLock)
        {
            if (!_disposed)
            {
                _connection?.Close();
                _connection?.Dispose();
                _disposed = true;
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(QueueDatabase), "The queue database has been disposed");
        }
    }

    /// <summary>
    /// Executes a read operation with error handling for transient SQLite errors.
    /// </summary>
    private T ExecuteRead<T>(Func<T> readOperation, T fallbackValue, [CallerMemberName] string? callerName = null)
    {
        ThrowIfDisposed();

        try
        {
            lock (_writeLock)
            {
                EnsureConnection();
                return readOperation();
            }
        }
        catch (SqliteException ex) when (
            ex.SqliteErrorCode == 5 ||  // SQLITE_BUSY
            ex.SqliteErrorCode == 6 ||  // SQLITE_LOCKED
            ex.SqliteErrorCode == 8 ||  // SQLITE_READONLY
            ex.SqliteErrorCode == 14)   // SQLITE_CANTOPEN
        {
            _logger.LogWarning(ex, "Database read '{Operation}' failed with SQLite error {ErrorCode}, returning fallback", callerName, ex.SqliteErrorCode);
            return fallbackValue;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 11) // SQLITE_CORRUPT
        {
            _logger.LogError(ex, "Database CORRUPT detected during read '{Operation}'. Database may need to be reset.", callerName);
            return fallbackValue;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("connection", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(ex, "Database connection error during '{Operation}', returning fallback", callerName);
            return fallbackValue;
        }
    }

    private string BuildConnectionString()
    {
        return $"Data Source={_dbPath};Mode=ReadWriteCreate;Pooling=False";
    }

    private void EnsureConnection()
    {
        if (_connection?.State != System.Data.ConnectionState.Open)
        {
            _connection?.Dispose();
            _connection = new SqliteConnection(BuildConnectionString());
            _connection.Open();
            ApplyConnectionPragmas();
        }
    }

    /// <summary>
    /// Applies per-connection pragmas. WAL mode persists in the database file, but
    /// busy_timeout and synchronous are per-connection and must be re-applied on every open.
    /// </summary>
    private void ApplyConnectionPragmas()
    {
        using var pragmaCmd = _connection!.CreateCommand();
        pragmaCmd.CommandText = @"
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;
            PRAGMA synchronous=NORMAL;
        ";
        pragmaCmd.ExecuteNonQuery();
    }

    private void InitializeDatabase()
    {
        try
        {
            _connection = new SqliteConnection(BuildConnectionString());
            _connection.Open();
            ApplyConnectionPragmas();

            var currentVersion = GetSchemaVersion();

            if (currentVersion == 0)
            {
                CreateSchema();
                SetSchemaVersion(SchemaVersion);
            }
            else if (currentVersion < SchemaVersion)
            {
                MigrateSchema(currentVersion);
                SetSchemaVersion(SchemaVersion);
            }

            ResetStaleDownloadingItems();

            _logger.LogDebug("Queue database initialized at {DbPath} (schema v{Version})", _dbPath, SchemaVersion);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize database, attempting recovery");
            try
            {
                RecreateDatabase();
            }
            catch (Exception recreateEx)
            {
                _logger.LogError(recreateEx, "Failed to recreate database");
                throw new InvalidOperationException("Unable to initialize or recover queue database", recreateEx);
            }
        }
    }

    /// <summary>
    /// Resets items stuck in Downloading back to Queued. Downloading is only ever set while a
    /// download is actively running, so any Downloading row at initialization time is a leftover
    /// from a crash or restart mid-download and would otherwise be stuck forever.
    /// </summary>
    private void ResetStaleDownloadingItems()
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "UPDATE QueueItems SET Status = @Queued, UpdatedAt = @UpdatedAt WHERE Status = @Downloading";
        cmd.Parameters.AddWithValue("@Queued", (int)QueueStatus.Queued);
        cmd.Parameters.AddWithValue("@Downloading", (int)QueueStatus.Downloading);
        cmd.Parameters.AddWithValue("@UpdatedAt", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        var reset = cmd.ExecuteNonQuery();
        if (reset > 0)
        {
            _logger.LogInformation("Reset {Count} stale Downloading item(s) back to Queued after restart", reset);
        }
    }

    private void CreateSchema()
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS QueueItems (
                Id TEXT PRIMARY KEY,
                Url TEXT NOT NULL,
                Title TEXT,
                FileName TEXT NOT NULL,
                Status INTEGER NOT NULL DEFAULT 0,
                ErrorMessage TEXT,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                Artist TEXT,
                Album TEXT,
                Year INTEGER,
                UserTitled INTEGER NOT NULL DEFAULT 0,
                Source TEXT,
                SourceTrack TEXT,
                MatchNote TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_queue_status ON QueueItems(Status);
        " + PlaylistSchema;
        cmd.ExecuteNonQuery();
    }

    private void MigrateSchema(int fromVersion)
    {
        if (fromVersion < 2)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = @"
                ALTER TABLE QueueItems ADD COLUMN Artist TEXT;
                ALTER TABLE QueueItems ADD COLUMN Album TEXT;
                ALTER TABLE QueueItems ADD COLUMN Year INTEGER;
            ";
            cmd.ExecuteNonQuery();
            _logger.LogInformation("Migrated queue database to schema v2 (added Artist, Album, Year)");
        }

        if (fromVersion < 3)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "ALTER TABLE QueueItems ADD COLUMN UserTitled INTEGER NOT NULL DEFAULT 0";
            cmd.ExecuteNonQuery();
            _logger.LogInformation("Migrated queue database to schema v3 (added UserTitled)");
        }

        if (fromVersion < 4)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = @"
                ALTER TABLE QueueItems ADD COLUMN Source TEXT;
                ALTER TABLE QueueItems ADD COLUMN SourceTrack TEXT;
                ALTER TABLE QueueItems ADD COLUMN MatchNote TEXT;
            ";
            cmd.ExecuteNonQuery();
            _logger.LogInformation("Migrated queue database to schema v4 (added Source, SourceTrack, MatchNote)");
        }

        if (fromVersion < 5)
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = PlaylistSchema;
            cmd.ExecuteNonQuery();
            _logger.LogInformation("Migrated queue database to schema v5 (added playlist targets)");
        }
    }

    private int GetSchemaVersion()
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        var result = cmd.ExecuteScalar();
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private void SetSchemaVersion(int version)
    {
        using var cmd = _connection!.CreateCommand();
        cmd.CommandText = $"PRAGMA user_version = {version}";
        cmd.ExecuteNonQuery();
    }

    private void RecreateDatabase()
    {
        _connection?.Close();
        _connection?.Dispose();
        _connection = null;

        // Delete database and journal files
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }

        var walPath = _dbPath + "-wal";
        var shmPath = _dbPath + "-shm";
        if (File.Exists(walPath))
        {
            File.Delete(walPath);
        }

        if (File.Exists(shmPath))
        {
            File.Delete(shmPath);
        }

        // Reinitialize
        _connection = new SqliteConnection(BuildConnectionString());
        _connection.Open();
        ApplyConnectionPragmas();

        CreateSchema();
        SetSchemaVersion(SchemaVersion);

        _logger.LogInformation("Queue database recreated successfully");
    }

    private static QueueItem ReadQueueItem(SqliteDataReader reader)
    {
        return new QueueItem
        {
            Id = reader.GetString(0),
            Url = reader.GetString(1),
            Title = reader.IsDBNull(2) ? null : reader.GetString(2),
            FileName = reader.GetString(3),
            Status = (QueueStatus)reader.GetInt32(4),
            ErrorMessage = reader.IsDBNull(5) ? null : reader.GetString(5),
            CreatedAt = reader.GetString(6),
            UpdatedAt = reader.GetString(7),
            Artist = reader.IsDBNull(8) ? null : reader.GetString(8),
            Album = reader.IsDBNull(9) ? null : reader.GetString(9),
            Year = reader.IsDBNull(10) ? null : reader.GetInt32(10),
            UserTitled = !reader.IsDBNull(11) && reader.GetInt32(11) != 0,
            Source = reader.IsDBNull(12) ? null : reader.GetString(12),
            SourceTrack = reader.IsDBNull(13) ? null : DeserializeTrack(reader.GetString(13)),
            MatchNote = reader.IsDBNull(14) ? null : reader.GetString(14)
        };
    }

    private static PlaylistTarget ReadPlaylistTarget(SqliteDataReader reader)
    {
        return new PlaylistTarget
        {
            Id = reader.GetString(0),
            UserId = reader.GetString(1),
            Name = reader.GetString(2),
            Source = reader.IsDBNull(3) ? null : reader.GetString(3),
            JellyfinPlaylistId = reader.IsDBNull(4) ? null : reader.GetString(4),
            CreatedAt = reader.GetString(5)
        };
    }

    private static PlaylistTrack? DeserializeTrack(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<PlaylistTrack>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
