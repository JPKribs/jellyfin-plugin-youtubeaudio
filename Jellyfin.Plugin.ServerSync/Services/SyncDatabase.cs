using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using Jellyfin.Plugin.ServerSync.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services;

// SyncDatabase
// SQLite database for tracking sync items between servers.
public class SyncDatabase : IDisposable
{
    private readonly ILogger<SyncDatabase> _logger;
    private readonly string _dbPath;
    private SqliteConnection? _connection;
    private bool _disposed;

    public SyncDatabase(ILogger<SyncDatabase> logger, string dataPath)
    {
        _logger = logger;
        var dbDir = Path.Combine(dataPath, "serversync");
        Directory.CreateDirectory(dbDir);
        _dbPath = Path.Combine(dbDir, "sync.db");
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();

        using var command = _connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS SyncItems (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SourceLibraryId TEXT NOT NULL,
                LocalLibraryId TEXT NOT NULL,
                SourceItemId TEXT NOT NULL,
                SourcePath TEXT NOT NULL,
                SourceSize INTEGER NOT NULL,
                SourceCreateDate TEXT NOT NULL,
                SourceModifyDate TEXT NOT NULL,
                LocalItemId TEXT,
                LocalPath TEXT,
                StatusDate TEXT NOT NULL,
                Status INTEGER NOT NULL,
                UNIQUE(SourceItemId)
            );
            CREATE INDEX IF NOT EXISTS idx_source_item ON SyncItems(SourceItemId);
            CREATE INDEX IF NOT EXISTS idx_status ON SyncItems(Status);
            CREATE INDEX IF NOT EXISTS idx_source_library ON SyncItems(SourceLibraryId);
        ";
        command.ExecuteNonQuery();

        _logger.LogDebug("Sync database initialized at {DbPath}", _dbPath);
    }

    // GetBySourceItemId
    // Retrieves a sync item by its source item ID.
    public SyncItem? GetBySourceItemId(string sourceItemId)
    {
        EnsureConnection();

        using var command = _connection!.CreateCommand();
        command.CommandText = "SELECT * FROM SyncItems WHERE SourceItemId = @sourceItemId";
        command.Parameters.AddWithValue("@sourceItemId", sourceItemId);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSyncItem(reader) : null;
    }

    // GetByStatus
    // Retrieves all sync items with a specific status.
    public List<SyncItem> GetByStatus(SyncStatus status)
    {
        EnsureConnection();

        var items = new List<SyncItem>();
        using var command = _connection!.CreateCommand();
        command.CommandText = "SELECT * FROM SyncItems WHERE Status = @status";
        command.Parameters.AddWithValue("@status", (int)status);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(ReadSyncItem(reader));
        }

        return items;
    }

    // GetBySourceLibrary
    // Retrieves all sync items for a specific source library.
    public List<SyncItem> GetBySourceLibrary(string sourceLibraryId)
    {
        EnsureConnection();

        var items = new List<SyncItem>();
        using var command = _connection!.CreateCommand();
        command.CommandText = "SELECT * FROM SyncItems WHERE SourceLibraryId = @sourceLibraryId";
        command.Parameters.AddWithValue("@sourceLibraryId", sourceLibraryId);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(ReadSyncItem(reader));
        }

        return items;
    }

    // GetAll
    // Retrieves all sync items from the database.
    public List<SyncItem> GetAll()
    {
        EnsureConnection();

        var items = new List<SyncItem>();
        using var command = _connection!.CreateCommand();
        command.CommandText = "SELECT * FROM SyncItems";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(ReadSyncItem(reader));
        }

        return items;
    }

    // Upsert
    // Inserts or updates a sync item in the database.
    public void Upsert(SyncItem item)
    {
        EnsureConnection();

        using var command = _connection!.CreateCommand();
        command.CommandText = @"
            INSERT INTO SyncItems (
                SourceLibraryId, LocalLibraryId, SourceItemId, SourcePath, SourceSize,
                SourceCreateDate, SourceModifyDate, LocalItemId, LocalPath, StatusDate, Status
            ) VALUES (
                @sourceLibraryId, @localLibraryId, @sourceItemId, @sourcePath, @sourceSize,
                @sourceCreateDate, @sourceModifyDate, @localItemId, @localPath, @statusDate, @status
            )
            ON CONFLICT(SourceItemId) DO UPDATE SET
                SourceLibraryId = @sourceLibraryId,
                LocalLibraryId = @localLibraryId,
                SourcePath = @sourcePath,
                SourceSize = @sourceSize,
                SourceCreateDate = @sourceCreateDate,
                SourceModifyDate = @sourceModifyDate,
                LocalItemId = @localItemId,
                LocalPath = @localPath,
                StatusDate = @statusDate,
                Status = @status
        ";

        command.Parameters.AddWithValue("@sourceLibraryId", item.SourceLibraryId);
        command.Parameters.AddWithValue("@localLibraryId", item.LocalLibraryId);
        command.Parameters.AddWithValue("@sourceItemId", item.SourceItemId);
        command.Parameters.AddWithValue("@sourcePath", item.SourcePath);
        command.Parameters.AddWithValue("@sourceSize", item.SourceSize);
        command.Parameters.AddWithValue("@sourceCreateDate", item.SourceCreateDate.ToString("o"));
        command.Parameters.AddWithValue("@sourceModifyDate", item.SourceModifyDate.ToString("o"));
        command.Parameters.AddWithValue("@localItemId", item.LocalItemId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@localPath", item.LocalPath ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@statusDate", item.StatusDate.ToString("o"));
        command.Parameters.AddWithValue("@status", (int)item.Status);

        command.ExecuteNonQuery();
    }

    // UpdateStatus
    // Updates the status of a sync item.
    public void UpdateStatus(string sourceItemId, SyncStatus status, string? localItemId = null, string? localPath = null)
    {
        EnsureConnection();

        using var command = _connection!.CreateCommand();

        if (localItemId != null || localPath != null)
        {
            command.CommandText = @"
                UPDATE SyncItems SET
                    Status = @status,
                    StatusDate = @statusDate,
                    LocalItemId = COALESCE(@localItemId, LocalItemId),
                    LocalPath = COALESCE(@localPath, LocalPath)
                WHERE SourceItemId = @sourceItemId
            ";
            command.Parameters.AddWithValue("@localItemId", localItemId ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@localPath", localPath ?? (object)DBNull.Value);
        }
        else
        {
            command.CommandText = @"
                UPDATE SyncItems SET
                    Status = @status,
                    StatusDate = @statusDate
                WHERE SourceItemId = @sourceItemId
            ";
        }

        command.Parameters.AddWithValue("@sourceItemId", sourceItemId);
        command.Parameters.AddWithValue("@status", (int)status);
        command.Parameters.AddWithValue("@statusDate", DateTime.UtcNow.ToString("o"));

        command.ExecuteNonQuery();
    }

    // Delete
    // Deletes a sync item by its source item ID.
    public void Delete(string sourceItemId)
    {
        EnsureConnection();

        using var command = _connection!.CreateCommand();
        command.CommandText = "DELETE FROM SyncItems WHERE SourceItemId = @sourceItemId";
        command.Parameters.AddWithValue("@sourceItemId", sourceItemId);
        command.ExecuteNonQuery();
    }

    // GetStatusCounts
    // Returns counts of items grouped by status.
    public Dictionary<SyncStatus, int> GetStatusCounts()
    {
        EnsureConnection();

        var counts = new Dictionary<SyncStatus, int>();
        using var command = _connection!.CreateCommand();
        command.CommandText = "SELECT Status, COUNT(*) as Count FROM SyncItems GROUP BY Status";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var status = (SyncStatus)reader.GetInt32(0);
            var count = reader.GetInt32(1);
            counts[status] = count;
        }

        return counts;
    }

    private static SyncItem ReadSyncItem(SqliteDataReader reader)
    {
        return new SyncItem
        {
            Id = reader.GetInt64(reader.GetOrdinal("Id")),
            SourceLibraryId = reader.GetString(reader.GetOrdinal("SourceLibraryId")),
            LocalLibraryId = reader.GetString(reader.GetOrdinal("LocalLibraryId")),
            SourceItemId = reader.GetString(reader.GetOrdinal("SourceItemId")),
            SourcePath = reader.GetString(reader.GetOrdinal("SourcePath")),
            SourceSize = reader.GetInt64(reader.GetOrdinal("SourceSize")),
            SourceCreateDate = DateTime.Parse(reader.GetString(reader.GetOrdinal("SourceCreateDate"))),
            SourceModifyDate = DateTime.Parse(reader.GetString(reader.GetOrdinal("SourceModifyDate"))),
            LocalItemId = reader.IsDBNull(reader.GetOrdinal("LocalItemId")) ? null : reader.GetString(reader.GetOrdinal("LocalItemId")),
            LocalPath = reader.IsDBNull(reader.GetOrdinal("LocalPath")) ? null : reader.GetString(reader.GetOrdinal("LocalPath")),
            StatusDate = DateTime.Parse(reader.GetString(reader.GetOrdinal("StatusDate"))),
            Status = (SyncStatus)reader.GetInt32(reader.GetOrdinal("Status"))
        };
    }

    private void EnsureConnection()
    {
        if (_connection == null || _connection.State != ConnectionState.Open)
        {
            _connection?.Dispose();
            _connection = new SqliteConnection($"Data Source={_dbPath}");
            _connection.Open();
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _connection?.Dispose();
            _disposed = true;
        }
    }
}
