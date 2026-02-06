using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;
using Jellyfin.Plugin.ServerSync.Models.MetadataSync;
using Jellyfin.Plugin.ServerSync.Models.UserSync;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

#pragma warning disable CA2100 // SQL commands use only internal constants and safe parameterized queries

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// SyncDatabase
/// SQLite database for tracking sync items between servers.
/// </summary>
public class SyncDatabase : IDisposable
{
    private readonly ILogger<SyncDatabase> _logger;
    private readonly string _dbPath;
    private readonly object _writeLock = new();
    private SqliteConnection? _connection;
    private bool _disposed;

    public SyncDatabase(ILogger<SyncDatabase> logger, string dataPath)
    {
        _logger = logger;
        var dbDir = Path.Combine(dataPath, "serversync");
        Directory.CreateDirectory(dbDir);
        _dbPath = Path.Combine(dbDir, "sync.db");

        _logger.LogInformation("Sync database path: {DbPath}", _dbPath);
        _logger.LogInformation("Database directory exists: {Exists}, writable: {Writable}",
            Directory.Exists(dbDir),
            IsDirectoryWritable(dbDir));

        InitializeDatabase();
    }

    /// <summary>
    /// Throws ObjectDisposedException if the database has been disposed.
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SyncDatabase), "The sync database has been disposed");
        }
    }

    /// <summary>
    /// Executes a read operation with error handling for transient SQLite errors.
    /// Returns the fallback value if the database is temporarily unavailable.
    /// </summary>
    /// <typeparam name="T">Return type of the read operation.</typeparam>
    /// <param name="readOperation">The read operation to execute.</param>
    /// <param name="fallbackValue">Value to return if the database is unavailable.</param>
    /// <param name="callerName">Auto-populated caller method name for logging.</param>
    /// <returns>Result of the read operation, or fallback value on transient error.</returns>
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
            ex.SqliteErrorCode == 11 || // SQLITE_CORRUPT
            ex.SqliteErrorCode == 14)   // SQLITE_CANTOPEN
        {
            _logger.LogWarning(ex, "Database read '{Operation}' failed with SQLite error {ErrorCode}, returning fallback", callerName, ex.SqliteErrorCode);
            return fallbackValue;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("connection", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(ex, "Database connection error during '{Operation}', returning fallback", callerName);
            return fallbackValue;
        }
    }

    /// <summary>
    /// Checks if a directory is writable by attempting to create a temp file.
    /// </summary>
    private static bool IsDirectoryWritable(string dirPath)
    {
        try
        {
            var testPath = Path.Combine(dirPath, ".write_test_" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(testPath, "test");
            File.Delete(testPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Builds the SQLite connection string with hardened settings.
    /// </summary>
    private string BuildConnectionString()
    {
        // Use a connection string with settings for better reliability:
        // - Mode=ReadWriteCreate: Create the file if it doesn't exist
        // - Pooling=False: Disable connection pooling to avoid stale cached connections
        //   causing SQLITE_READONLY errors after server restarts or crashes
        return $"Data Source={_dbPath};Mode=ReadWriteCreate;Pooling=False";
    }

    /// <summary>
    /// Deletes a file with retry logic for locked files.
    /// </summary>
    private void DeleteFileWithRetry(string filePath, int maxRetries = 3)
    {
        for (var i = 0; i < maxRetries; i++)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }

                return;
            }
            catch (IOException ex) when (i < maxRetries - 1)
            {
                _logger.LogDebug(ex, "Failed to delete {FilePath}, retrying ({Attempt}/{Max})", filePath, i + 1, maxRetries);
                System.Threading.Thread.Sleep(50 * (i + 1)); // Brief backoff (may block while _writeLock is held)
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete file {FilePath}", filePath);
                throw;
            }
        }
    }

    /// <summary>
    /// Deletes WAL and SHM journal files associated with the database.
    /// </summary>
    private void DeleteWalFiles()
    {
        var walPath = _dbPath + "-wal";
        var shmPath = _dbPath + "-shm";

        try
        {
            if (File.Exists(walPath))
            {
                File.Delete(walPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to delete WAL file at {Path}", walPath);
        }

        try
        {
            if (File.Exists(shmPath))
            {
                File.Delete(shmPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to delete SHM file at {Path}", shmPath);
        }
    }

    private void InitializeDatabase()
    {
        try
        {
            _connection = new SqliteConnection(BuildConnectionString());
            _connection.Open();

            // Set pragmas for reliability in multi-threaded environments
            using (var pragmaCmd = _connection.CreateCommand())
            {
                pragmaCmd.CommandText = @"
                    PRAGMA journal_mode=WAL;
                    PRAGMA busy_timeout=5000;
                    PRAGMA synchronous=NORMAL;
                ";
                pragmaCmd.ExecuteNonQuery();
            }

            var currentVersion = DatabaseMigrationService.GetSchemaVersion(_connection);

            if (currentVersion == 0)
            {
                DatabaseMigrationService.CreateInitialSchema(_connection);
                DatabaseMigrationService.SetSchemaVersion(_connection, DatabaseMigrationService.CurrentSchemaVersion);
            }
            else if (currentVersion < DatabaseMigrationService.CurrentSchemaVersion)
            {
                var migrationSucceeded = DatabaseMigrationService.MigrateSchema(_connection, currentVersion, _logger);
                if (!migrationSucceeded)
                {
                    _logger.LogWarning("Migration failed, recreating database with fresh schema");
                    RecreateDatabase();
                    return;
                }
            }

            _logger.LogDebug("Sync database initialized at {DbPath} (schema v{Version})", _dbPath, DatabaseMigrationService.CurrentSchemaVersion);
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

    /// <summary>
    /// RecreateDatabase
    /// Closes and deletes the current database, then creates a fresh one.
    /// </summary>
    private void RecreateDatabase()
    {
        _connection?.Close();
        _connection?.Dispose();
        _connection = null;

        // Clear SQLite connection pool to release file handles
        SqliteConnection.ClearAllPools();

        // Delete main database file
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

        // Also delete WAL and SHM files if they exist
        DeleteWalFiles();

        _connection = new SqliteConnection(BuildConnectionString());
        _connection.Open();

        // Set pragmas on fresh connection
        using (var pragmaCmd = _connection.CreateCommand())
        {
            pragmaCmd.CommandText = @"
                PRAGMA journal_mode=WAL;
                PRAGMA busy_timeout=5000;
                PRAGMA synchronous=NORMAL;
            ";
            pragmaCmd.ExecuteNonQuery();
        }

        DatabaseMigrationService.CreateInitialSchema(_connection);
        DatabaseMigrationService.SetSchemaVersion(_connection, DatabaseMigrationService.CurrentSchemaVersion);
        _logger.LogInformation("Database recreated with fresh schema v{Version}", DatabaseMigrationService.CurrentSchemaVersion);
    }

    /// <summary>
    /// BeginTransaction
    /// Starts a new database transaction.
    /// </summary>
    /// <returns>A new SQLite transaction.</returns>
    public SqliteTransaction BeginTransaction()
    {
        ThrowIfDisposed();
        EnsureConnection();
        return _connection!.BeginTransaction();
    }

    /// <summary>
    /// CheckPathCollision
    /// Returns true if another item already uses this local path.
    /// </summary>
    /// <param name="localPath">Path to check.</param>
    /// <param name="excludeSourceItemId">Optional item ID to exclude from check.</param>
    /// <returns>True if collision exists.</returns>
    public bool CheckPathCollision(string localPath, string? excludeSourceItemId = null)
    {
        return ExecuteRead(() =>
        {
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
        }, fallbackValue: false);
    }

    /// <summary>
    /// GetBySourceItemId
    /// Retrieves a sync item by its source item ID.
    /// </summary>
    /// <param name="sourceItemId">Source item ID.</param>
    /// <returns>Sync item or null if not found.</returns>
    public SyncItem? GetBySourceItemId(string sourceItemId)
    {
        return ExecuteRead(() =>
        {
            using var command = _connection!.CreateCommand();
            command.CommandText = "SELECT * FROM SyncItems WHERE SourceItemId = @sourceItemId";
            command.Parameters.AddWithValue("@sourceItemId", sourceItemId);

            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadSyncItem(reader) : null;
        }, fallbackValue: null);
    }

    /// <summary>
    /// GetByLocalPath
    /// Retrieves a sync item by its local path.
    /// </summary>
    /// <param name="localPath">Local file path.</param>
    /// <returns>Sync item or null if not found.</returns>
    public SyncItem? GetByLocalPath(string localPath)
    {
        return ExecuteRead(() =>
        {
            using var command = _connection!.CreateCommand();
            command.CommandText = "SELECT * FROM SyncItems WHERE LocalPath = @localPath";
            command.Parameters.AddWithValue("@localPath", localPath);

            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadSyncItem(reader) : null;
        }, fallbackValue: null);
    }

    /// <summary>
    /// GetByStatus
    /// Retrieves all sync items with a specific status.
    /// </summary>
    /// <param name="status">Status to filter by.</param>
    /// <returns>List of matching sync items.</returns>
    public List<SyncItem> GetByStatus(SyncStatus status)
    {
        return ExecuteRead(() =>
        {
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
        }, fallbackValue: new List<SyncItem>());
    }

    /// <summary>
    /// GetBySourceLibrary
    /// Retrieves all sync items for a specific source library.
    /// </summary>
    /// <param name="sourceLibraryId">Source library ID.</param>
    /// <returns>List of sync items in the library.</returns>
    public List<SyncItem> GetBySourceLibrary(string sourceLibraryId)
    {
        return ExecuteRead(() =>
        {
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
        }, fallbackValue: new List<SyncItem>());
    }

    /// <summary>
    /// GetAll
    /// Retrieves all sync items from the database.
    /// </summary>
    /// <returns>List of all sync items.</returns>
    public List<SyncItem> GetAll()
    {
        return ExecuteRead(() =>
        {
            var items = new List<SyncItem>();
            using var command = _connection!.CreateCommand();
            command.CommandText = "SELECT * FROM SyncItems";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                items.Add(ReadSyncItem(reader));
            }

            return items;
        }, fallbackValue: new List<SyncItem>());
    }

    /// <summary>
    /// Search
    /// Searches sync items by filename with optional status and pending type filters.
    /// </summary>
    /// <param name="searchTerm">Search term for filename.</param>
    /// <param name="status">Optional status filter.</param>
    /// <param name="pendingType">Optional pending type filter.</param>
    /// <returns>List of matching sync items.</returns>
    public List<SyncItem> Search(string? searchTerm, SyncStatus? status = null, PendingType? pendingType = null)
    {
        return ExecuteRead(() =>
        {
            var items = new List<SyncItem>();
            using var command = _connection!.CreateCommand();

            var conditions = new List<string>();
            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                // Search in filename portion of SourcePath (case-insensitive)
                conditions.Add("(SourcePath LIKE @searchTerm OR LocalPath LIKE @searchTerm)");
                command.Parameters.AddWithValue("@searchTerm", $"%{searchTerm}%");
            }

            if (status.HasValue)
            {
                conditions.Add("Status = @status");
                command.Parameters.AddWithValue("@status", (int)status.Value);
            }

            if (pendingType.HasValue)
            {
                conditions.Add("PendingType = @pendingType");
                command.Parameters.AddWithValue("@pendingType", (int)pendingType.Value);
            }

            command.CommandText = conditions.Count > 0
                ? $"SELECT * FROM SyncItems WHERE {string.Join(" AND ", conditions)}"
                : "SELECT * FROM SyncItems";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                items.Add(ReadSyncItem(reader));
            }

            return items;
        }, fallbackValue: new List<SyncItem>());
    }

    /// <summary>
    /// SearchPaginated
    /// Searches sync items with pagination support.
    /// </summary>
    /// <param name="searchTerm">Search term for filename.</param>
    /// <param name="status">Optional status filter.</param>
    /// <param name="pendingType">Optional pending type filter.</param>
    /// <param name="skip">Number of items to skip.</param>
    /// <param name="take">Maximum items to return.</param>
    /// <returns>Tuple of items and total count.</returns>
    public (List<SyncItem> Items, int TotalCount) SearchPaginated(
        string? searchTerm,
        SyncStatus? status = null,
        PendingType? pendingType = null,
        int skip = 0,
        int take = 50)
    {
        return ExecuteRead(() =>
        {
            var conditions = new List<string>();

            // Build WHERE clause conditions
            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                conditions.Add("(SourcePath LIKE @searchTerm OR LocalPath LIKE @searchTerm)");
            }

            if (status.HasValue)
            {
                conditions.Add("Status = @status");
            }

            if (pendingType.HasValue)
            {
                conditions.Add("PendingType = @pendingType");
            }

            var whereClause = conditions.Count > 0
                ? $"WHERE {string.Join(" AND ", conditions)}"
                : string.Empty;

            // Get total count
            int totalCount;
            using (var countCommand = _connection!.CreateCommand())
            {
                countCommand.CommandText = $"SELECT COUNT(*) FROM SyncItems {whereClause}";

                if (!string.IsNullOrWhiteSpace(searchTerm))
                {
                    countCommand.Parameters.AddWithValue("@searchTerm", $"%{searchTerm}%");
                }

                if (status.HasValue)
                {
                    countCommand.Parameters.AddWithValue("@status", (int)status.Value);
                }

                if (pendingType.HasValue)
                {
                    countCommand.Parameters.AddWithValue("@pendingType", (int)pendingType.Value);
                }

                totalCount = Convert.ToInt32(countCommand.ExecuteScalar());
            }

            // Get paginated data
            var items = new List<SyncItem>();
            using (var dataCommand = _connection!.CreateCommand())
            {
                dataCommand.CommandText = $@"
                    SELECT * FROM SyncItems
                    {whereClause}
                    ORDER BY
                        CASE Status
                            WHEN 0 THEN 0  -- Pending first
                            WHEN 3 THEN 1  -- Errored second
                            WHEN 1 THEN 2  -- Queued third
                            WHEN 4 THEN 3  -- Ignored fourth
                            WHEN 2 THEN 4  -- Synced last
                            ELSE 5
                        END,
                        LocalPath ASC
                    LIMIT @take OFFSET @skip";

                if (!string.IsNullOrWhiteSpace(searchTerm))
                {
                    dataCommand.Parameters.AddWithValue("@searchTerm", $"%{searchTerm}%");
                }

                if (status.HasValue)
                {
                    dataCommand.Parameters.AddWithValue("@status", (int)status.Value);
                }

                if (pendingType.HasValue)
                {
                    dataCommand.Parameters.AddWithValue("@pendingType", (int)pendingType.Value);
                }

                dataCommand.Parameters.AddWithValue("@take", take);
                dataCommand.Parameters.AddWithValue("@skip", skip);

                using var reader = dataCommand.ExecuteReader();
                while (reader.Read())
                {
                    items.Add(ReadSyncItem(reader));
                }
            }

            return (items, totalCount);
        }, fallbackValue: (new List<SyncItem>(), 0));
    }

    /// <summary>
    /// GetPendingByType
    /// Retrieves all pending items with a specific pending type.
    /// </summary>
    /// <param name="pendingType">Type of pending operation.</param>
    /// <returns>List of pending sync items.</returns>
    public List<SyncItem> GetPendingByType(PendingType pendingType)
    {
        return ExecuteRead(() =>
        {
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
        }, fallbackValue: new List<SyncItem>());
    }

    /// <summary>
    /// GetPendingCounts
    /// Returns counts of pending items grouped by pending type.
    /// </summary>
    /// <returns>Dictionary of pending type to count.</returns>
    public Dictionary<PendingType, int> GetPendingCounts()
    {
        return ExecuteRead(() =>
        {
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
        }, fallbackValue: new Dictionary<PendingType, int>());
    }

    /// <summary>
    /// GetPendingSizes
    /// Returns the total size in bytes for items that need to be synced.
    /// Calculates: PendingDownload + PendingReplacement + Queued - PendingDeletion
    /// </summary>
    /// <returns>Dictionary with size breakdown and total.</returns>
    public Dictionary<string, long> GetPendingSizes()
    {
        return ExecuteRead(() =>
        {
            var sizes = new Dictionary<string, long>();

            // Get pending download size
            using var pendingDownloadCmd = _connection!.CreateCommand();
            pendingDownloadCmd.CommandText = "SELECT COALESCE(SUM(SourceSize), 0) FROM SyncItems WHERE Status = @status AND PendingType = @pendingType";
            pendingDownloadCmd.Parameters.AddWithValue("@status", (int)SyncStatus.Pending);
            pendingDownloadCmd.Parameters.AddWithValue("@pendingType", (int)PendingType.Download);
            sizes["PendingDownload"] = Convert.ToInt64(pendingDownloadCmd.ExecuteScalar());

            // Get pending replacement size
            using var pendingReplacementCmd = _connection.CreateCommand();
            pendingReplacementCmd.CommandText = "SELECT COALESCE(SUM(SourceSize), 0) FROM SyncItems WHERE Status = @status AND PendingType = @pendingType";
            pendingReplacementCmd.Parameters.AddWithValue("@status", (int)SyncStatus.Pending);
            pendingReplacementCmd.Parameters.AddWithValue("@pendingType", (int)PendingType.Replacement);
            sizes["PendingReplacement"] = Convert.ToInt64(pendingReplacementCmd.ExecuteScalar());

            // Get pending deletion size
            using var pendingDeletionCmd = _connection.CreateCommand();
            pendingDeletionCmd.CommandText = "SELECT COALESCE(SUM(SourceSize), 0) FROM SyncItems WHERE Status = @status AND PendingType = @pendingType";
            pendingDeletionCmd.Parameters.AddWithValue("@status", (int)SyncStatus.Pending);
            pendingDeletionCmd.Parameters.AddWithValue("@pendingType", (int)PendingType.Deletion);
            sizes["PendingDeletion"] = Convert.ToInt64(pendingDeletionCmd.ExecuteScalar());

            // Get queued size
            using var queuedCmd = _connection.CreateCommand();
            queuedCmd.CommandText = "SELECT COALESCE(SUM(SourceSize), 0) FROM SyncItems WHERE Status = @status";
            queuedCmd.Parameters.AddWithValue("@status", (int)SyncStatus.Queued);
            sizes["Queued"] = Convert.ToInt64(queuedCmd.ExecuteScalar());

            // Calculate total: PendingDownload + PendingReplacement + Queued - PendingDeletion
            sizes["Total"] = sizes["PendingDownload"] + sizes["PendingReplacement"] + sizes["Queued"] - sizes["PendingDeletion"];

            return sizes;
        }, fallbackValue: new Dictionary<string, long>());
    }

    /// <summary>
    /// GetErroredItemsForRetry
    /// Gets errored items that haven't exceeded max retries.
    /// </summary>
    /// <param name="maxRetries">Maximum retry count threshold.</param>
    /// <returns>List of errored items eligible for retry.</returns>
    public List<SyncItem> GetErroredItemsForRetry(int maxRetries)
    {
        return ExecuteRead(() =>
        {
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
        }, fallbackValue: new List<SyncItem>());
    }

    /// <summary>
    /// Upsert
    /// Inserts or updates a sync item in the database.
    /// </summary>
    /// <param name="item">Sync item to insert or update.</param>
    /// <param name="transaction">Optional transaction.</param>
    public void Upsert(SyncItem item, SqliteTransaction? transaction = null)
    {
        ThrowIfDisposed();
        lock (_writeLock)
        {
            UpsertInternal(item, transaction);
        }
    }

    private void UpsertInternal(SyncItem item, SqliteTransaction? transaction)
    {
        EnsureConnection();

        using var command = _connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
            INSERT INTO SyncItems (
                SourceLibraryId, LocalLibraryId, SourceItemId, SourcePath, SourceSize,
                SourceCreateDate, SourceModifyDate, SourceETag, LocalItemId, LocalPath, StatusDate, Status,
                PendingType, LastSyncTime, ErrorMessage, RetryCount, CompanionFiles
            ) VALUES (
                @sourceLibraryId, @localLibraryId, @sourceItemId, @sourcePath, @sourceSize,
                @sourceCreateDate, @sourceModifyDate, @sourceETag, @localItemId, @localPath, @statusDate, @status,
                @pendingType, @lastSyncTime, @errorMessage, @retryCount, @companionFiles
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
                RetryCount = @retryCount,
                CompanionFiles = @companionFiles
        ";

        command.Parameters.AddWithValue("@sourceLibraryId", item.SourceLibraryId);
        command.Parameters.AddWithValue("@localLibraryId", item.LocalLibraryId);
        command.Parameters.AddWithValue("@sourceItemId", item.SourceItemId);
        command.Parameters.AddWithValue("@sourcePath", item.SourcePath);
        command.Parameters.AddWithValue("@sourceSize", item.SourceSize);
        command.Parameters.AddWithValue("@sourceCreateDate", item.SourceCreateDate.ToString("o"));
        command.Parameters.AddWithValue("@sourceModifyDate", item.SourceCreateDate.ToString("o")); // Deprecated: use SourceCreateDate
        command.Parameters.AddWithValue("@sourceETag", item.SourceETag ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@localItemId", item.LocalItemId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@localPath", item.LocalPath ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@statusDate", item.StatusDate.ToString("o"));
        command.Parameters.AddWithValue("@status", (int)item.Status);
        command.Parameters.AddWithValue("@pendingType", item.PendingType.HasValue ? (int)item.PendingType.Value : DBNull.Value);
        command.Parameters.AddWithValue("@lastSyncTime", item.LastSyncTime?.ToString("o") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@errorMessage", item.ErrorMessage ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@retryCount", item.RetryCount);
        command.Parameters.AddWithValue("@companionFiles", item.CompanionFiles ?? (object)DBNull.Value);

        command.ExecuteNonQuery();
    }

    /// <summary>
    /// UpdateStatus
    /// Updates the status of a sync item with optional fields.
    /// </summary>
    /// <param name="sourceItemId">Source item ID.</param>
    /// <param name="status">New status.</param>
    /// <param name="pendingType">Optional pending type.</param>
    /// <param name="localItemId">Optional local item ID.</param>
    /// <param name="localPath">Optional local path.</param>
    /// <param name="errorMessage">Optional error message.</param>
    /// <param name="sourceETag">Optional source ETag.</param>
    /// <param name="sourceSize">Optional source size.</param>
    public void UpdateStatus(
        string sourceItemId,
        SyncStatus status,
        PendingType? pendingType = null,
        string? localItemId = null,
        string? localPath = null,
        string? errorMessage = null,
        string? sourceETag = null,
        long? sourceSize = null,
        string? companionFiles = null)
    {
        ThrowIfDisposed();
        lock (_writeLock)
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

        if (companionFiles != null)
        {
            setClauses.Add("CompanionFiles = @companionFiles");
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

        if (companionFiles != null)
        {
            command.Parameters.AddWithValue("@companionFiles", companionFiles);
        }

        command.Parameters.AddWithValue("@errorMessage", errorMessage ?? (object)DBNull.Value);

            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Delete
    /// Deletes a sync item by its source item ID.
    /// </summary>
    /// <param name="sourceItemId">Source item ID to delete.</param>
    /// <param name="transaction">Optional transaction.</param>
    public void Delete(string sourceItemId, SqliteTransaction? transaction = null)
    {
        ThrowIfDisposed();
        // Only lock if we're not already inside a transaction (which would have its own lock)
        if (transaction == null)
        {
            lock (_writeLock)
            {
                DeleteInternal(sourceItemId, null);
            }
        }
        else
        {
            DeleteInternal(sourceItemId, transaction);
        }
    }

    private void DeleteInternal(string sourceItemId, SqliteTransaction? transaction)
    {
        EnsureConnection();

        using var command = _connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM SyncItems WHERE SourceItemId = @sourceItemId";
        command.Parameters.AddWithValue("@sourceItemId", sourceItemId);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// GetStatusCounts
    /// Returns counts of items grouped by status.
    /// </summary>
    /// <returns>Dictionary of status to count.</returns>
    public Dictionary<SyncStatus, int> GetStatusCounts()
    {
        return ExecuteRead(() =>
        {
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
        }, fallbackValue: new Dictionary<SyncStatus, int>());
    }

    /// <summary>
    /// GetSyncStats
    /// Returns detailed sync statistics.
    /// </summary>
    /// <returns>Sync statistics object.</returns>
    public SyncStats GetSyncStats()
    {
        return ExecuteRead(() =>
        {
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
                stats.LastSyncTime = DateTime.Parse((string)lastSync, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
            }

            return stats;
        }, fallbackValue: new SyncStats());
    }

    /// <summary>
    /// ClearStaleErrors
    /// Resets error status for items older than specified days.
    /// </summary>
    /// <param name="olderThanDays">Age threshold in days.</param>
    /// <returns>Number of items reset.</returns>
    public int ClearStaleErrors(int olderThanDays)
    {
        ThrowIfDisposed();
        lock (_writeLock)
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
    }

    /// <summary>
    /// ReadSyncItem
    /// Reads a SyncItem from the database reader.
    /// </summary>
    /// <param name="reader">SQLite data reader.</param>
    /// <returns>Populated sync item.</returns>
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
            // SourceModifyDate column exists in DB but is deprecated - not read into model
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

            var companionOrdinal = reader.GetOrdinal("CompanionFiles");
            if (!reader.IsDBNull(companionOrdinal))
            {
                item.CompanionFiles = reader.GetString(companionOrdinal);
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            // Column doesn't exist yet (pre-migration)
        }

        return item;
    }

    /// <summary>
    /// ParseDateTimeSafe
    /// Parses a datetime string safely, returning DateTime.MinValue on failure.
    /// </summary>
    /// <param name="dateString">Date string to parse.</param>
    /// <returns>Parsed datetime or MinValue on failure.</returns>
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

    /// <summary>
    /// Safely tries to get a column ordinal, returning -1 if column doesn't exist.
    /// Used for columns that may not exist in older schema versions.
    /// </summary>
    private static int TryGetOrdinal(SqliteDataReader reader, string columnName)
    {
        try
        {
            return reader.GetOrdinal(columnName);
        }
        catch (ArgumentOutOfRangeException)
        {
            return -1;
        }
    }

    /// <summary>
    /// EnsureConnection
    /// Ensures the database connection is open, reopening if necessary.
    /// </summary>
    private void EnsureConnection()
    {
        if (_connection != null && _connection.State == ConnectionState.Open)
        {
            return;
        }

        try
        {
            _connection?.Close();
            _connection?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disposing old database connection");
        }

        _connection = null;

        // Clear the connection pool to avoid stale cached connections
        SqliteConnection.ClearAllPools();

        SqliteConnection? newConnection = null;
        try
        {
            newConnection = new SqliteConnection(BuildConnectionString());
            newConnection.Open();

            // Re-apply pragmas on reconnection
            using var pragmaCmd = newConnection.CreateCommand();
            pragmaCmd.CommandText = @"
                PRAGMA journal_mode=WAL;
                PRAGMA busy_timeout=5000;
                PRAGMA synchronous=NORMAL;
            ";
            pragmaCmd.ExecuteNonQuery();

            _connection = newConnection;
            newConnection = null; // Transfer ownership, prevent dispose in finally
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open database connection to {DbPath}", _dbPath);
            throw new InvalidOperationException($"Unable to open database connection: {ex.Message}", ex);
        }
        finally
        {
            // If newConnection is still set, we failed after Open() but before
            // assigning to _connection — dispose to prevent leak
            newConnection?.Dispose();
        }
    }

    /// <summary>
    /// ResetDatabase
    /// Drops all data and recreates the database with the latest schema.
    /// </summary>
    public void ResetDatabase()
    {
        ThrowIfDisposed();
        lock (_writeLock)
        {
            _logger.LogWarning("Resetting sync database - all tracking data will be lost");

            _connection?.Close();
            _connection?.Dispose();
            _connection = null;

            // Clear SQLite connection pool to release file handles
            SqliteConnection.ClearAllPools();

            // Delete main database file with retry logic
            if (File.Exists(_dbPath))
            {
                DeleteFileWithRetry(_dbPath);
            }

            // Also delete WAL and SHM files if they exist
            DeleteWalFiles();

            InitializeDatabase();
            _logger.LogInformation("Sync database has been reset with fresh schema v{Version}", DatabaseMigrationService.CurrentSchemaVersion);
        }
    }

    /// <summary>
    /// ResetHistoryDatabase
    /// Clears all history sync items from the database.
    /// Falls back to a full database recreation if the database is readonly or corrupted.
    /// </summary>
    public void ResetHistoryDatabase()
    {
        ThrowIfDisposed();
        lock (_writeLock)
        {
            _logger.LogWarning("Resetting history sync database - all history tracking data will be lost");

            try
            {
                EnsureConnection();
                using var command = _connection!.CreateCommand();
                command.CommandText = "DELETE FROM HistorySyncItems WHERE 1=1";
                var deleted = command.ExecuteNonQuery();
                _logger.LogInformation("History sync database has been reset, {Count} items removed", deleted);
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 1)
            {
                // Table doesn't exist - that's fine, nothing to delete
                _logger.LogInformation("History sync table does not exist yet, nothing to reset");
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 8)
            {
                _logger.LogWarning(ex, "Database is readonly, falling back to full database recreation");
                RecreateDatabase();
            }
        }
    }

    /// <summary>
    /// ExecuteInTransaction
    /// Executes multiple database operations within a transaction.
    /// </summary>
    /// <param name="action">Action to execute within transaction.</param>
    /// <returns>True if committed successfully.</returns>
    public bool ExecuteInTransaction(Action<SqliteTransaction> action)
    {
        ThrowIfDisposed();
        lock (_writeLock)
        {
            EnsureConnection();

            using var transaction = _connection!.BeginTransaction();
            try
            {
                action(transaction);
                transaction.Commit();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Transaction failed, rolling back");
                try
                {
                transaction.Rollback();
            }
            catch (Exception rollbackEx)
            {
                _logger.LogError(rollbackEx, "Failed to rollback transaction");
            }

                return false;
            }
        }
    }

    /// <summary>
    /// BatchDelete
    /// Deletes multiple items within a transaction.
    /// </summary>
    /// <param name="sourceItemIds">Source item IDs to delete.</param>
    /// <returns>Number of items deleted.</returns>
    public int BatchDelete(IEnumerable<string> sourceItemIds)
    {
        ThrowIfDisposed();
        lock (_writeLock)
        {
            EnsureConnection();

            var count = 0;
            using var transaction = _connection!.BeginTransaction();
            try
            {
                foreach (var sourceItemId in sourceItemIds)
                {
                    DeleteInternal(sourceItemId, transaction);
                    count++;
                }

                transaction.Commit();
                return count;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Batch delete failed after {Count} items, rolling back", count);
                transaction.Rollback();
                throw;
            }
        }
    }

    /// <summary>
    /// BatchUpdateStatus
    /// Updates the status of multiple items within a transaction.
    /// </summary>
    /// <param name="sourceItemIds">Source item IDs to update.</param>
    /// <param name="status">New status to set.</param>
    /// <param name="errorMessage">Optional error message.</param>
    /// <returns>Number of items updated.</returns>
    public int BatchUpdateStatus(IEnumerable<string> sourceItemIds, SyncStatus status, string? errorMessage = null)
    {
        ThrowIfDisposed();
        lock (_writeLock)
        {
            EnsureConnection();

            var count = 0;
            using var transaction = _connection!.BeginTransaction();
            try
            {
                foreach (var sourceItemId in sourceItemIds)
                {
                    using var command = _connection!.CreateCommand();
                    command.Transaction = transaction;

                    if (status == SyncStatus.Errored && errorMessage != null)
                    {
                        command.CommandText = @"
                            UPDATE SyncItems
                            SET Status = @status, StatusDate = @statusDate, ErrorMessage = @errorMessage, RetryCount = COALESCE(RetryCount, 0) + 1
                            WHERE SourceItemId = @sourceItemId";
                        command.Parameters.AddWithValue("@errorMessage", errorMessage);
                    }
                    else
                    {
                        command.CommandText = @"
                            UPDATE SyncItems
                            SET Status = @status, StatusDate = @statusDate, ErrorMessage = NULL, PendingType = NULL
                            WHERE SourceItemId = @sourceItemId";
                    }

                    command.Parameters.AddWithValue("@sourceItemId", sourceItemId);
                    command.Parameters.AddWithValue("@status", (int)status);
                    command.Parameters.AddWithValue("@statusDate", DateTime.UtcNow.ToString("o"));

                    command.ExecuteNonQuery();
                    count++;
                }

                transaction.Commit();
                return count;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Batch update status failed after {Count} items, rolling back", count);
                transaction.Rollback();
                throw;
            }
        }
    }

    // ============================================
    // History Sync Methods
    // ============================================

    /// <summary>
    /// Gets a history sync item by source user ID and source item ID.
    /// </summary>
    public HistorySyncItem? GetHistoryItem(string sourceUserId, string sourceItemId)
    {
        return ExecuteRead(() =>
        {
            using var command = _connection!.CreateCommand();
            command.CommandText = "SELECT * FROM HistorySyncItems WHERE SourceUserId = @sourceUserId AND SourceItemId = @sourceItemId";
            command.Parameters.AddWithValue("@sourceUserId", sourceUserId);
            command.Parameters.AddWithValue("@sourceItemId", sourceItemId);

            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadHistorySyncItem(reader) : null;
        }, fallbackValue: null);
    }

    /// <summary>
    /// Gets all history sync items for a specific user mapping.
    /// </summary>
    public List<HistorySyncItem> GetHistoryItemsByUserMapping(string sourceUserId, string localUserId)
    {
        return ExecuteRead(() =>
        {
            var items = new List<HistorySyncItem>();
            using var command = _connection!.CreateCommand();
            command.CommandText = "SELECT * FROM HistorySyncItems WHERE SourceUserId = @sourceUserId AND LocalUserId = @localUserId";
            command.Parameters.AddWithValue("@sourceUserId", sourceUserId);
            command.Parameters.AddWithValue("@localUserId", localUserId);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                items.Add(ReadHistorySyncItem(reader));
            }

            return items;
        }, fallbackValue: new List<HistorySyncItem>());
    }

    /// <summary>
    /// Gets all history sync items with a specific status.
    /// </summary>
    public List<HistorySyncItem> GetHistoryItemsByStatus(BaseSyncStatus status)
    {
        return ExecuteRead(() =>
        {
            var items = new List<HistorySyncItem>();
            using var command = _connection!.CreateCommand();
            command.CommandText = "SELECT * FROM HistorySyncItems WHERE Status = @status";
            command.Parameters.AddWithValue("@status", (int)status);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                items.Add(ReadHistorySyncItem(reader));
            }

            return items;
        }, fallbackValue: new List<HistorySyncItem>());
    }

    /// <summary>
    /// Searches history sync items with pagination support.
    /// </summary>
    public (List<HistorySyncItem> Items, int TotalCount) SearchHistoryItemsPaginated(
        string? searchTerm = null,
        BaseSyncStatus? status = null,
        string? sourceUserId = null,
        int skip = 0,
        int take = 50)
    {
        return ExecuteRead(() =>
        {
            var conditions = new List<string>();

            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                conditions.Add("(ItemName LIKE @searchTerm OR SourcePath LIKE @searchTerm)");
            }

            if (status.HasValue)
            {
                conditions.Add("Status = @status");
            }

            if (!string.IsNullOrWhiteSpace(sourceUserId))
            {
                conditions.Add("SourceUserId = @sourceUserId");
            }

            var whereClause = conditions.Count > 0
                ? $"WHERE {string.Join(" AND ", conditions)}"
                : string.Empty;

            // Get total count
            int totalCount;
            using (var countCommand = _connection!.CreateCommand())
            {
                countCommand.CommandText = $"SELECT COUNT(*) FROM HistorySyncItems {whereClause}";

                if (!string.IsNullOrWhiteSpace(searchTerm))
                {
                    countCommand.Parameters.AddWithValue("@searchTerm", $"%{searchTerm}%");
                }

                if (status.HasValue)
                {
                    countCommand.Parameters.AddWithValue("@status", (int)status.Value);
                }

                if (!string.IsNullOrWhiteSpace(sourceUserId))
                {
                    countCommand.Parameters.AddWithValue("@sourceUserId", sourceUserId);
                }

                totalCount = Convert.ToInt32(countCommand.ExecuteScalar());
            }

            // Get paginated data
            var items = new List<HistorySyncItem>();
            using (var dataCommand = _connection!.CreateCommand())
            {
                dataCommand.CommandText = $@"
                    SELECT * FROM HistorySyncItems
                    {whereClause}
                    ORDER BY
                        CASE Status
                            WHEN 3 THEN 0  -- Errored first
                            WHEN 1 THEN 1  -- Queued second
                            WHEN 4 THEN 2  -- Ignored third
                            WHEN 2 THEN 3  -- Synced last
                            ELSE 4
                        END,
                        ItemName ASC
                    LIMIT @take OFFSET @skip";

                if (!string.IsNullOrWhiteSpace(searchTerm))
                {
                    dataCommand.Parameters.AddWithValue("@searchTerm", $"%{searchTerm}%");
                }

                if (status.HasValue)
                {
                    dataCommand.Parameters.AddWithValue("@status", (int)status.Value);
                }

                if (!string.IsNullOrWhiteSpace(sourceUserId))
                {
                    dataCommand.Parameters.AddWithValue("@sourceUserId", sourceUserId);
                }

                dataCommand.Parameters.AddWithValue("@take", take);
                dataCommand.Parameters.AddWithValue("@skip", skip);

                using var reader = dataCommand.ExecuteReader();
                while (reader.Read())
                {
                    items.Add(ReadHistorySyncItem(reader));
                }
            }

            return (items, totalCount);
        }, fallbackValue: (new List<HistorySyncItem>(), 0));
    }

    /// <summary>
    /// Gets history sync status counts.
    /// </summary>
    public Dictionary<BaseSyncStatus, int> GetHistoryStatusCounts()
    {
        return ExecuteRead(() =>
        {
            var counts = new Dictionary<BaseSyncStatus, int>();
            using var command = _connection!.CreateCommand();
            command.CommandText = "SELECT Status, COUNT(*) as Count FROM HistorySyncItems GROUP BY Status";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var status = (BaseSyncStatus)reader.GetInt32(0);
                var count = reader.GetInt32(1);
                counts[status] = count;
            }

            return counts;
        }, fallbackValue: new Dictionary<BaseSyncStatus, int>());
    }

    /// <summary>
    /// Upserts a history sync item.
    /// </summary>
    public void UpsertHistoryItem(HistorySyncItem item, SqliteTransaction? transaction = null)
    {
        ThrowIfDisposed();
        lock (_writeLock)
        {
            EnsureConnection();

            using var command = _connection!.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                INSERT INTO HistorySyncItems (
                    SourceUserId, LocalUserId, SourceLibraryId, LocalLibraryId,
                    SourceItemId, LocalItemId, ItemName, SourcePath, LocalPath,
                    SourceIsPlayed, SourcePlayCount, SourcePlaybackPositionTicks, SourceLastPlayedDate, SourceIsFavorite,
                    LocalIsPlayed, LocalPlayCount, LocalPlaybackPositionTicks, LocalLastPlayedDate, LocalIsFavorite,
                    MergedIsPlayed, MergedPlayCount, MergedPlaybackPositionTicks, MergedLastPlayedDate, MergedIsFavorite,
                    Status, StatusDate, LastSyncTime, ErrorMessage
                ) VALUES (
                    @sourceUserId, @localUserId, @sourceLibraryId, @localLibraryId,
                    @sourceItemId, @localItemId, @itemName, @sourcePath, @localPath,
                    @sourceIsPlayed, @sourcePlayCount, @sourcePlaybackPositionTicks, @sourceLastPlayedDate, @sourceIsFavorite,
                    @localIsPlayed, @localPlayCount, @localPlaybackPositionTicks, @localLastPlayedDate, @localIsFavorite,
                    @mergedIsPlayed, @mergedPlayCount, @mergedPlaybackPositionTicks, @mergedLastPlayedDate, @mergedIsFavorite,
                    @status, @statusDate, @lastSyncTime, @errorMessage
                )
                ON CONFLICT(SourceUserId, SourceItemId) DO UPDATE SET
                    LocalUserId = @localUserId,
                    SourceLibraryId = @sourceLibraryId,
                    LocalLibraryId = @localLibraryId,
                    LocalItemId = @localItemId,
                    ItemName = @itemName,
                    SourcePath = @sourcePath,
                    LocalPath = @localPath,
                    SourceIsPlayed = @sourceIsPlayed,
                    SourcePlayCount = @sourcePlayCount,
                    SourcePlaybackPositionTicks = @sourcePlaybackPositionTicks,
                    SourceLastPlayedDate = @sourceLastPlayedDate,
                    SourceIsFavorite = @sourceIsFavorite,
                    LocalIsPlayed = @localIsPlayed,
                    LocalPlayCount = @localPlayCount,
                    LocalPlaybackPositionTicks = @localPlaybackPositionTicks,
                    LocalLastPlayedDate = @localLastPlayedDate,
                    LocalIsFavorite = @localIsFavorite,
                    MergedIsPlayed = @mergedIsPlayed,
                    MergedPlayCount = @mergedPlayCount,
                    MergedPlaybackPositionTicks = @mergedPlaybackPositionTicks,
                    MergedLastPlayedDate = @mergedLastPlayedDate,
                    MergedIsFavorite = @mergedIsFavorite,
                    Status = @status,
                    StatusDate = @statusDate,
                    LastSyncTime = @lastSyncTime,
                    ErrorMessage = @errorMessage
            ";

            command.Parameters.AddWithValue("@sourceUserId", item.SourceUserId);
            command.Parameters.AddWithValue("@localUserId", item.LocalUserId);
            command.Parameters.AddWithValue("@sourceLibraryId", item.SourceLibraryId);
            command.Parameters.AddWithValue("@localLibraryId", item.LocalLibraryId);
            command.Parameters.AddWithValue("@sourceItemId", item.SourceItemId);
            command.Parameters.AddWithValue("@localItemId", item.LocalItemId ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@itemName", item.ItemName ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@sourcePath", item.SourcePath ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@localPath", item.LocalPath ?? (object)DBNull.Value);

            // Source state
            command.Parameters.AddWithValue("@sourceIsPlayed", item.SourceIsPlayed.HasValue ? (item.SourceIsPlayed.Value ? 1 : 0) : DBNull.Value);
            command.Parameters.AddWithValue("@sourcePlayCount", item.SourcePlayCount ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@sourcePlaybackPositionTicks", item.SourcePlaybackPositionTicks ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@sourceLastPlayedDate", item.SourceLastPlayedDate?.ToString("o") ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@sourceIsFavorite", item.SourceIsFavorite.HasValue ? (item.SourceIsFavorite.Value ? 1 : 0) : DBNull.Value);

            // Local state
            command.Parameters.AddWithValue("@localIsPlayed", item.LocalIsPlayed.HasValue ? (item.LocalIsPlayed.Value ? 1 : 0) : DBNull.Value);
            command.Parameters.AddWithValue("@localPlayCount", item.LocalPlayCount ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@localPlaybackPositionTicks", item.LocalPlaybackPositionTicks ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@localLastPlayedDate", item.LocalLastPlayedDate?.ToString("o") ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@localIsFavorite", item.LocalIsFavorite.HasValue ? (item.LocalIsFavorite.Value ? 1 : 0) : DBNull.Value);

            // Merged state
            command.Parameters.AddWithValue("@mergedIsPlayed", item.MergedIsPlayed.HasValue ? (item.MergedIsPlayed.Value ? 1 : 0) : DBNull.Value);
            command.Parameters.AddWithValue("@mergedPlayCount", item.MergedPlayCount ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@mergedPlaybackPositionTicks", item.MergedPlaybackPositionTicks ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@mergedLastPlayedDate", item.MergedLastPlayedDate?.ToString("o") ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@mergedIsFavorite", item.MergedIsFavorite.HasValue ? (item.MergedIsFavorite.Value ? 1 : 0) : DBNull.Value);

            // Status
            command.Parameters.AddWithValue("@status", (int)item.Status);
            command.Parameters.AddWithValue("@statusDate", item.StatusDate.ToString("o"));
            command.Parameters.AddWithValue("@lastSyncTime", item.LastSyncTime?.ToString("o") ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@errorMessage", item.ErrorMessage ?? (object)DBNull.Value);

            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Updates the status of a history sync item.
    /// </summary>
    public void UpdateHistoryItemStatus(
        string sourceUserId,
        string sourceItemId,
        BaseSyncStatus status,
        string? errorMessage = null)
    {
        ThrowIfDisposed();
        lock (_writeLock)
        {
            EnsureConnection();

            using var command = _connection!.CreateCommand();

            if (status == BaseSyncStatus.Synced)
            {
                command.CommandText = @"
                    UPDATE HistorySyncItems
                    SET Status = @status, StatusDate = @statusDate, LastSyncTime = @statusDate, ErrorMessage = NULL
                    WHERE SourceUserId = @sourceUserId AND SourceItemId = @sourceItemId";
            }
            else if (status == BaseSyncStatus.Errored)
            {
                command.CommandText = @"
                    UPDATE HistorySyncItems
                    SET Status = @status, StatusDate = @statusDate, ErrorMessage = @errorMessage
                    WHERE SourceUserId = @sourceUserId AND SourceItemId = @sourceItemId";
                command.Parameters.AddWithValue("@errorMessage", errorMessage ?? (object)DBNull.Value);
            }
            else
            {
                command.CommandText = @"
                    UPDATE HistorySyncItems
                    SET Status = @status, StatusDate = @statusDate, ErrorMessage = NULL
                    WHERE SourceUserId = @sourceUserId AND SourceItemId = @sourceItemId";
            }

            command.Parameters.AddWithValue("@sourceUserId", sourceUserId);
            command.Parameters.AddWithValue("@sourceItemId", sourceItemId);
            command.Parameters.AddWithValue("@status", (int)status);
            command.Parameters.AddWithValue("@statusDate", DateTime.UtcNow.ToString("o"));

            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Batch updates status for multiple history sync items.
    /// </summary>
    public int BatchUpdateHistoryItemStatus(IEnumerable<(string SourceUserId, string SourceItemId)> items, BaseSyncStatus status)
    {
        ThrowIfDisposed();
        lock (_writeLock)
        {
            EnsureConnection();

            var count = 0;
            using var transaction = _connection!.BeginTransaction();
            try
            {
                foreach (var (sourceUserId, sourceItemId) in items)
                {
                    using var command = _connection!.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = @"
                        UPDATE HistorySyncItems
                        SET Status = @status, StatusDate = @statusDate, ErrorMessage = NULL
                        WHERE SourceUserId = @sourceUserId AND SourceItemId = @sourceItemId";
                    command.Parameters.AddWithValue("@sourceUserId", sourceUserId);
                    command.Parameters.AddWithValue("@sourceItemId", sourceItemId);
                    command.Parameters.AddWithValue("@status", (int)status);
                    command.Parameters.AddWithValue("@statusDate", DateTime.UtcNow.ToString("o"));

                    command.ExecuteNonQuery();
                    count++;
                }

                transaction.Commit();
                return count;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Batch update history status failed after {Count} items, rolling back", count);
                transaction.Rollback();
                throw;
            }
        }
    }

    /// <summary>
    /// Updates the status of a history sync item by database ID.
    /// </summary>
    /// <param name="id">Database ID of the item.</param>
    /// <param name="status">New status.</param>
    /// <param name="errorMessage">Optional error message.</param>
    public void UpdateHistoryItemStatusById(long id, BaseSyncStatus status, string? errorMessage = null)
    {
        ThrowIfDisposed();
        lock (_writeLock)
        {
            EnsureConnection();

            using var command = _connection!.CreateCommand();

            if (status == BaseSyncStatus.Synced)
            {
                command.CommandText = @"
                    UPDATE HistorySyncItems
                    SET Status = @status,
                        StatusDate = @statusDate,
                        LastSyncTime = @statusDate,
                        ErrorMessage = NULL
                    WHERE Id = @id";
            }
            else
            {
                command.CommandText = @"
                    UPDATE HistorySyncItems
                    SET Status = @status,
                        StatusDate = @statusDate,
                        ErrorMessage = @errorMessage
                    WHERE Id = @id";
                command.Parameters.AddWithValue("@errorMessage", errorMessage ?? (object)DBNull.Value);
            }

            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@status", (int)status);
            command.Parameters.AddWithValue("@statusDate", DateTime.UtcNow.ToString("o"));

            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Deletes a history sync item by its database ID.
    /// </summary>
    /// <param name="id">Database ID of the item to delete.</param>
    /// <returns>True if an item was deleted, false otherwise.</returns>
    public bool DeleteHistoryItem(long id)
    {
        ThrowIfDisposed();
        lock (_writeLock)
        {
            EnsureConnection();

            using var command = _connection!.CreateCommand();
            command.CommandText = "DELETE FROM HistorySyncItems WHERE Id = @id";
            command.Parameters.AddWithValue("@id", id);

            return command.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>
    /// Batch updates status for multiple history sync items by their database IDs.
    /// </summary>
    /// <param name="ids">List of database IDs.</param>
    /// <param name="status">New status.</param>
    /// <returns>Number of items updated.</returns>
    public int BatchUpdateHistoryItemStatusByIds(IEnumerable<long> ids, BaseSyncStatus status)
    {
        ThrowIfDisposed();
        lock (_writeLock)
        {
            EnsureConnection();

            var count = 0;
            using var transaction = _connection!.BeginTransaction();
            try
            {
                foreach (var id in ids)
                {
                    using var command = _connection!.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = @"
                        UPDATE HistorySyncItems
                        SET Status = @status,
                            StatusDate = @statusDate
                        WHERE Id = @id";

                    command.Parameters.AddWithValue("@id", id);
                    command.Parameters.AddWithValue("@status", (int)status);
                    command.Parameters.AddWithValue("@statusDate", DateTime.UtcNow.ToString("o"));

                    count += command.ExecuteNonQuery();
                }

                transaction.Commit();
                return count;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Batch update history status by IDs failed after {Count} items, rolling back", count);
                transaction.Rollback();
                throw;
            }
        }
    }

    /// <summary>
    /// Reads a HistorySyncItem from the database reader.
    /// </summary>
    private static HistorySyncItem ReadHistorySyncItem(SqliteDataReader reader)
    {
        var item = new HistorySyncItem
        {
            Id = reader.GetInt64(reader.GetOrdinal("Id")),
            SourceUserId = reader.GetString(reader.GetOrdinal("SourceUserId")),
            LocalUserId = reader.GetString(reader.GetOrdinal("LocalUserId")),
            SourceLibraryId = reader.GetString(reader.GetOrdinal("SourceLibraryId")),
            LocalLibraryId = reader.GetString(reader.GetOrdinal("LocalLibraryId")),
            SourceItemId = reader.GetString(reader.GetOrdinal("SourceItemId")),
            Status = (BaseSyncStatus)reader.GetInt32(reader.GetOrdinal("Status")),
            StatusDate = ParseDateTimeSafe(reader.GetString(reader.GetOrdinal("StatusDate")))
        };

        // Optional fields
        var localItemIdOrdinal = reader.GetOrdinal("LocalItemId");
        if (!reader.IsDBNull(localItemIdOrdinal))
        {
            item.LocalItemId = reader.GetString(localItemIdOrdinal);
        }

        var itemNameOrdinal = reader.GetOrdinal("ItemName");
        if (!reader.IsDBNull(itemNameOrdinal))
        {
            item.ItemName = reader.GetString(itemNameOrdinal);
        }

        var sourcePathOrdinal = reader.GetOrdinal("SourcePath");
        if (!reader.IsDBNull(sourcePathOrdinal))
        {
            item.SourcePath = reader.GetString(sourcePathOrdinal);
        }

        var localPathOrdinal = reader.GetOrdinal("LocalPath");
        if (!reader.IsDBNull(localPathOrdinal))
        {
            item.LocalPath = reader.GetString(localPathOrdinal);
        }

        // Source state
        var sourceIsPlayedOrdinal = reader.GetOrdinal("SourceIsPlayed");
        if (!reader.IsDBNull(sourceIsPlayedOrdinal))
        {
            item.SourceIsPlayed = reader.GetInt32(sourceIsPlayedOrdinal) == 1;
        }

        var sourcePlayCountOrdinal = reader.GetOrdinal("SourcePlayCount");
        if (!reader.IsDBNull(sourcePlayCountOrdinal))
        {
            item.SourcePlayCount = reader.GetInt32(sourcePlayCountOrdinal);
        }

        var sourcePlaybackPositionOrdinal = reader.GetOrdinal("SourcePlaybackPositionTicks");
        if (!reader.IsDBNull(sourcePlaybackPositionOrdinal))
        {
            item.SourcePlaybackPositionTicks = reader.GetInt64(sourcePlaybackPositionOrdinal);
        }

        var sourceLastPlayedOrdinal = reader.GetOrdinal("SourceLastPlayedDate");
        if (!reader.IsDBNull(sourceLastPlayedOrdinal))
        {
            item.SourceLastPlayedDate = ParseDateTimeSafe(reader.GetString(sourceLastPlayedOrdinal));
        }

        var sourceIsFavoriteOrdinal = reader.GetOrdinal("SourceIsFavorite");
        if (!reader.IsDBNull(sourceIsFavoriteOrdinal))
        {
            item.SourceIsFavorite = reader.GetInt32(sourceIsFavoriteOrdinal) == 1;
        }

        // Local state
        var localIsPlayedOrdinal = reader.GetOrdinal("LocalIsPlayed");
        if (!reader.IsDBNull(localIsPlayedOrdinal))
        {
            item.LocalIsPlayed = reader.GetInt32(localIsPlayedOrdinal) == 1;
        }

        var localPlayCountOrdinal = reader.GetOrdinal("LocalPlayCount");
        if (!reader.IsDBNull(localPlayCountOrdinal))
        {
            item.LocalPlayCount = reader.GetInt32(localPlayCountOrdinal);
        }

        var localPlaybackPositionOrdinal = reader.GetOrdinal("LocalPlaybackPositionTicks");
        if (!reader.IsDBNull(localPlaybackPositionOrdinal))
        {
            item.LocalPlaybackPositionTicks = reader.GetInt64(localPlaybackPositionOrdinal);
        }

        var localLastPlayedOrdinal = reader.GetOrdinal("LocalLastPlayedDate");
        if (!reader.IsDBNull(localLastPlayedOrdinal))
        {
            item.LocalLastPlayedDate = ParseDateTimeSafe(reader.GetString(localLastPlayedOrdinal));
        }

        var localIsFavoriteOrdinal = reader.GetOrdinal("LocalIsFavorite");
        if (!reader.IsDBNull(localIsFavoriteOrdinal))
        {
            item.LocalIsFavorite = reader.GetInt32(localIsFavoriteOrdinal) == 1;
        }

        // Merged state
        var mergedIsPlayedOrdinal = reader.GetOrdinal("MergedIsPlayed");
        if (!reader.IsDBNull(mergedIsPlayedOrdinal))
        {
            item.MergedIsPlayed = reader.GetInt32(mergedIsPlayedOrdinal) == 1;
        }

        var mergedPlayCountOrdinal = reader.GetOrdinal("MergedPlayCount");
        if (!reader.IsDBNull(mergedPlayCountOrdinal))
        {
            item.MergedPlayCount = reader.GetInt32(mergedPlayCountOrdinal);
        }

        var mergedPlaybackPositionOrdinal = reader.GetOrdinal("MergedPlaybackPositionTicks");
        if (!reader.IsDBNull(mergedPlaybackPositionOrdinal))
        {
            item.MergedPlaybackPositionTicks = reader.GetInt64(mergedPlaybackPositionOrdinal);
        }

        var mergedLastPlayedOrdinal = reader.GetOrdinal("MergedLastPlayedDate");
        if (!reader.IsDBNull(mergedLastPlayedOrdinal))
        {
            item.MergedLastPlayedDate = ParseDateTimeSafe(reader.GetString(mergedLastPlayedOrdinal));
        }

        var mergedIsFavoriteOrdinal = reader.GetOrdinal("MergedIsFavorite");
        if (!reader.IsDBNull(mergedIsFavoriteOrdinal))
        {
            item.MergedIsFavorite = reader.GetInt32(mergedIsFavoriteOrdinal) == 1;
        }

        // Sync tracking
        var lastSyncTimeOrdinal = reader.GetOrdinal("LastSyncTime");
        if (!reader.IsDBNull(lastSyncTimeOrdinal))
        {
            item.LastSyncTime = ParseDateTimeSafe(reader.GetString(lastSyncTimeOrdinal));
        }

        var errorMessageOrdinal = reader.GetOrdinal("ErrorMessage");
        if (!reader.IsDBNull(errorMessageOrdinal))
        {
            item.ErrorMessage = reader.GetString(errorMessageOrdinal);
        }

        return item;
    }

    // ============================================
    // User Sync Methods
    // ============================================

    /// <summary>
    /// Gets a user sync item by its ID.
    /// </summary>
    public UserSyncItem? GetUserSyncItemById(long id)
    {
        return ExecuteRead(() =>
        {
            using var command = _connection!.CreateCommand();
            command.CommandText = "SELECT * FROM UserSyncItems WHERE Id = @id";
            command.Parameters.AddWithValue("@id", id);

            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadUserSyncItem(reader) : null;
        }, fallbackValue: null);
    }

    /// <summary>
    /// Gets a user sync item by user mapping and property category.
    /// </summary>
    public UserSyncItem? GetUserSyncItem(string sourceUserId, string localUserId, string propertyCategory)
    {
        return ExecuteRead(() =>
        {
            using var command = _connection!.CreateCommand();
            command.CommandText = @"
                SELECT * FROM UserSyncItems
                WHERE SourceUserId = @sourceUserId
                  AND LocalUserId = @localUserId
                  AND PropertyCategory = @propertyCategory";
            command.Parameters.AddWithValue("@sourceUserId", sourceUserId);
            command.Parameters.AddWithValue("@localUserId", localUserId);
            command.Parameters.AddWithValue("@propertyCategory", propertyCategory);

            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadUserSyncItem(reader) : null;
        }, fallbackValue: null);
    }

    /// <summary>
    /// Gets all user sync items for a specific user mapping.
    /// </summary>
    public List<UserSyncItem> GetUserSyncItemsByUserMapping(string sourceUserId, string localUserId)
    {
        return ExecuteRead(() =>
        {
            var items = new List<UserSyncItem>();
            using var command = _connection!.CreateCommand();
            command.CommandText = "SELECT * FROM UserSyncItems WHERE SourceUserId = @sourceUserId AND LocalUserId = @localUserId ORDER BY PropertyCategory";
            command.Parameters.AddWithValue("@sourceUserId", sourceUserId);
            command.Parameters.AddWithValue("@localUserId", localUserId);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                items.Add(ReadUserSyncItem(reader));
            }

            return items;
        }, fallbackValue: new List<UserSyncItem>());
    }

    /// <summary>
    /// Gets all user sync items.
    /// </summary>
    public List<UserSyncItem> GetAllUserSyncItems()
    {
        return ExecuteRead(() =>
        {
            var items = new List<UserSyncItem>();
            using var command = _connection!.CreateCommand();
            command.CommandText = "SELECT * FROM UserSyncItems ORDER BY SourceUserName, LocalUserName";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                items.Add(ReadUserSyncItem(reader));
            }

            return items;
        }, fallbackValue: new List<UserSyncItem>());
    }

    /// <summary>
    /// Gets all user sync items with a specific status.
    /// </summary>
    public List<UserSyncItem> GetUserSyncItemsByStatus(BaseSyncStatus status)
    {
        return ExecuteRead(() =>
        {
            var items = new List<UserSyncItem>();
            using var command = _connection!.CreateCommand();
            command.CommandText = "SELECT * FROM UserSyncItems WHERE Status = @status";
            command.Parameters.AddWithValue("@status", (int)status);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                items.Add(ReadUserSyncItem(reader));
            }

            return items;
        }, fallbackValue: new List<UserSyncItem>());
    }

    /// <summary>
    /// Searches user sync items with pagination support.
    /// </summary>
    public (List<UserSyncItem> Items, int TotalCount) SearchUserSyncItemsPaginated(
        string? searchTerm = null,
        BaseSyncStatus? status = null,
        string? sourceUserId = null,
        string? propertyCategory = null,
        int skip = 0,
        int take = 50)
    {
        return ExecuteRead(() =>
        {
            var conditions = new List<string>();

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            conditions.Add("(SourceUserName LIKE @searchTerm OR LocalUserName LIKE @searchTerm)");
        }

        if (status.HasValue)
        {
            conditions.Add("Status = @status");
        }

        if (!string.IsNullOrWhiteSpace(sourceUserId))
        {
            conditions.Add("SourceUserId = @sourceUserId");
        }

        if (!string.IsNullOrWhiteSpace(propertyCategory))
        {
            conditions.Add("PropertyCategory = @propertyCategory");
        }

        var whereClause = conditions.Count > 0
            ? $"WHERE {string.Join(" AND ", conditions)}"
            : string.Empty;

        // Get total count
        int totalCount;
        using (var countCommand = _connection!.CreateCommand())
        {
            countCommand.CommandText = $"SELECT COUNT(*) FROM UserSyncItems {whereClause}";

            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                countCommand.Parameters.AddWithValue("@searchTerm", $"%{searchTerm}%");
            }

            if (status.HasValue)
            {
                countCommand.Parameters.AddWithValue("@status", (int)status.Value);
            }

            if (!string.IsNullOrWhiteSpace(sourceUserId))
            {
                countCommand.Parameters.AddWithValue("@sourceUserId", sourceUserId);
            }

            if (!string.IsNullOrWhiteSpace(propertyCategory))
            {
                countCommand.Parameters.AddWithValue("@propertyCategory", propertyCategory);
            }

            totalCount = Convert.ToInt32(countCommand.ExecuteScalar());
        }

        // Get paginated data
        var items = new List<UserSyncItem>();
        using (var dataCommand = _connection!.CreateCommand())
        {
            dataCommand.CommandText = $@"
                SELECT * FROM UserSyncItems
                {whereClause}
                ORDER BY
                    CASE Status
                        WHEN 3 THEN 0  -- Errored first
                        WHEN 1 THEN 1  -- Queued second
                        WHEN 4 THEN 2  -- Ignored third
                        WHEN 2 THEN 3  -- Synced last
                        ELSE 4
                    END,
                    SourceUserName ASC,
                    LocalUserName ASC,
                    PropertyCategory ASC
                LIMIT @take OFFSET @skip";

            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                dataCommand.Parameters.AddWithValue("@searchTerm", $"%{searchTerm}%");
            }

            if (status.HasValue)
            {
                dataCommand.Parameters.AddWithValue("@status", (int)status.Value);
            }

            if (!string.IsNullOrWhiteSpace(sourceUserId))
            {
                dataCommand.Parameters.AddWithValue("@sourceUserId", sourceUserId);
            }

            if (!string.IsNullOrWhiteSpace(propertyCategory))
            {
                dataCommand.Parameters.AddWithValue("@propertyCategory", propertyCategory);
            }

            dataCommand.Parameters.AddWithValue("@take", take);
            dataCommand.Parameters.AddWithValue("@skip", skip);

            using var reader = dataCommand.ExecuteReader();
            while (reader.Read())
            {
                items.Add(ReadUserSyncItem(reader));
            }
        }

            return (items, totalCount);
        }, fallbackValue: (new List<UserSyncItem>(), 0));
    }

    /// <summary>
    /// Gets user sync status counts (counting unique user mappings, not individual records).
    /// Uses the "worst" status for each user mapping: Errored > Queued > Ignored > Synced.
    /// </summary>
    public Dictionary<BaseSyncStatus, int> GetUserSyncStatusCounts()
    {
        return ExecuteRead(() =>
        {
            var counts = new Dictionary<BaseSyncStatus, int>();
            using var command = _connection!.CreateCommand();

        // For each unique user mapping (SourceUserId, LocalUserId), determine overall status:
        // - If ANY record is Errored -> user is Errored
        // - Else if ANY record is Queued -> user is Queued
        // - Else if ALL records are Ignored -> user is Ignored
        // - Else user is Synced
        command.CommandText = @"
            WITH UserStatus AS (
                SELECT
                    SourceUserId,
                    LocalUserId,
                    CASE
                        WHEN MAX(CASE WHEN Status = 3 THEN 1 ELSE 0 END) = 1 THEN 3  -- Errored
                        WHEN MAX(CASE WHEN Status = 1 THEN 1 ELSE 0 END) = 1 THEN 1  -- Queued
                        WHEN MIN(CASE WHEN Status = 2 THEN 1 ELSE 0 END) = 1 THEN 2  -- All Ignored
                        ELSE 0  -- Synced
                    END AS OverallStatus
                FROM UserSyncItems
                GROUP BY SourceUserId, LocalUserId
            )
            SELECT OverallStatus, COUNT(*) as Count
            FROM UserStatus
            GROUP BY OverallStatus";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var syncStatus = (BaseSyncStatus)reader.GetInt32(0);
            var count = reader.GetInt32(1);
            counts[syncStatus] = count;
        }

            return counts;
        }, fallbackValue: new Dictionary<BaseSyncStatus, int>());
    }

    /// <summary>
    /// Upserts a user sync item.
    /// </summary>
    public void UpsertUserSyncItem(UserSyncItem item, SqliteTransaction? transaction = null)
    {
        ThrowIfDisposed();
        lock (_writeLock)
        {
            EnsureConnection();

            using var command = _connection!.CreateCommand();
            command.Transaction = transaction;

            // Insert or update user sync item (one record per property category per user mapping)
            // Preserve Ignored status - don't overwrite if user explicitly ignored the item
            // Preserve SyncedImageHash/Size when updating (unless explicitly provided)
            command.CommandText = @"
                INSERT INTO UserSyncItems (
                    SourceUserId, LocalUserId, SourceUserName, LocalUserName,
                    PropertyCategory, SourceValue, LocalValue, MergedValue,
                    SourceImageHash, LocalImageHash, SyncedImageHash,
                    SourceImageSize, LocalImageSize, SyncedImageSize,
                    Status, StatusDate, LastSyncTime, ErrorMessage
                ) VALUES (
                    @sourceUserId, @localUserId, @sourceUserName, @localUserName,
                    @propertyCategory, @sourceValue, @localValue, @mergedValue,
                    @sourceImageHash, @localImageHash, @syncedImageHash,
                    @sourceImageSize, @localImageSize, @syncedImageSize,
                    @status, @statusDate, @lastSyncTime, @errorMessage
                )
                ON CONFLICT(SourceUserId, LocalUserId, PropertyCategory) DO UPDATE SET
                    SourceUserName = @sourceUserName,
                    LocalUserName = @localUserName,
                    SourceValue = @sourceValue,
                    LocalValue = @localValue,
                    MergedValue = @mergedValue,
                    SourceImageHash = @sourceImageHash,
                    LocalImageHash = @localImageHash,
                    SyncedImageHash = CASE
                        WHEN @syncedImageHash IS NOT NULL THEN @syncedImageHash
                        ELSE UserSyncItems.SyncedImageHash
                    END,
                    SourceImageSize = @sourceImageSize,
                    LocalImageSize = @localImageSize,
                    SyncedImageSize = CASE
                        WHEN @syncedImageSize IS NOT NULL THEN @syncedImageSize
                        ELSE UserSyncItems.SyncedImageSize
                    END,
                    Status = CASE
                        WHEN UserSyncItems.Status = @ignoredStatus THEN @ignoredStatus
                        WHEN UserSyncItems.Status = @syncedStatus AND @sourceValue = UserSyncItems.LocalValue THEN @syncedStatus
                        ELSE @status
                    END,
                    StatusDate = CASE
                        WHEN UserSyncItems.Status = @ignoredStatus THEN UserSyncItems.StatusDate
                        WHEN UserSyncItems.Status = @syncedStatus AND @sourceValue = UserSyncItems.LocalValue THEN UserSyncItems.StatusDate
                        ELSE @statusDate
                    END,
                    LastSyncTime = CASE
                        WHEN UserSyncItems.Status = @ignoredStatus THEN UserSyncItems.LastSyncTime
                        WHEN UserSyncItems.Status = @syncedStatus AND @sourceValue = UserSyncItems.LocalValue THEN UserSyncItems.LastSyncTime
                        ELSE @lastSyncTime
                    END,
                    ErrorMessage = CASE
                        WHEN UserSyncItems.Status = @ignoredStatus THEN UserSyncItems.ErrorMessage
                        ELSE @errorMessage
                    END
            ";

            command.Parameters.AddWithValue("@sourceUserId", item.SourceUserId);
            command.Parameters.AddWithValue("@localUserId", item.LocalUserId);
            command.Parameters.AddWithValue("@sourceUserName", item.SourceUserName ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@localUserName", item.LocalUserName ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@propertyCategory", item.PropertyCategory);
            command.Parameters.AddWithValue("@sourceValue", item.SourceValue ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@localValue", item.LocalValue ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@mergedValue", item.MergedValue ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@sourceImageHash", item.SourceImageHash ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@localImageHash", item.LocalImageHash ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@syncedImageHash", item.SyncedImageHash ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@sourceImageSize", item.SourceImageSize ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@localImageSize", item.LocalImageSize ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@syncedImageSize", item.SyncedImageSize ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@status", (int)item.Status);
            command.Parameters.AddWithValue("@statusDate", item.StatusDate.ToString("o"));
            command.Parameters.AddWithValue("@lastSyncTime", item.LastSyncTime?.ToString("o") ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@errorMessage", item.ErrorMessage ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@ignoredStatus", (int)BaseSyncStatus.Ignored);
            command.Parameters.AddWithValue("@syncedStatus", (int)BaseSyncStatus.Synced);

            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Updates the status of a user sync item by database ID.
    /// </summary>
    public void UpdateUserSyncItemStatusById(long id, BaseSyncStatus status, string? errorMessage = null)
    {
        ThrowIfDisposed();
        lock (_writeLock)
        {
            EnsureConnection();

            using var command = _connection!.CreateCommand();

            if (status == BaseSyncStatus.Synced)
            {
                command.CommandText = @"
                    UPDATE UserSyncItems
                    SET Status = @status,
                        StatusDate = @statusDate,
                        LastSyncTime = @statusDate,
                        ErrorMessage = NULL
                    WHERE Id = @id";
            }
            else
            {
                command.CommandText = @"
                    UPDATE UserSyncItems
                    SET Status = @status,
                        StatusDate = @statusDate,
                        ErrorMessage = @errorMessage
                    WHERE Id = @id";
                command.Parameters.AddWithValue("@errorMessage", errorMessage ?? (object)DBNull.Value);
            }

            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@status", (int)status);
            command.Parameters.AddWithValue("@statusDate", DateTime.UtcNow.ToString("o"));

            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Batch updates status for multiple user sync items by their database IDs.
    /// </summary>
    public int BatchUpdateUserSyncItemStatusByIds(IEnumerable<long> ids, BaseSyncStatus status)
    {
        ThrowIfDisposed();
        lock (_writeLock)
        {
            EnsureConnection();

            var count = 0;
            using var transaction = _connection!.BeginTransaction();
            try
            {
                foreach (var id in ids)
                {
                    using var command = _connection!.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = @"
                        UPDATE UserSyncItems
                        SET Status = @status,
                            StatusDate = @statusDate
                        WHERE Id = @id";

                    command.Parameters.AddWithValue("@id", id);
                    command.Parameters.AddWithValue("@status", (int)status);
                    command.Parameters.AddWithValue("@statusDate", DateTime.UtcNow.ToString("o"));

                    count += command.ExecuteNonQuery();
                }

                transaction.Commit();
                return count;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Batch update user sync status by IDs failed after {Count} items, rolling back", count);
                transaction.Rollback();
                throw;
            }
        }
    }

    /// <summary>
    /// Deletes all user sync items for a specific user mapping.
    /// </summary>
    public int DeleteUserSyncItemsByUserMapping(string sourceUserId, string localUserId)
    {
        ThrowIfDisposed();
        lock (_writeLock)
        {
            EnsureConnection();

            using var command = _connection!.CreateCommand();
            command.CommandText = "DELETE FROM UserSyncItems WHERE SourceUserId = @sourceUserId AND LocalUserId = @localUserId";
            command.Parameters.AddWithValue("@sourceUserId", sourceUserId);
            command.Parameters.AddWithValue("@localUserId", localUserId);

            return command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Resets the user sync database by clearing all items.
    /// Falls back to a full database recreation if the database is readonly or corrupted.
    /// </summary>
    public void ResetUserSyncDatabase()
    {
        ThrowIfDisposed();
        lock (_writeLock)
        {
            _logger.LogWarning("Resetting user sync database - all user sync tracking data will be lost");

            try
            {
                EnsureConnection();
                using var command = _connection!.CreateCommand();
                command.CommandText = "DELETE FROM UserSyncItems WHERE 1=1";
                var deleted = command.ExecuteNonQuery();
                _logger.LogInformation("User sync database has been reset, {Count} items removed", deleted);
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 1)
            {
                _logger.LogInformation("User sync table does not exist yet, nothing to reset");
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 8)
            {
                _logger.LogWarning(ex, "Database is readonly, falling back to full database recreation");
                RecreateDatabase();
            }
        }
    }

    // ============================================
    // User Sync Consolidated Methods
    // ============================================

    /// <summary>
    /// Gets distinct user mappings with pagination for consolidated view.
    /// Returns aggregated data grouped by (SourceUserId, LocalUserId).
    /// </summary>
    public (List<(string SourceUserId, string LocalUserId, string? SourceUserName, string? LocalUserName, List<UserSyncItem> Items)> Users, int TotalCount) GetUserSyncUsersPaginated(
        string? searchTerm = null,
        BaseSyncStatus? status = null,
        int skip = 0,
        int take = 50)
    {
        return ExecuteRead(() =>
        {
            var conditions = new List<string>();

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            conditions.Add("(SourceUserName LIKE @searchTerm OR LocalUserName LIKE @searchTerm)");
        }

        if (status.HasValue)
        {
            conditions.Add("Status = @status");
        }

        var whereClause = conditions.Count > 0
            ? $"WHERE {string.Join(" AND ", conditions)}"
            : string.Empty;

        // For status filtering, we need to get distinct user mappings that have at least one record matching the status
        // For non-status filtering, just get distinct user mappings
        string countQuery;
        string dataQuery;

        if (status.HasValue)
        {
            // Count distinct user mappings that have at least one record with this status
            countQuery = $@"
                SELECT COUNT(DISTINCT SourceUserId || '|' || LocalUserId)
                FROM UserSyncItems
                {whereClause}";

            // Get paginated distinct user mappings
            dataQuery = $@"
                SELECT DISTINCT SourceUserId, LocalUserId
                FROM UserSyncItems
                {whereClause}
                ORDER BY MIN(SourceUserName), MIN(LocalUserName)
                LIMIT @take OFFSET @skip";
        }
        else
        {
            // Count distinct user mappings
            countQuery = $@"
                SELECT COUNT(DISTINCT SourceUserId || '|' || LocalUserId)
                FROM UserSyncItems
                {whereClause}";

            // Get paginated distinct user mappings
            dataQuery = $@"
                SELECT SourceUserId, LocalUserId, MIN(SourceUserName) as SourceUserName, MIN(LocalUserName) as LocalUserName
                FROM UserSyncItems
                {whereClause}
                GROUP BY SourceUserId, LocalUserId
                ORDER BY MIN(SourceUserName), MIN(LocalUserName)
                LIMIT @take OFFSET @skip";
        }

        // Get total count of distinct user mappings
        int totalCount;
        using (var countCommand = _connection!.CreateCommand())
        {
            countCommand.CommandText = countQuery;

            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                countCommand.Parameters.AddWithValue("@searchTerm", $"%{searchTerm}%");
            }

            if (status.HasValue)
            {
                countCommand.Parameters.AddWithValue("@status", (int)status.Value);
            }

            totalCount = Convert.ToInt32(countCommand.ExecuteScalar());
        }

        // Get distinct user mappings for this page
        var userMappings = new List<(string SourceUserId, string LocalUserId)>();
        using (var dataCommand = _connection!.CreateCommand())
        {
            dataCommand.CommandText = dataQuery;

            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                dataCommand.Parameters.AddWithValue("@searchTerm", $"%{searchTerm}%");
            }

            if (status.HasValue)
            {
                dataCommand.Parameters.AddWithValue("@status", (int)status.Value);
            }

            dataCommand.Parameters.AddWithValue("@take", take);
            dataCommand.Parameters.AddWithValue("@skip", skip);

            using var reader = dataCommand.ExecuteReader();
            while (reader.Read())
            {
                userMappings.Add((
                    reader.GetString(reader.GetOrdinal("SourceUserId")),
                    reader.GetString(reader.GetOrdinal("LocalUserId"))));
            }
        }

        // For each user mapping, get all their items
        var result = new List<(string SourceUserId, string LocalUserId, string? SourceUserName, string? LocalUserName, List<UserSyncItem> Items)>();
        foreach (var (sourceUserId, localUserId) in userMappings)
        {
            var items = GetUserSyncItemsByUserMapping(sourceUserId, localUserId);
            var sourceUserName = items.FirstOrDefault()?.SourceUserName;
            var localUserName = items.FirstOrDefault()?.LocalUserName;
            result.Add((sourceUserId, localUserId, sourceUserName, localUserName, items));
        }

            return (result, totalCount);
        }, fallbackValue: (new List<(string SourceUserId, string LocalUserId, string? SourceUserName, string? LocalUserName, List<UserSyncItem> Items)>(), 0));
    }

    /// <summary>
    /// Gets all user sync items for a specific user mapping.
    /// Used for the consolidated modal view.
    /// </summary>
    public List<UserSyncItem> GetUserSyncUserDetail(string sourceUserId, string localUserId)
    {
        return GetUserSyncItemsByUserMapping(sourceUserId, localUserId);
    }

    /// <summary>
    /// Batch updates status for all categories of a user mapping.
    /// </summary>
    public int BatchUpdateUserSyncStatusByMapping(string sourceUserId, string localUserId, BaseSyncStatus status)
    {
        ThrowIfDisposed();
        lock (_writeLock)
        {
            EnsureConnection();

            using var command = _connection!.CreateCommand();
            command.CommandText = @"
                UPDATE UserSyncItems
                SET Status = @status,
                    StatusDate = @statusDate
                WHERE SourceUserId = @sourceUserId AND LocalUserId = @localUserId";

            command.Parameters.AddWithValue("@sourceUserId", sourceUserId);
            command.Parameters.AddWithValue("@localUserId", localUserId);
            command.Parameters.AddWithValue("@status", (int)status);
            command.Parameters.AddWithValue("@statusDate", DateTime.UtcNow.ToString("o"));

            return command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Batch updates status for multiple user mappings.
    /// </summary>
    public int BatchUpdateUserSyncStatusByMappings(IEnumerable<(string SourceUserId, string LocalUserId)> mappings, BaseSyncStatus status)
    {
        ThrowIfDisposed();
        lock (_writeLock)
        {
            EnsureConnection();

            var count = 0;
            using var transaction = _connection!.BeginTransaction();
            try
            {
                foreach (var (sourceUserId, localUserId) in mappings)
                {
                    using var command = _connection!.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = @"
                        UPDATE UserSyncItems
                        SET Status = @status,
                            StatusDate = @statusDate
                        WHERE SourceUserId = @sourceUserId AND LocalUserId = @localUserId";

                    command.Parameters.AddWithValue("@sourceUserId", sourceUserId);
                    command.Parameters.AddWithValue("@localUserId", localUserId);
                    command.Parameters.AddWithValue("@status", (int)status);
                    command.Parameters.AddWithValue("@statusDate", DateTime.UtcNow.ToString("o"));

                    count += command.ExecuteNonQuery();
                }

                transaction.Commit();
                return count;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Batch update user sync status by mappings failed after {Count} items, rolling back", count);
                transaction.Rollback();
                throw;
            }
        }
    }

    /// <summary>
    /// Reads a UserSyncItem from the database reader.
    /// </summary>
    private static UserSyncItem ReadUserSyncItem(SqliteDataReader reader)
    {
        var item = new UserSyncItem
        {
            Id = reader.GetInt64(reader.GetOrdinal("Id")),
            SourceUserId = reader.GetString(reader.GetOrdinal("SourceUserId")),
            LocalUserId = reader.GetString(reader.GetOrdinal("LocalUserId")),
            PropertyCategory = reader.GetString(reader.GetOrdinal("PropertyCategory")),
            Status = (BaseSyncStatus)reader.GetInt32(reader.GetOrdinal("Status")),
            StatusDate = ParseDateTimeSafe(reader.GetString(reader.GetOrdinal("StatusDate")))
        };

        // Optional string fields
        var sourceUserNameOrdinal = reader.GetOrdinal("SourceUserName");
        if (!reader.IsDBNull(sourceUserNameOrdinal))
        {
            item.SourceUserName = reader.GetString(sourceUserNameOrdinal);
        }

        var localUserNameOrdinal = reader.GetOrdinal("LocalUserName");
        if (!reader.IsDBNull(localUserNameOrdinal))
        {
            item.LocalUserName = reader.GetString(localUserNameOrdinal);
        }

        var sourceValueOrdinal = reader.GetOrdinal("SourceValue");
        if (!reader.IsDBNull(sourceValueOrdinal))
        {
            item.SourceValue = reader.GetString(sourceValueOrdinal);
        }

        var localValueOrdinal = reader.GetOrdinal("LocalValue");
        if (!reader.IsDBNull(localValueOrdinal))
        {
            item.LocalValue = reader.GetString(localValueOrdinal);
        }

        var mergedValueOrdinal = reader.GetOrdinal("MergedValue");
        if (!reader.IsDBNull(mergedValueOrdinal))
        {
            item.MergedValue = reader.GetString(mergedValueOrdinal);
        }

        // Image hash fields
        var sourceImageHashOrdinal = TryGetOrdinal(reader, "SourceImageHash");
        if (sourceImageHashOrdinal >= 0 && !reader.IsDBNull(sourceImageHashOrdinal))
        {
            item.SourceImageHash = reader.GetString(sourceImageHashOrdinal);
        }

        var localImageHashOrdinal = TryGetOrdinal(reader, "LocalImageHash");
        if (localImageHashOrdinal >= 0 && !reader.IsDBNull(localImageHashOrdinal))
        {
            item.LocalImageHash = reader.GetString(localImageHashOrdinal);
        }

        var syncedImageHashOrdinal = TryGetOrdinal(reader, "SyncedImageHash");
        if (syncedImageHashOrdinal >= 0 && !reader.IsDBNull(syncedImageHashOrdinal))
        {
            item.SyncedImageHash = reader.GetString(syncedImageHashOrdinal);
        }

        // Image size fields (legacy, kept for backward compatibility)
        var sourceImageSizeOrdinal = reader.GetOrdinal("SourceImageSize");
        if (!reader.IsDBNull(sourceImageSizeOrdinal))
        {
            item.SourceImageSize = reader.GetInt64(sourceImageSizeOrdinal);
        }

        var localImageSizeOrdinal = reader.GetOrdinal("LocalImageSize");
        if (!reader.IsDBNull(localImageSizeOrdinal))
        {
            item.LocalImageSize = reader.GetInt64(localImageSizeOrdinal);
        }

        var syncedImageSizeOrdinal = reader.GetOrdinal("SyncedImageSize");
        if (!reader.IsDBNull(syncedImageSizeOrdinal))
        {
            item.SyncedImageSize = reader.GetInt64(syncedImageSizeOrdinal);
        }

        var lastSyncTimeOrdinal = reader.GetOrdinal("LastSyncTime");
        if (!reader.IsDBNull(lastSyncTimeOrdinal))
        {
            item.LastSyncTime = ParseDateTimeSafe(reader.GetString(lastSyncTimeOrdinal));
        }

        var errorMessageOrdinal = reader.GetOrdinal("ErrorMessage");
        if (!reader.IsDBNull(errorMessageOrdinal))
        {
            item.ErrorMessage = reader.GetString(errorMessageOrdinal);
        }

        return item;
    }

    // ============================================
    // Metadata Sync Methods
    // ============================================

    /// <summary>
    /// Gets a metadata sync item by its ID.
    /// </summary>
    public MetadataSyncItem? GetMetadataSyncItemById(long id)
    {
        return ExecuteRead(() =>
        {
            using var command = _connection!.CreateCommand();
            command.CommandText = "SELECT * FROM MetadataSyncItems WHERE Id = @id";
            command.Parameters.AddWithValue("@id", id);

            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadMetadataSyncItem(reader) : null;
        }, fallbackValue: null);
    }

    /// <summary>
    /// Gets a metadata sync item by source library ID and source item ID.
    /// </summary>
    public MetadataSyncItem? GetMetadataSyncItem(string sourceLibraryId, string sourceItemId)
    {
        return ExecuteRead(() =>
        {
            using var command = _connection!.CreateCommand();
            command.CommandText = @"
                SELECT * FROM MetadataSyncItems
                WHERE SourceLibraryId = @sourceLibraryId
                  AND SourceItemId = @sourceItemId";
            command.Parameters.AddWithValue("@sourceLibraryId", sourceLibraryId);
            command.Parameters.AddWithValue("@sourceItemId", sourceItemId);

            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadMetadataSyncItem(reader) : null;
        }, fallbackValue: null);
    }

    /// <summary>
    /// Gets a metadata sync item for a specific source item (one record per item).
    /// </summary>
    public MetadataSyncItem? GetMetadataSyncItemBySourceItem(string sourceItemId)
    {
        return ExecuteRead(() =>
        {
            using var command = _connection!.CreateCommand();
            command.CommandText = "SELECT * FROM MetadataSyncItems WHERE SourceItemId = @sourceItemId";
            command.Parameters.AddWithValue("@sourceItemId", sourceItemId);

            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadMetadataSyncItem(reader) : null;
        }, fallbackValue: null);
    }

    /// <summary>
    /// Gets all metadata sync items for a specific library.
    /// </summary>
    public List<MetadataSyncItem> GetMetadataSyncItemsByLibrary(string sourceLibraryId)
    {
        return ExecuteRead(() =>
        {
            var items = new List<MetadataSyncItem>();
            using var command = _connection!.CreateCommand();
            command.CommandText = "SELECT * FROM MetadataSyncItems WHERE SourceLibraryId = @sourceLibraryId";
            command.Parameters.AddWithValue("@sourceLibraryId", sourceLibraryId);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                items.Add(ReadMetadataSyncItem(reader));
            }

            return items;
        }, fallbackValue: new List<MetadataSyncItem>());
    }

    /// <summary>
    /// Gets all metadata sync items with a specific status.
    /// </summary>
    public List<MetadataSyncItem> GetMetadataSyncItemsByStatus(BaseSyncStatus status)
    {
        return ExecuteRead(() =>
        {
            var items = new List<MetadataSyncItem>();
            using var command = _connection!.CreateCommand();
            command.CommandText = "SELECT * FROM MetadataSyncItems WHERE Status = @status";
            command.Parameters.AddWithValue("@status", (int)status);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                items.Add(ReadMetadataSyncItem(reader));
            }

            return items;
        }, fallbackValue: new List<MetadataSyncItem>());
    }

    /// <summary>
    /// Searches metadata sync items with pagination support.
    /// </summary>
    public (List<MetadataSyncItem> Items, int TotalCount) SearchMetadataSyncItemsPaginated(
        string? searchTerm = null,
        BaseSyncStatus? status = null,
        string? sourceLibraryId = null,
        int skip = 0,
        int take = 50)
    {
        return ExecuteRead(() =>
        {
            var conditions = new List<string>();

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            conditions.Add("(ItemName LIKE @searchTerm OR SourcePath LIKE @searchTerm)");
        }

        if (status.HasValue)
        {
            conditions.Add("Status = @status");
        }

        if (!string.IsNullOrWhiteSpace(sourceLibraryId))
        {
            conditions.Add("SourceLibraryId = @sourceLibraryId");
        }

        var whereClause = conditions.Count > 0
            ? $"WHERE {string.Join(" AND ", conditions)}"
            : string.Empty;

        // Get total count
        int totalCount;
        using (var countCommand = _connection!.CreateCommand())
        {
            countCommand.CommandText = $"SELECT COUNT(*) FROM MetadataSyncItems {whereClause}";

            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                countCommand.Parameters.AddWithValue("@searchTerm", $"%{searchTerm}%");
            }

            if (status.HasValue)
            {
                countCommand.Parameters.AddWithValue("@status", (int)status.Value);
            }

            if (!string.IsNullOrWhiteSpace(sourceLibraryId))
            {
                countCommand.Parameters.AddWithValue("@sourceLibraryId", sourceLibraryId);
            }

            totalCount = Convert.ToInt32(countCommand.ExecuteScalar());
        }

        // Get paginated data
        var items = new List<MetadataSyncItem>();
        using (var dataCommand = _connection!.CreateCommand())
        {
            dataCommand.CommandText = $@"
                SELECT * FROM MetadataSyncItems
                {whereClause}
                ORDER BY
                    CASE Status
                        WHEN 3 THEN 0  -- Errored first
                        WHEN 1 THEN 1  -- Queued second
                        WHEN 4 THEN 2  -- Ignored third
                        WHEN 2 THEN 3  -- Synced last
                        ELSE 4
                    END,
                    ItemName ASC
                LIMIT @take OFFSET @skip";

            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                dataCommand.Parameters.AddWithValue("@searchTerm", $"%{searchTerm}%");
            }

            if (status.HasValue)
            {
                dataCommand.Parameters.AddWithValue("@status", (int)status.Value);
            }

            if (!string.IsNullOrWhiteSpace(sourceLibraryId))
            {
                dataCommand.Parameters.AddWithValue("@sourceLibraryId", sourceLibraryId);
            }

            dataCommand.Parameters.AddWithValue("@take", take);
            dataCommand.Parameters.AddWithValue("@skip", skip);

            using var reader = dataCommand.ExecuteReader();
            while (reader.Read())
            {
                items.Add(ReadMetadataSyncItem(reader));
            }
        }

            return (items, totalCount);
        }, fallbackValue: (new List<MetadataSyncItem>(), 0));
    }

    /// <summary>
    /// Gets metadata sync status counts.
    /// </summary>
    public Dictionary<BaseSyncStatus, int> GetMetadataSyncStatusCounts()
    {
        return ExecuteRead(() =>
        {
            var counts = new Dictionary<BaseSyncStatus, int>();
            using var command = _connection!.CreateCommand();
            command.CommandText = "SELECT Status, COUNT(*) as Count FROM MetadataSyncItems GROUP BY Status";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var syncStatus = (BaseSyncStatus)reader.GetInt32(0);
                var count = reader.GetInt32(1);
                counts[syncStatus] = count;
            }

            return counts;
        }, fallbackValue: new Dictionary<BaseSyncStatus, int>());
    }

    /// <summary>
    /// Upserts a metadata sync item.
    /// </summary>
    public void UpsertMetadataSyncItem(MetadataSyncItem item, SqliteTransaction? transaction = null)
    {
        ThrowIfDisposed();
        lock (_writeLock)
        {
            EnsureConnection();

            using var command = _connection!.CreateCommand();
            command.Transaction = transaction;

            // Insert or update metadata sync item (one record per item with all categories)
            // Preserve Ignored status - don't overwrite if user explicitly ignored the item
            command.CommandText = @"
                INSERT INTO MetadataSyncItems (
                    SourceLibraryId, LocalLibraryId, SourceItemId, LocalItemId,
                    ItemName, SourcePath, LocalPath,
                    SourceMetadataValue, LocalMetadataValue,
                    SourceImagesValue, LocalImagesValue, SourceImagesHash, SyncedImagesHash,
                    SourcePeopleValue, LocalPeopleValue,
                    SourceStudiosValue, LocalStudiosValue,
                    Status, StatusDate, LastSyncTime, ErrorMessage, SourceETag
                ) VALUES (
                    @sourceLibraryId, @localLibraryId, @sourceItemId, @localItemId,
                    @itemName, @sourcePath, @localPath,
                    @sourceMetadataValue, @localMetadataValue,
                    @sourceImagesValue, @localImagesValue, @sourceImagesHash, @syncedImagesHash,
                    @sourcePeopleValue, @localPeopleValue,
                    @sourceStudiosValue, @localStudiosValue,
                    @status, @statusDate, @lastSyncTime, @errorMessage, @sourceETag
                )
                ON CONFLICT(SourceLibraryId, SourceItemId) DO UPDATE SET
                    LocalLibraryId = @localLibraryId,
                    LocalItemId = @localItemId,
                    ItemName = @itemName,
                    SourcePath = @sourcePath,
                    LocalPath = @localPath,
                    SourceMetadataValue = @sourceMetadataValue,
                    LocalMetadataValue = @localMetadataValue,
                    SourceImagesValue = @sourceImagesValue,
                    LocalImagesValue = @localImagesValue,
                    SourceImagesHash = @sourceImagesHash,
                    SyncedImagesHash = CASE
                        WHEN @syncedImagesHash IS NOT NULL THEN @syncedImagesHash
                        ELSE MetadataSyncItems.SyncedImagesHash
                    END,
                    SourcePeopleValue = @sourcePeopleValue,
                    LocalPeopleValue = @localPeopleValue,
                    SourceStudiosValue = @sourceStudiosValue,
                    LocalStudiosValue = @localStudiosValue,
                    Status = CASE
                        WHEN MetadataSyncItems.Status = @ignoredStatus THEN @ignoredStatus
                        ELSE @status
                    END,
                    StatusDate = CASE
                        WHEN MetadataSyncItems.Status = @ignoredStatus THEN MetadataSyncItems.StatusDate
                        ELSE @statusDate
                    END,
                    LastSyncTime = CASE
                        WHEN MetadataSyncItems.Status = @ignoredStatus THEN MetadataSyncItems.LastSyncTime
                        ELSE @lastSyncTime
                    END,
                    ErrorMessage = CASE
                        WHEN MetadataSyncItems.Status = @ignoredStatus THEN MetadataSyncItems.ErrorMessage
                        ELSE @errorMessage
                    END,
                    SourceETag = @sourceETag
            ";

            command.Parameters.AddWithValue("@sourceLibraryId", item.SourceLibraryId);
            command.Parameters.AddWithValue("@localLibraryId", item.LocalLibraryId);
            command.Parameters.AddWithValue("@sourceItemId", item.SourceItemId);
            command.Parameters.AddWithValue("@localItemId", item.LocalItemId ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@itemName", item.ItemName ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@sourcePath", item.SourcePath ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@localPath", item.LocalPath ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@sourceMetadataValue", item.SourceMetadataValue ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@localMetadataValue", item.LocalMetadataValue ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@sourceImagesValue", item.SourceImagesValue ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@localImagesValue", item.LocalImagesValue ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@sourceImagesHash", item.SourceImagesHash ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@syncedImagesHash", item.SyncedImagesHash ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@sourcePeopleValue", item.SourcePeopleValue ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@localPeopleValue", item.LocalPeopleValue ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@sourceStudiosValue", item.SourceStudiosValue ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@localStudiosValue", item.LocalStudiosValue ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@status", (int)item.Status);
            command.Parameters.AddWithValue("@statusDate", item.StatusDate.ToString("o"));
            command.Parameters.AddWithValue("@lastSyncTime", item.LastSyncTime?.ToString("o") ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@errorMessage", item.ErrorMessage ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@ignoredStatus", (int)BaseSyncStatus.Ignored);
            command.Parameters.AddWithValue("@sourceETag", item.SourceETag ?? (object)DBNull.Value);

            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Updates the status of a metadata sync item by database ID.
    /// </summary>
    public void UpdateMetadataSyncItemStatusById(long id, BaseSyncStatus status, string? errorMessage = null)
    {
        ThrowIfDisposed();
        lock (_writeLock)
        {
            EnsureConnection();

            using var command = _connection!.CreateCommand();

            if (status == BaseSyncStatus.Synced)
            {
                command.CommandText = @"
                    UPDATE MetadataSyncItems
                    SET Status = @status,
                        StatusDate = @statusDate,
                        LastSyncTime = @statusDate,
                        ErrorMessage = NULL
                    WHERE Id = @id";
            }
            else
            {
                command.CommandText = @"
                    UPDATE MetadataSyncItems
                    SET Status = @status,
                        StatusDate = @statusDate,
                        ErrorMessage = @errorMessage
                    WHERE Id = @id";
                command.Parameters.AddWithValue("@errorMessage", errorMessage ?? (object)DBNull.Value);
            }

            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@status", (int)status);
            command.Parameters.AddWithValue("@statusDate", DateTime.UtcNow.ToString("o"));

            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Batch updates status for multiple metadata sync items by their database IDs.
    /// </summary>
    public int BatchUpdateMetadataSyncItemStatusByIds(IEnumerable<long> ids, BaseSyncStatus status)
    {
        ThrowIfDisposed();
        lock (_writeLock)
        {
            EnsureConnection();

            var count = 0;
            using var transaction = _connection!.BeginTransaction();
            try
            {
                foreach (var id in ids)
                {
                    using var command = _connection!.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = @"
                        UPDATE MetadataSyncItems
                        SET Status = @status,
                            StatusDate = @statusDate
                        WHERE Id = @id";

                    command.Parameters.AddWithValue("@id", id);
                    command.Parameters.AddWithValue("@status", (int)status);
                    command.Parameters.AddWithValue("@statusDate", DateTime.UtcNow.ToString("o"));

                    count += command.ExecuteNonQuery();
                }

                transaction.Commit();
                return count;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Batch update metadata sync status by IDs failed after {Count} items, rolling back", count);
                transaction.Rollback();
                throw;
            }
        }
    }

    /// <summary>
    /// Deletes all metadata sync items for a specific source item.
    /// </summary>
    public int DeleteMetadataSyncItemsBySourceItem(string sourceItemId)
    {
        ThrowIfDisposed();
        lock (_writeLock)
        {
            EnsureConnection();

            using var command = _connection!.CreateCommand();
            command.CommandText = "DELETE FROM MetadataSyncItems WHERE SourceItemId = @sourceItemId";
            command.Parameters.AddWithValue("@sourceItemId", sourceItemId);

            return command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Resets the metadata sync database by clearing all items.
    /// Falls back to a full database recreation if the database is readonly or corrupted.
    /// </summary>
    public void ResetMetadataSyncDatabase()
    {
        ThrowIfDisposed();
        lock (_writeLock)
        {
            _logger.LogWarning("Resetting metadata sync database - all metadata sync tracking data will be lost");

            try
            {
                EnsureConnection();
                using var command = _connection!.CreateCommand();
                command.CommandText = "DELETE FROM MetadataSyncItems WHERE 1=1";
                var deleted = command.ExecuteNonQuery();
                _logger.LogInformation("Metadata sync database has been reset, {Count} items removed", deleted);
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 1)
            {
                _logger.LogInformation("Metadata sync table does not exist yet, nothing to reset");
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 8)
            {
                _logger.LogWarning(ex, "Database is readonly, falling back to full database recreation");
                RecreateDatabase();
            }
        }
    }

    /// <summary>
    /// Reads a MetadataSyncItem from the database reader.
    /// </summary>
    private static MetadataSyncItem ReadMetadataSyncItem(SqliteDataReader reader)
    {
        var item = new MetadataSyncItem
        {
            Id = reader.GetInt64(reader.GetOrdinal("Id")),
            SourceLibraryId = reader.GetString(reader.GetOrdinal("SourceLibraryId")),
            LocalLibraryId = reader.GetString(reader.GetOrdinal("LocalLibraryId")),
            SourceItemId = reader.GetString(reader.GetOrdinal("SourceItemId")),
            Status = (BaseSyncStatus)reader.GetInt32(reader.GetOrdinal("Status")),
            StatusDate = ParseDateTimeSafe(reader.GetString(reader.GetOrdinal("StatusDate")))
        };

        // Optional string fields
        var localItemIdOrdinal = reader.GetOrdinal("LocalItemId");
        if (!reader.IsDBNull(localItemIdOrdinal))
        {
            item.LocalItemId = reader.GetString(localItemIdOrdinal);
        }

        var itemNameOrdinal = reader.GetOrdinal("ItemName");
        if (!reader.IsDBNull(itemNameOrdinal))
        {
            item.ItemName = reader.GetString(itemNameOrdinal);
        }

        var sourcePathOrdinal = reader.GetOrdinal("SourcePath");
        if (!reader.IsDBNull(sourcePathOrdinal))
        {
            item.SourcePath = reader.GetString(sourcePathOrdinal);
        }

        var localPathOrdinal = reader.GetOrdinal("LocalPath");
        if (!reader.IsDBNull(localPathOrdinal))
        {
            item.LocalPath = reader.GetString(localPathOrdinal);
        }

        // Metadata category fields
        var sourceMetadataOrdinal = TryGetOrdinal(reader, "SourceMetadataValue");
        if (sourceMetadataOrdinal >= 0 && !reader.IsDBNull(sourceMetadataOrdinal))
        {
            item.SourceMetadataValue = reader.GetString(sourceMetadataOrdinal);
        }

        var localMetadataOrdinal = TryGetOrdinal(reader, "LocalMetadataValue");
        if (localMetadataOrdinal >= 0 && !reader.IsDBNull(localMetadataOrdinal))
        {
            item.LocalMetadataValue = reader.GetString(localMetadataOrdinal);
        }

        // Images category fields
        var sourceImagesOrdinal = TryGetOrdinal(reader, "SourceImagesValue");
        if (sourceImagesOrdinal >= 0 && !reader.IsDBNull(sourceImagesOrdinal))
        {
            item.SourceImagesValue = reader.GetString(sourceImagesOrdinal);
        }

        var localImagesOrdinal = TryGetOrdinal(reader, "LocalImagesValue");
        if (localImagesOrdinal >= 0 && !reader.IsDBNull(localImagesOrdinal))
        {
            item.LocalImagesValue = reader.GetString(localImagesOrdinal);
        }

        var sourceImagesHashOrdinal = TryGetOrdinal(reader, "SourceImagesHash");
        if (sourceImagesHashOrdinal >= 0 && !reader.IsDBNull(sourceImagesHashOrdinal))
        {
            item.SourceImagesHash = reader.GetString(sourceImagesHashOrdinal);
        }

        var syncedImagesHashOrdinal = TryGetOrdinal(reader, "SyncedImagesHash");
        if (syncedImagesHashOrdinal >= 0 && !reader.IsDBNull(syncedImagesHashOrdinal))
        {
            item.SyncedImagesHash = reader.GetString(syncedImagesHashOrdinal);
        }

        // People category fields
        var sourcePeopleOrdinal = TryGetOrdinal(reader, "SourcePeopleValue");
        if (sourcePeopleOrdinal >= 0 && !reader.IsDBNull(sourcePeopleOrdinal))
        {
            item.SourcePeopleValue = reader.GetString(sourcePeopleOrdinal);
        }

        var localPeopleOrdinal = TryGetOrdinal(reader, "LocalPeopleValue");
        if (localPeopleOrdinal >= 0 && !reader.IsDBNull(localPeopleOrdinal))
        {
            item.LocalPeopleValue = reader.GetString(localPeopleOrdinal);
        }

        // Studios category fields
        var sourceStudiosOrdinal = TryGetOrdinal(reader, "SourceStudiosValue");
        if (sourceStudiosOrdinal >= 0 && !reader.IsDBNull(sourceStudiosOrdinal))
        {
            item.SourceStudiosValue = reader.GetString(sourceStudiosOrdinal);
        }

        var localStudiosOrdinal = TryGetOrdinal(reader, "LocalStudiosValue");
        if (localStudiosOrdinal >= 0 && !reader.IsDBNull(localStudiosOrdinal))
        {
            item.LocalStudiosValue = reader.GetString(localStudiosOrdinal);
        }

        // ETag for change detection
        var sourceETagOrdinal = TryGetOrdinal(reader, "SourceETag");
        if (sourceETagOrdinal >= 0 && !reader.IsDBNull(sourceETagOrdinal))
        {
            item.SourceETag = reader.GetString(sourceETagOrdinal);
        }

        var lastSyncTimeOrdinal = reader.GetOrdinal("LastSyncTime");
        if (!reader.IsDBNull(lastSyncTimeOrdinal))
        {
            item.LastSyncTime = ParseDateTimeSafe(reader.GetString(lastSyncTimeOrdinal));
        }

        var errorMessageOrdinal = reader.GetOrdinal("ErrorMessage");
        if (!reader.IsDBNull(errorMessageOrdinal))
        {
            item.ErrorMessage = reader.GetString(errorMessageOrdinal);
        }

        return item;
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
            lock (_writeLock)
            {
                _disposed = true; // Set first inside lock to prevent races

                try
                {
                    _connection?.Close();
                    _connection?.Dispose();
                    _connection = null;

                    // Clear the pool to ensure no stale connections remain
                    SqliteConnection.ClearAllPools();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error during database disposal");
                }
            }
        }
    }
}
