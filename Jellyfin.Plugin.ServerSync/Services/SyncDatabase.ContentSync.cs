using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Microsoft.Data.Sqlite;

#pragma warning disable CA2100 // SQL commands use only internal constants and safe parameterized queries

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// SyncDatabase — Content Sync methods.
/// </summary>
public partial class SyncDatabase
{
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

            // Calculate total: PendingDownload + PendingReplacement + Queued - PendingDeletion (clamped to 0)
            sizes["Total"] = Math.Max(0, sizes["PendingDownload"] + sizes["PendingReplacement"] + sizes["Queued"] - sizes["PendingDeletion"]);

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
        command.Parameters.AddWithValue("@sourceModifyDate", item.SourceCreateDate.ToString("o")); // Deprecated column: use SourceCreateDate as placeholder for NOT NULL constraint
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
        lock (_writeLock)
        {
            DeleteInternal(sourceItemId, transaction);
        }
    }

    private int DeleteInternal(string sourceItemId, SqliteTransaction? transaction)
    {
        EnsureConnection();

        using var command = _connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM SyncItems WHERE SourceItemId = @sourceItemId";
        command.Parameters.AddWithValue("@sourceItemId", sourceItemId);
        return command.ExecuteNonQuery();
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
}
