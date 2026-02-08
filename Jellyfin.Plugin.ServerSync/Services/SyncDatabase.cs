using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Runtime.CompilerServices;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

#pragma warning disable CA2100 // SQL commands use only internal constants and safe parameterized queries

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// SyncDatabase
/// SQLite database for tracking sync items between servers.
/// </summary>
public partial class SyncDatabase : IDisposable
{
    private readonly ILogger<SyncDatabase> _logger;
    private readonly string _dbPath;
    private readonly object _writeLock = new();
    private SqliteConnection? _connection;
    private volatile bool _disposed;

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
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
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
                System.Threading.Thread.Sleep(50 * (i + 1)); // Brief backoff
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

        if (DateTime.TryParse(dateString, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var result))
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

    // ============================================
    // Shared Database Operations
    // ============================================

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
    /// ResetContentSyncTable
    /// Clears all content sync items from the database without affecting other tables.
    /// Falls back to a full database recreation if the database is readonly or corrupted.
    /// </summary>
    public void ResetContentSyncTable()
    {
        ThrowIfDisposed();
        lock (_writeLock)
        {
            _logger.LogWarning("Resetting content sync table - all content tracking data will be lost");

            try
            {
                EnsureConnection();
                using var command = _connection!.CreateCommand();
                command.CommandText = "DELETE FROM SyncItems WHERE 1=1";
                var deleted = command.ExecuteNonQuery();
                _logger.LogInformation("Content sync table has been reset, {Count} items removed", deleted);
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 1)
            {
                // Table doesn't exist - that's fine, nothing to delete
                _logger.LogInformation("Content sync table does not exist yet, nothing to reset");
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 8)
            {
                _logger.LogWarning(ex, "Database is readonly, falling back to full database recreation");
                RecreateDatabase();
            }
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
                    count += DeleteInternal(sourceItemId, transaction);
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
                using var command = _connection!.CreateCommand();
                command.Transaction = transaction;

                if (status == SyncStatus.Errored && errorMessage != null)
                {
                    command.CommandText = @"
                        UPDATE SyncItems
                        SET Status = @status, StatusDate = @statusDate, ErrorMessage = @errorMessage, RetryCount = COALESCE(RetryCount, 0) + 1
                        WHERE SourceItemId = @sourceItemId";
                    command.Parameters.Add(new SqliteParameter("@errorMessage", errorMessage));
                }
                else
                {
                    command.CommandText = @"
                        UPDATE SyncItems
                        SET Status = @status, StatusDate = @statusDate, ErrorMessage = NULL, PendingType = NULL
                        WHERE SourceItemId = @sourceItemId";
                }

                var sourceItemIdParam = new SqliteParameter("@sourceItemId", string.Empty);
                command.Parameters.Add(sourceItemIdParam);
                command.Parameters.Add(new SqliteParameter("@status", (int)status));
                var statusDateParam = new SqliteParameter("@statusDate", DateTime.UtcNow.ToString("o"));
                command.Parameters.Add(statusDateParam);

                foreach (var sourceItemId in sourceItemIds)
                {
                    sourceItemIdParam.Value = sourceItemId;
                    statusDateParam.Value = DateTime.UtcNow.ToString("o");
                    count += command.ExecuteNonQuery();
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
    // Dispose Pattern
    // ============================================

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
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error during database disposal");
                }
            }
        }
    }
}
