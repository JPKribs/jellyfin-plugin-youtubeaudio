using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

#pragma warning disable CA2100 // SQL commands use only internal constants and safe parameterized queries

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// SyncDatabase — History Sync methods.
/// </summary>
public partial class SyncDatabase
{
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
                using var command = _connection!.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
                    UPDATE HistorySyncItems
                    SET Status = @status, StatusDate = @statusDate, ErrorMessage = NULL
                    WHERE SourceUserId = @sourceUserId AND SourceItemId = @sourceItemId";

                var sourceUserIdParam = new SqliteParameter("@sourceUserId", string.Empty);
                var sourceItemIdParam = new SqliteParameter("@sourceItemId", string.Empty);
                var statusDateParam = new SqliteParameter("@statusDate", DateTime.UtcNow.ToString("o"));
                command.Parameters.Add(sourceUserIdParam);
                command.Parameters.Add(sourceItemIdParam);
                command.Parameters.Add(new SqliteParameter("@status", (int)status));
                command.Parameters.Add(statusDateParam);

                foreach (var (sourceUserId, sourceItemId) in items)
                {
                    sourceUserIdParam.Value = sourceUserId;
                    sourceItemIdParam.Value = sourceItemId;
                    statusDateParam.Value = DateTime.UtcNow.ToString("o");
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
                using var command = _connection!.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
                    UPDATE HistorySyncItems
                    SET Status = @status,
                        StatusDate = @statusDate
                    WHERE Id = @id";

                var idParam = new SqliteParameter("@id", 0L);
                var statusDateParam = new SqliteParameter("@statusDate", DateTime.UtcNow.ToString("o"));
                command.Parameters.Add(idParam);
                command.Parameters.Add(new SqliteParameter("@status", (int)status));
                command.Parameters.Add(statusDateParam);

                foreach (var id in ids)
                {
                    idParam.Value = id;
                    statusDateParam.Value = DateTime.UtcNow.ToString("o");
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
}
