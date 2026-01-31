using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;
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
        InitializeDatabase();
    }

    /// <summary>
    /// Builds the SQLite connection string with hardened settings.
    /// </summary>
    private string BuildConnectionString()
    {
        // Use a connection string with settings for better reliability:
        // - Mode=ReadWriteCreate: Create the file if it doesn't exist
        // - Cache=Shared: Allow connection sharing within the process
        // - Pooling=True: Enable connection pooling (default)
        return $"Data Source={_dbPath};Mode=ReadWriteCreate;Cache=Shared";
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
                System.Threading.Thread.Sleep(100 * (i + 1)); // Exponential backoff
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

    /// <summary>
    /// GetBySourceItemId
    /// Retrieves a sync item by its source item ID.
    /// </summary>
    /// <param name="sourceItemId">Source item ID.</param>
    /// <returns>Sync item or null if not found.</returns>
    public SyncItem? GetBySourceItemId(string sourceItemId)
    {
        EnsureConnection();

        using var command = _connection!.CreateCommand();
        command.CommandText = "SELECT * FROM SyncItems WHERE SourceItemId = @sourceItemId";
        command.Parameters.AddWithValue("@sourceItemId", sourceItemId);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSyncItem(reader) : null;
    }

    /// <summary>
    /// GetByLocalPath
    /// Retrieves a sync item by its local path.
    /// </summary>
    /// <param name="localPath">Local file path.</param>
    /// <returns>Sync item or null if not found.</returns>
    public SyncItem? GetByLocalPath(string localPath)
    {
        EnsureConnection();

        using var command = _connection!.CreateCommand();
        command.CommandText = "SELECT * FROM SyncItems WHERE LocalPath = @localPath";
        command.Parameters.AddWithValue("@localPath", localPath);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSyncItem(reader) : null;
    }

    /// <summary>
    /// GetByStatus
    /// Retrieves all sync items with a specific status.
    /// </summary>
    /// <param name="status">Status to filter by.</param>
    /// <returns>List of matching sync items.</returns>
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

    /// <summary>
    /// GetBySourceLibrary
    /// Retrieves all sync items for a specific source library.
    /// </summary>
    /// <param name="sourceLibraryId">Source library ID.</param>
    /// <returns>List of sync items in the library.</returns>
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

    /// <summary>
    /// GetAll
    /// Retrieves all sync items from the database.
    /// </summary>
    /// <returns>List of all sync items.</returns>
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
        EnsureConnection();

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
        EnsureConnection();

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
                ORDER BY StatusDate DESC
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
    }

    /// <summary>
    /// GetPendingByType
    /// Retrieves all pending items with a specific pending type.
    /// </summary>
    /// <param name="pendingType">Type of pending operation.</param>
    /// <returns>List of pending sync items.</returns>
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

    /// <summary>
    /// GetPendingCounts
    /// Returns counts of pending items grouped by pending type.
    /// </summary>
    /// <returns>Dictionary of pending type to count.</returns>
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

    /// <summary>
    /// GetErroredItemsForRetry
    /// Gets errored items that haven't exceeded max retries.
    /// </summary>
    /// <param name="maxRetries">Maximum retry count threshold.</param>
    /// <returns>List of errored items eligible for retry.</returns>
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

    /// <summary>
    /// Upsert
    /// Inserts or updates a sync item in the database.
    /// </summary>
    /// <param name="item">Sync item to insert or update.</param>
    /// <param name="transaction">Optional transaction.</param>
    public void Upsert(SyncItem item, SqliteTransaction? transaction = null)
    {
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

    /// <summary>
    /// GetSyncStats
    /// Returns detailed sync statistics.
    /// </summary>
    /// <returns>Sync statistics object.</returns>
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

    /// <summary>
    /// ClearStaleErrors
    /// Resets error status for items older than specified days.
    /// </summary>
    /// <param name="olderThanDays">Age threshold in days.</param>
    /// <returns>Number of items reset.</returns>
    public int ClearStaleErrors(int olderThanDays)
    {
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

        // Clear the connection pool to avoid stale cached connections
        SqliteConnection.ClearAllPools();

        try
        {
            _connection = new SqliteConnection(BuildConnectionString());
            _connection.Open();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open database connection to {DbPath}", _dbPath);
            throw new InvalidOperationException($"Unable to open database connection: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// ResetDatabase
    /// Drops all data and recreates the database with the latest schema.
    /// </summary>
    public void ResetDatabase()
    {
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
    /// ExecuteInTransaction
    /// Executes multiple database operations within a transaction.
    /// </summary>
    /// <param name="action">Action to execute within transaction.</param>
    /// <returns>True if committed successfully.</returns>
    public bool ExecuteInTransaction(Action<SqliteTransaction> action)
    {
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
        EnsureConnection();

        using var command = _connection!.CreateCommand();
        command.CommandText = "SELECT * FROM HistorySyncItems WHERE SourceUserId = @sourceUserId AND SourceItemId = @sourceItemId";
        command.Parameters.AddWithValue("@sourceUserId", sourceUserId);
        command.Parameters.AddWithValue("@sourceItemId", sourceItemId);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadHistorySyncItem(reader) : null;
    }

    /// <summary>
    /// Gets all history sync items for a specific user mapping.
    /// </summary>
    public List<HistorySyncItem> GetHistoryItemsByUserMapping(string sourceUserId, string localUserId)
    {
        EnsureConnection();

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
    }

    /// <summary>
    /// Gets all history sync items with a specific status.
    /// </summary>
    public List<HistorySyncItem> GetHistoryItemsByStatus(HistorySyncStatus status)
    {
        EnsureConnection();

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
    }

    /// <summary>
    /// Searches history sync items with pagination support.
    /// </summary>
    public (List<HistorySyncItem> Items, int TotalCount) SearchHistoryItemsPaginated(
        string? searchTerm = null,
        HistorySyncStatus? status = null,
        string? sourceUserId = null,
        int skip = 0,
        int take = 50)
    {
        EnsureConnection();

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
                ORDER BY StatusDate DESC
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
    }

    /// <summary>
    /// Gets history sync status counts.
    /// </summary>
    public Dictionary<HistorySyncStatus, int> GetHistoryStatusCounts()
    {
        EnsureConnection();

        var counts = new Dictionary<HistorySyncStatus, int>();
        using var command = _connection!.CreateCommand();
        command.CommandText = "SELECT Status, COUNT(*) as Count FROM HistorySyncItems GROUP BY Status";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var status = (HistorySyncStatus)reader.GetInt32(0);
            var count = reader.GetInt32(1);
            counts[status] = count;
        }

        return counts;
    }

    /// <summary>
    /// Upserts a history sync item.
    /// </summary>
    public void UpsertHistoryItem(HistorySyncItem item, SqliteTransaction? transaction = null)
    {
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
        HistorySyncStatus status,
        string? errorMessage = null)
    {
        lock (_writeLock)
        {
            EnsureConnection();

            using var command = _connection!.CreateCommand();

            if (status == HistorySyncStatus.Synced)
            {
                command.CommandText = @"
                    UPDATE HistorySyncItems
                    SET Status = @status, StatusDate = @statusDate, LastSyncTime = @statusDate, ErrorMessage = NULL
                    WHERE SourceUserId = @sourceUserId AND SourceItemId = @sourceItemId";
            }
            else if (status == HistorySyncStatus.Errored)
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
    public int BatchUpdateHistoryItemStatus(IEnumerable<(string SourceUserId, string SourceItemId)> items, HistorySyncStatus status)
    {
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
    public void UpdateHistoryItemStatusById(long id, HistorySyncStatus status, string? errorMessage = null)
    {
        lock (_writeLock)
        {
            EnsureConnection();

            using var command = _connection!.CreateCommand();

            if (status == HistorySyncStatus.Synced)
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
    /// Batch updates status for multiple history sync items by their database IDs.
    /// </summary>
    /// <param name="ids">List of database IDs.</param>
    /// <param name="status">New status.</param>
    /// <returns>Number of items updated.</returns>
    public int BatchUpdateHistoryItemStatusByIds(IEnumerable<long> ids, HistorySyncStatus status)
    {
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
            Status = (HistorySyncStatus)reader.GetInt32(reader.GetOrdinal("Status")),
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

            _disposed = true;
        }
    }
}
