using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

#pragma warning disable CA2100 // SQL commands use only internal constants and safe parameterized queries

namespace Jellyfin.Plugin.ServerSync.Services;

// SyncDatabase
// SQLite database for tracking sync items between servers.
public class SyncDatabase : IDisposable
{
    private const int CurrentSchemaVersion = 5;

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
        try
        {
            _connection = new SqliteConnection($"Data Source={_dbPath}");
            _connection.Open();

            var currentVersion = GetSchemaVersion();

            if (currentVersion == 0)
            {
                CreateInitialSchema();
                SetSchemaVersion(CurrentSchemaVersion);
            }
            else if (currentVersion < CurrentSchemaVersion)
            {
                var migrationSucceeded = MigrateSchema(currentVersion);
                if (!migrationSucceeded)
                {
                    _logger.LogWarning("Migration failed, recreating database with fresh schema");
                    RecreateDatabase();
                    return;
                }
            }

            _logger.LogDebug("Sync database initialized at {DbPath} (schema v{Version})", _dbPath, CurrentSchemaVersion);
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
                throw new InvalidOperationException("Unable to initialize or recover sync database", recreateEx);
            }
        }
    }

    // RecreateDatabase
    // Closes and deletes the current database, then creates a fresh one.
    private void RecreateDatabase()
    {
        _connection?.Dispose();
        _connection = null;

        if (File.Exists(_dbPath))
        {
            try
            {
                File.Delete(_dbPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete corrupted database file, attempting to overwrite");
            }
        }

        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();
        CreateInitialSchema();
        SetSchemaVersion(CurrentSchemaVersion);
        _logger.LogInformation("Database recreated with fresh schema v{Version}", CurrentSchemaVersion);
    }

    // GetSchemaVersion
    // Returns the current database schema version.
    private int GetSchemaVersion()
    {
        using var command = _connection!.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        var result = command.ExecuteScalar();
        return Convert.ToInt32(result);
    }

    // SetSchemaVersion
    // Sets the database schema version.
    private void SetSchemaVersion(int version)
    {
        using var command = _connection!.CreateCommand();
        command.CommandText = $"PRAGMA user_version = {version}";
        command.ExecuteNonQuery();
    }

    // CreateInitialSchema
    // Creates the initial database schema.
    private void CreateInitialSchema()
    {
        using var command = _connection!.CreateCommand();
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
                SourceETag TEXT,
                LocalItemId TEXT,
                LocalPath TEXT,
                StatusDate TEXT NOT NULL,
                Status INTEGER NOT NULL,
                PendingType INTEGER,
                LastSyncTime TEXT,
                ErrorMessage TEXT,
                RetryCount INTEGER DEFAULT 0,
                UNIQUE(SourceItemId)
            );
            CREATE INDEX IF NOT EXISTS idx_source_item ON SyncItems(SourceItemId);
            CREATE INDEX IF NOT EXISTS idx_status ON SyncItems(Status);
            CREATE INDEX IF NOT EXISTS idx_source_library ON SyncItems(SourceLibraryId);
            CREATE INDEX IF NOT EXISTS idx_local_path ON SyncItems(LocalPath);
        ";
        command.ExecuteNonQuery();
    }

    // MigrateSchema
    // Migrates the database schema from an older version.
    // Returns true if migration succeeded, false if it failed and database was reset.
    private bool MigrateSchema(int fromVersion)
    {
        _logger.LogInformation("Migrating database schema from v{From} to v{To}", fromVersion, CurrentSchemaVersion);

        using var transaction = _connection!.BeginTransaction();
        try
        {
            if (fromVersion < 2)
            {
                var alterStatements = new[]
                {
                    "ALTER TABLE SyncItems ADD COLUMN LastSyncTime TEXT",
                    "ALTER TABLE SyncItems ADD COLUMN ErrorMessage TEXT",
                    "ALTER TABLE SyncItems ADD COLUMN RetryCount INTEGER DEFAULT 0"
                };

                foreach (var statement in alterStatements)
                {
                    try
                    {
                        using var cmd = _connection.CreateCommand();
                        cmd.Transaction = transaction;
                        cmd.CommandText = statement;
                        cmd.ExecuteNonQuery();
                    }
                    catch (SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogDebug("Column already exists, skipping");
                    }
                }

                using var idxCmd = _connection.CreateCommand();
                idxCmd.Transaction = transaction;
                idxCmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_local_path ON SyncItems(LocalPath)";
                idxCmd.ExecuteNonQuery();
            }

            if (fromVersion < 3)
            {
                // Add SourceETag column for reliable change detection
                try
                {
                    using var cmd = _connection.CreateCommand();
                    cmd.Transaction = transaction;
                    cmd.CommandText = "ALTER TABLE SyncItems ADD COLUMN SourceETag TEXT";
                    cmd.ExecuteNonQuery();
                    _logger.LogInformation("Added SourceETag column for change detection");
                }
                catch (SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("SourceETag column already exists, skipping");
                }
            }

            if (fromVersion < 4)
            {
                // Add PendingType column and migrate old PendingDeletion/PendingReplacement statuses
                try
                {
                    using var addColCmd = _connection.CreateCommand();
                    addColCmd.Transaction = transaction;
                    addColCmd.CommandText = "ALTER TABLE SyncItems ADD COLUMN PendingType INTEGER";
                    addColCmd.ExecuteNonQuery();
                    _logger.LogInformation("Added PendingType column");
                }
                catch (SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("PendingType column already exists, skipping");
                }

                // Migrate old statuses: PendingDeletion (5) -> Pending (0) with PendingType.Deletion (2)
                using var migrateDeletionCmd = _connection.CreateCommand();
                migrateDeletionCmd.Transaction = transaction;
                migrateDeletionCmd.CommandText = "UPDATE SyncItems SET Status = 0, PendingType = 2 WHERE Status = 5";
                var deletionCount = migrateDeletionCmd.ExecuteNonQuery();
                if (deletionCount > 0)
                {
                    _logger.LogInformation("Migrated {Count} PendingDeletion items to Pending with PendingType.Deletion", deletionCount);
                }

                // Migrate old statuses: PendingReplacement (6) -> Pending (0) with PendingType.Replacement (1)
                using var migrateReplacementCmd = _connection.CreateCommand();
                migrateReplacementCmd.Transaction = transaction;
                migrateReplacementCmd.CommandText = "UPDATE SyncItems SET Status = 0, PendingType = 1 WHERE Status = 6";
                var replacementCount = migrateReplacementCmd.ExecuteNonQuery();
                if (replacementCount > 0)
                {
                    _logger.LogInformation("Migrated {Count} PendingReplacement items to Pending with PendingType.Replacement", replacementCount);
                }

                // Set PendingType.Download (0) for existing Pending items without a type
                using var migrateDownloadCmd = _connection.CreateCommand();
                migrateDownloadCmd.Transaction = transaction;
                migrateDownloadCmd.CommandText = "UPDATE SyncItems SET PendingType = 0 WHERE Status = 0 AND PendingType IS NULL";
                migrateDownloadCmd.ExecuteNonQuery();
            }

            if (fromVersion < 5)
            {
                // No schema changes, but status value 5 is now Deleting instead of old PendingDeletion
                // Old PendingDeletion items were already migrated to Pending+PendingType.Deletion in v4
                _logger.LogInformation("Schema v5: Deleting status (5) is now available");
            }

            SetSchemaVersion(CurrentSchemaVersion);
            transaction.Commit();
            _logger.LogInformation("Database migration completed successfully");
            return true;
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            _logger.LogError(ex, "Database migration failed, rolled back changes");
            return false;
        }
    }

    // BeginTransaction
    // Starts a new database transaction.
    public SqliteTransaction BeginTransaction()
    {
        EnsureConnection();
        return _connection!.BeginTransaction();
    }

    // CheckPathCollision
    // Returns true if another item already uses this local path.
    public bool CheckPathCollision(string localPath, string? excludeSourceItemId = null)
    {
        EnsureConnection();

        using var command = _connection!.CreateCommand();
        if (excludeSourceItemId != null)
        {
            command.CommandText = "SELECT COUNT(*) FROM SyncItems WHERE LocalPath = @localPath AND SourceItemId != @excludeId";
            command.Parameters.AddWithValue("@excludeId", excludeSourceItemId);
        }
        else
        {
            command.CommandText = "SELECT COUNT(*) FROM SyncItems WHERE LocalPath = @localPath";
        }

        command.Parameters.AddWithValue("@localPath", localPath);
        var count = Convert.ToInt64(command.ExecuteScalar());
        return count > 0;
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

    // GetByLocalPath
    // Retrieves a sync item by its local path.
    public SyncItem? GetByLocalPath(string localPath)
    {
        EnsureConnection();

        using var command = _connection!.CreateCommand();
        command.CommandText = "SELECT * FROM SyncItems WHERE LocalPath = @localPath";
        command.Parameters.AddWithValue("@localPath", localPath);

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

    // GetPendingByType
    // Retrieves all pending items with a specific pending type.
    public List<SyncItem> GetPendingByType(PendingType pendingType)
    {
        EnsureConnection();

        var items = new List<SyncItem>();
        using var command = _connection!.CreateCommand();
        command.CommandText = "SELECT * FROM SyncItems WHERE Status = @status AND PendingType = @pendingType";
        command.Parameters.AddWithValue("@status", (int)SyncStatus.Pending);
        command.Parameters.AddWithValue("@pendingType", (int)pendingType);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(ReadSyncItem(reader));
        }

        return items;
    }

    // GetPendingCounts
    // Returns counts of pending items grouped by pending type.
    public Dictionary<PendingType, int> GetPendingCounts()
    {
        EnsureConnection();

        var counts = new Dictionary<PendingType, int>();
        using var command = _connection!.CreateCommand();
        command.CommandText = "SELECT PendingType, COUNT(*) as Count FROM SyncItems WHERE Status = @status AND PendingType IS NOT NULL GROUP BY PendingType";
        command.Parameters.AddWithValue("@status", (int)SyncStatus.Pending);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var pendingType = (PendingType)reader.GetInt32(0);
            var count = reader.GetInt32(1);
            counts[pendingType] = count;
        }

        return counts;
    }

    // GetErroredItemsForRetry
    // Gets errored items that haven't exceeded max retries.
    public List<SyncItem> GetErroredItemsForRetry(int maxRetries)
    {
        EnsureConnection();

        var items = new List<SyncItem>();
        using var command = _connection!.CreateCommand();
        command.CommandText = "SELECT * FROM SyncItems WHERE Status = @status AND (RetryCount IS NULL OR RetryCount < @maxRetries)";
        command.Parameters.AddWithValue("@status", (int)SyncStatus.Errored);
        command.Parameters.AddWithValue("@maxRetries", maxRetries);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(ReadSyncItem(reader));
        }

        return items;
    }

    // Upsert
    // Inserts or updates a sync item in the database.
    public void Upsert(SyncItem item, SqliteTransaction? transaction = null)
    {
        EnsureConnection();

        using var command = _connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
            INSERT INTO SyncItems (
                SourceLibraryId, LocalLibraryId, SourceItemId, SourcePath, SourceSize,
                SourceCreateDate, SourceModifyDate, SourceETag, LocalItemId, LocalPath, StatusDate, Status,
                PendingType, LastSyncTime, ErrorMessage, RetryCount
            ) VALUES (
                @sourceLibraryId, @localLibraryId, @sourceItemId, @sourcePath, @sourceSize,
                @sourceCreateDate, @sourceModifyDate, @sourceETag, @localItemId, @localPath, @statusDate, @status,
                @pendingType, @lastSyncTime, @errorMessage, @retryCount
            )
            ON CONFLICT(SourceItemId) DO UPDATE SET
                SourceLibraryId = @sourceLibraryId,
                LocalLibraryId = @localLibraryId,
                SourcePath = @sourcePath,
                SourceSize = @sourceSize,
                SourceCreateDate = @sourceCreateDate,
                SourceModifyDate = @sourceModifyDate,
                SourceETag = @sourceETag,
                LocalItemId = @localItemId,
                LocalPath = @localPath,
                StatusDate = @statusDate,
                Status = @status,
                PendingType = @pendingType,
                LastSyncTime = @lastSyncTime,
                ErrorMessage = @errorMessage,
                RetryCount = @retryCount
        ";

        command.Parameters.AddWithValue("@sourceLibraryId", item.SourceLibraryId);
        command.Parameters.AddWithValue("@localLibraryId", item.LocalLibraryId);
        command.Parameters.AddWithValue("@sourceItemId", item.SourceItemId);
        command.Parameters.AddWithValue("@sourcePath", item.SourcePath);
        command.Parameters.AddWithValue("@sourceSize", item.SourceSize);
        command.Parameters.AddWithValue("@sourceCreateDate", item.SourceCreateDate.ToString("o"));
        command.Parameters.AddWithValue("@sourceModifyDate", item.SourceModifyDate.ToString("o"));
        command.Parameters.AddWithValue("@sourceETag", item.SourceETag ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@localItemId", item.LocalItemId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@localPath", item.LocalPath ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@statusDate", item.StatusDate.ToString("o"));
        command.Parameters.AddWithValue("@status", (int)item.Status);
        command.Parameters.AddWithValue("@pendingType", item.PendingType.HasValue ? (int)item.PendingType.Value : DBNull.Value);
        command.Parameters.AddWithValue("@lastSyncTime", item.LastSyncTime?.ToString("o") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@errorMessage", item.ErrorMessage ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@retryCount", item.RetryCount);

        command.ExecuteNonQuery();
    }

    // UpdateStatus
    // Updates the status of a sync item with optional fields.
    public void UpdateStatus(
        string sourceItemId,
        SyncStatus status,
        PendingType? pendingType = null,
        string? localItemId = null,
        string? localPath = null,
        string? errorMessage = null,
        string? sourceETag = null,
        long? sourceSize = null)
    {
        EnsureConnection();

        using var command = _connection!.CreateCommand();

        var setClauses = new List<string>
        {
            "Status = @status",
            "StatusDate = @statusDate"
        };

        // Handle PendingType: set it when Pending, clear it otherwise
        if (status == SyncStatus.Pending)
        {
            setClauses.Add("PendingType = @pendingType");
        }
        else
        {
            setClauses.Add("PendingType = NULL");
        }

        if (localItemId != null)
        {
            setClauses.Add("LocalItemId = @localItemId");
        }

        if (localPath != null)
        {
            setClauses.Add("LocalPath = @localPath");
        }

        if (sourceETag != null)
        {
            setClauses.Add("SourceETag = @sourceETag");
        }

        if (sourceSize.HasValue)
        {
            setClauses.Add("SourceSize = @sourceSize");
        }

        if (status == SyncStatus.Synced)
        {
            setClauses.Add("LastSyncTime = @statusDate");
            setClauses.Add("ErrorMessage = NULL");
            setClauses.Add("RetryCount = 0");
        }
        else if (status == SyncStatus.Errored)
        {
            setClauses.Add("ErrorMessage = @errorMessage");
            setClauses.Add("RetryCount = COALESCE(RetryCount, 0) + 1");
        }
        else if (status == SyncStatus.Queued || status == SyncStatus.Deleting)
        {
            setClauses.Add("ErrorMessage = NULL");
        }

        command.CommandText = $"UPDATE SyncItems SET {string.Join(", ", setClauses)} WHERE SourceItemId = @sourceItemId";

        command.Parameters.AddWithValue("@sourceItemId", sourceItemId);
        command.Parameters.AddWithValue("@status", (int)status);
        command.Parameters.AddWithValue("@statusDate", DateTime.UtcNow.ToString("o"));
        command.Parameters.AddWithValue("@pendingType", pendingType.HasValue ? (int)pendingType.Value : DBNull.Value);

        if (localItemId != null)
        {
            command.Parameters.AddWithValue("@localItemId", localItemId);
        }

        if (localPath != null)
        {
            command.Parameters.AddWithValue("@localPath", localPath);
        }

        if (sourceETag != null)
        {
            command.Parameters.AddWithValue("@sourceETag", sourceETag);
        }

        if (sourceSize.HasValue)
        {
            command.Parameters.AddWithValue("@sourceSize", sourceSize.Value);
        }

        command.Parameters.AddWithValue("@errorMessage", errorMessage ?? (object)DBNull.Value);

        command.ExecuteNonQuery();
    }

    // Delete
    // Deletes a sync item by its source item ID.
    public void Delete(string sourceItemId, SqliteTransaction? transaction = null)
    {
        EnsureConnection();

        using var command = _connection!.CreateCommand();
        command.Transaction = transaction;
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

    // GetSyncStats
    // Returns detailed sync statistics.
    public SyncStats GetSyncStats()
    {
        EnsureConnection();

        var stats = new SyncStats();

        using var countCmd = _connection!.CreateCommand();
        countCmd.CommandText = "SELECT Status, COUNT(*) FROM SyncItems GROUP BY Status";
        using var reader = countCmd.ExecuteReader();
        while (reader.Read())
        {
            var status = (SyncStatus)reader.GetInt32(0);
            var count = reader.GetInt32(1);
            stats.StatusCounts[status] = count;
        }

        using var queuedSizeCmd = _connection.CreateCommand();
        queuedSizeCmd.CommandText = "SELECT COALESCE(SUM(SourceSize), 0) FROM SyncItems WHERE Status = @status";
        queuedSizeCmd.Parameters.AddWithValue("@status", (int)SyncStatus.Queued);
        stats.TotalQueuedBytes = Convert.ToInt64(queuedSizeCmd.ExecuteScalar());

        using var syncedSizeCmd = _connection.CreateCommand();
        syncedSizeCmd.CommandText = "SELECT COALESCE(SUM(SourceSize), 0) FROM SyncItems WHERE Status = @status";
        syncedSizeCmd.Parameters.AddWithValue("@status", (int)SyncStatus.Synced);
        stats.TotalSyncedBytes = Convert.ToInt64(syncedSizeCmd.ExecuteScalar());

        using var lastSyncCmd = _connection.CreateCommand();
        lastSyncCmd.CommandText = "SELECT MAX(LastSyncTime) FROM SyncItems WHERE LastSyncTime IS NOT NULL";
        var lastSync = lastSyncCmd.ExecuteScalar();
        if (lastSync != null && lastSync != DBNull.Value)
        {
            stats.LastSyncTime = DateTime.Parse((string)lastSync);
        }

        return stats;
    }

    // ClearStaleErrors
    // Resets error status for items older than specified days.
    public int ClearStaleErrors(int olderThanDays)
    {
        EnsureConnection();

        var cutoffDate = DateTime.UtcNow.AddDays(-olderThanDays).ToString("o");

        using var command = _connection!.CreateCommand();
        command.CommandText = @"
            UPDATE SyncItems
            SET Status = @newStatus, RetryCount = 0, ErrorMessage = NULL
            WHERE Status = @errorStatus AND StatusDate < @cutoffDate
        ";
        command.Parameters.AddWithValue("@newStatus", (int)SyncStatus.Queued);
        command.Parameters.AddWithValue("@errorStatus", (int)SyncStatus.Errored);
        command.Parameters.AddWithValue("@cutoffDate", cutoffDate);

        return command.ExecuteNonQuery();
    }

    // ReadSyncItem
    // Reads a SyncItem from the database reader.
    private static SyncItem ReadSyncItem(SqliteDataReader reader)
    {
        var item = new SyncItem
        {
            Id = reader.GetInt64(reader.GetOrdinal("Id")),
            SourceLibraryId = reader.GetString(reader.GetOrdinal("SourceLibraryId")),
            LocalLibraryId = reader.GetString(reader.GetOrdinal("LocalLibraryId")),
            SourceItemId = reader.GetString(reader.GetOrdinal("SourceItemId")),
            SourcePath = reader.GetString(reader.GetOrdinal("SourcePath")),
            SourceSize = reader.GetInt64(reader.GetOrdinal("SourceSize")),
            SourceCreateDate = ParseDateTimeSafe(reader.GetString(reader.GetOrdinal("SourceCreateDate"))),
            SourceModifyDate = ParseDateTimeSafe(reader.GetString(reader.GetOrdinal("SourceModifyDate"))),
            LocalItemId = reader.IsDBNull(reader.GetOrdinal("LocalItemId")) ? null : reader.GetString(reader.GetOrdinal("LocalItemId")),
            LocalPath = reader.IsDBNull(reader.GetOrdinal("LocalPath")) ? null : reader.GetString(reader.GetOrdinal("LocalPath")),
            StatusDate = ParseDateTimeSafe(reader.GetString(reader.GetOrdinal("StatusDate"))),
            Status = (SyncStatus)reader.GetInt32(reader.GetOrdinal("Status"))
        };

        try
        {
            var pendingTypeOrdinal = reader.GetOrdinal("PendingType");
            if (!reader.IsDBNull(pendingTypeOrdinal))
            {
                item.PendingType = (PendingType)reader.GetInt32(pendingTypeOrdinal);
            }

            var etagOrdinal = reader.GetOrdinal("SourceETag");
            if (!reader.IsDBNull(etagOrdinal))
            {
                item.SourceETag = reader.GetString(etagOrdinal);
            }

            var lastSyncOrdinal = reader.GetOrdinal("LastSyncTime");
            if (!reader.IsDBNull(lastSyncOrdinal))
            {
                item.LastSyncTime = ParseDateTimeSafe(reader.GetString(lastSyncOrdinal));
            }

            var errorOrdinal = reader.GetOrdinal("ErrorMessage");
            if (!reader.IsDBNull(errorOrdinal))
            {
                item.ErrorMessage = reader.GetString(errorOrdinal);
            }

            var retryOrdinal = reader.GetOrdinal("RetryCount");
            if (!reader.IsDBNull(retryOrdinal))
            {
                item.RetryCount = reader.GetInt32(retryOrdinal);
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            // Column doesn't exist yet (pre-migration)
        }

        return item;
    }

    // ParseDateTimeSafe
    // Parses a datetime string safely, returning DateTime.MinValue on failure.
    private static DateTime ParseDateTimeSafe(string? dateString)
    {
        if (string.IsNullOrEmpty(dateString))
        {
            return DateTime.MinValue;
        }

        if (DateTime.TryParse(dateString, out var result))
        {
            return result;
        }

        return DateTime.MinValue;
    }

    // EnsureConnection
    // Ensures the database connection is open, reopening if necessary.
    private void EnsureConnection()
    {
        if (_connection != null && _connection.State == ConnectionState.Open)
        {
            return;
        }

        try
        {
            _connection?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disposing old database connection");
        }

        try
        {
            _connection = new SqliteConnection($"Data Source={_dbPath}");
            _connection.Open();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open database connection to {DbPath}", _dbPath);
            throw new InvalidOperationException($"Unable to open database connection: {ex.Message}", ex);
        }
    }

    // ResetDatabase
    // Drops all data and recreates the database with the latest schema.
    public void ResetDatabase()
    {
        _logger.LogWarning("Resetting sync database - all tracking data will be lost");

        _connection?.Dispose();
        _connection = null;

        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }

        InitializeDatabase();
        _logger.LogInformation("Sync database has been reset with fresh schema v{Version}", CurrentSchemaVersion);
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

// SyncStats
// Statistics about the sync database.
public class SyncStats
{
    public Dictionary<SyncStatus, int> StatusCounts { get; set; } = new();

    public long TotalQueuedBytes { get; set; }

    public long TotalSyncedBytes { get; set; }

    public DateTime? LastSyncTime { get; set; }
}
