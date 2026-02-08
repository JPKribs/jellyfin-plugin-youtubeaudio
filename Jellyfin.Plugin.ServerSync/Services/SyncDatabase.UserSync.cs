using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.UserSync;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

#pragma warning disable CA2100 // SQL commands use only internal constants and safe parameterized queries

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// SyncDatabase — User Sync methods.
/// </summary>
public partial class SyncDatabase
{
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
                        WHEN MIN(CASE WHEN Status = 4 THEN 1 ELSE 0 END) = 1 THEN 4  -- All Ignored
                        ELSE 2  -- Synced
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
                using var command = _connection!.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
                    UPDATE UserSyncItems
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

        var countQuery = $@"
            SELECT COUNT(DISTINCT SourceUserId || '|' || LocalUserId)
            FROM UserSyncItems
            {whereClause}";

        var dataQuery = $@"
            SELECT SourceUserId, LocalUserId, MIN(SourceUserName) as SourceUserName, MIN(LocalUserName) as LocalUserName
            FROM UserSyncItems
            {whereClause}
            GROUP BY SourceUserId, LocalUserId
            ORDER BY MIN(SourceUserName), MIN(LocalUserName)
            LIMIT @take OFFSET @skip";

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
                using var command = _connection!.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
                    UPDATE UserSyncItems
                    SET Status = @status,
                        StatusDate = @statusDate
                    WHERE SourceUserId = @sourceUserId AND LocalUserId = @localUserId";

                var sourceUserIdParam = new SqliteParameter("@sourceUserId", string.Empty);
                var localUserIdParam = new SqliteParameter("@localUserId", string.Empty);
                var statusDateParam = new SqliteParameter("@statusDate", DateTime.UtcNow.ToString("o"));
                command.Parameters.Add(sourceUserIdParam);
                command.Parameters.Add(localUserIdParam);
                command.Parameters.Add(new SqliteParameter("@status", (int)status));
                command.Parameters.Add(statusDateParam);

                foreach (var (sourceUserId, localUserId) in mappings)
                {
                    sourceUserIdParam.Value = sourceUserId;
                    localUserIdParam.Value = localUserId;
                    statusDateParam.Value = DateTime.UtcNow.ToString("o");
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
}
