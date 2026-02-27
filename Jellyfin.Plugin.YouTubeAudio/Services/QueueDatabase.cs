using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
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
    private const int SchemaVersion = 1;

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
                INSERT INTO QueueItems (Id, Url, Title, FileName, Status, ErrorMessage, CreatedAt, UpdatedAt)
                VALUES (@Id, @Url, @Title, @FileName, @Status, @ErrorMessage, @CreatedAt, @UpdatedAt)";
            cmd.Parameters.AddWithValue("@Id", item.Id);
            cmd.Parameters.AddWithValue("@Url", item.Url);
            cmd.Parameters.AddWithValue("@Title", (object?)item.Title ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@FileName", item.FileName);
            cmd.Parameters.AddWithValue("@Status", (int)item.Status);
            cmd.Parameters.AddWithValue("@ErrorMessage", (object?)item.ErrorMessage ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@CreatedAt", item.CreatedAt);
            cmd.Parameters.AddWithValue("@UpdatedAt", item.UpdatedAt);
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
                cmd.CommandText = "SELECT Id, Url, Title, FileName, Status, ErrorMessage, CreatedAt, UpdatedAt FROM QueueItems ORDER BY CreatedAt DESC";

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
                cmd.CommandText = "SELECT Id, Url, Title, FileName, Status, ErrorMessage, CreatedAt, UpdatedAt FROM QueueItems WHERE Status = @Status ORDER BY CreatedAt ASC";
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
                cmd.CommandText = "SELECT Id, Url, Title, FileName, Status, ErrorMessage, CreatedAt, UpdatedAt FROM QueueItems WHERE Id = @Id";
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

            var currentVersion = GetSchemaVersion();

            if (currentVersion == 0)
            {
                CreateSchema();
                SetSchemaVersion(SchemaVersion);
            }

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
                UpdatedAt TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_queue_status ON QueueItems(Status);
        ";
        cmd.ExecuteNonQuery();
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

        using (var pragmaCmd = _connection.CreateCommand())
        {
            pragmaCmd.CommandText = @"
                PRAGMA journal_mode=WAL;
                PRAGMA busy_timeout=5000;
                PRAGMA synchronous=NORMAL;
            ";
            pragmaCmd.ExecuteNonQuery();
        }

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
            UpdatedAt = reader.GetString(7)
        };
    }
}
