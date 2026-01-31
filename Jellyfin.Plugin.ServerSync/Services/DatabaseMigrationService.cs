using System;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

#pragma warning disable CA2100 // SQL commands use only internal constants and safe parameterized queries

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Handles database schema migrations for the sync database.
/// </summary>
public static class DatabaseMigrationService
{
    /// <summary>
    /// Current schema version. Increment this when adding new migrations.
    /// </summary>
    public const int CurrentSchemaVersion = 7;

    /// <summary>
    /// Creates the initial database schema.
    /// </summary>
    /// <param name="connection">Database connection.</param>
    public static void CreateInitialSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
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
                CompanionFiles TEXT,
                UNIQUE(SourceItemId)
            );
            CREATE INDEX IF NOT EXISTS idx_source_item ON SyncItems(SourceItemId);
            CREATE INDEX IF NOT EXISTS idx_status ON SyncItems(Status);
            CREATE INDEX IF NOT EXISTS idx_source_library ON SyncItems(SourceLibraryId);
            CREATE INDEX IF NOT EXISTS idx_local_path ON SyncItems(LocalPath);
        ";
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Gets the current schema version from the database.
    /// </summary>
    /// <param name="connection">Database connection.</param>
    /// <returns>Current schema version number.</returns>
    public static int GetSchemaVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        var result = command.ExecuteScalar();
        return Convert.ToInt32(result);
    }

    /// <summary>
    /// Sets the schema version in the database.
    /// </summary>
    /// <param name="connection">Database connection.</param>
    /// <param name="version">Version number to set.</param>
    public static void SetSchemaVersion(SqliteConnection connection, int version)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA user_version = {version}";
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Migrates the database schema from an older version to the current version.
    /// </summary>
    /// <param name="connection">Database connection.</param>
    /// <param name="fromVersion">Version to migrate from.</param>
    /// <param name="logger">Logger for migration messages.</param>
    /// <returns>True if migration succeeded, false if failed.</returns>
    public static bool MigrateSchema(SqliteConnection connection, int fromVersion, ILogger logger)
    {
        logger.LogInformation("Migrating database schema from v{From} to v{To}", fromVersion, CurrentSchemaVersion);

        using var transaction = connection.BeginTransaction();
        try
        {
            if (fromVersion < 2)
            {
                MigrateToV2(connection, transaction, logger);
            }

            if (fromVersion < 3)
            {
                MigrateToV3(connection, transaction, logger);
            }

            if (fromVersion < 4)
            {
                MigrateToV4(connection, transaction, logger);
            }

            if (fromVersion < 5)
            {
                MigrateToV5(logger);
            }

            if (fromVersion < 6)
            {
                MigrateToV6(connection, transaction, logger);
            }

            if (fromVersion < 7)
            {
                MigrateToV7(connection, transaction, logger);
            }

            SetSchemaVersion(connection, CurrentSchemaVersion);
            transaction.Commit();
            logger.LogInformation("Database migration completed successfully");
            return true;
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            logger.LogError(ex, "Database migration failed, rolled back changes");
            return false;
        }
    }

    /// <summary>
    /// Migration to v2: Add LastSyncTime, ErrorMessage, RetryCount columns and local_path index.
    /// </summary>
    private static void MigrateToV2(SqliteConnection connection, SqliteTransaction transaction, ILogger logger)
    {
        var alterStatements = new[]
        {
            "ALTER TABLE SyncItems ADD COLUMN LastSyncTime TEXT",
            "ALTER TABLE SyncItems ADD COLUMN ErrorMessage TEXT",
            "ALTER TABLE SyncItems ADD COLUMN RetryCount INTEGER DEFAULT 0"
        };

        foreach (var statement in alterStatements)
        {
            ExecuteAlterIfColumnMissing(connection, transaction, statement, logger);
        }

        using var idxCmd = connection.CreateCommand();
        idxCmd.Transaction = transaction;
        idxCmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_local_path ON SyncItems(LocalPath)";
        idxCmd.ExecuteNonQuery();

        logger.LogInformation("Migration v2: Added LastSyncTime, ErrorMessage, RetryCount columns");
    }

    /// <summary>
    /// Migration to v3: Add SourceETag column for reliable change detection.
    /// </summary>
    private static void MigrateToV3(SqliteConnection connection, SqliteTransaction transaction, ILogger logger)
    {
        ExecuteAlterIfColumnMissing(
            connection,
            transaction,
            "ALTER TABLE SyncItems ADD COLUMN SourceETag TEXT",
            logger);

        logger.LogInformation("Migration v3: Added SourceETag column for change detection");
    }

    /// <summary>
    /// Migration to v4: Add PendingType column and migrate old PendingDeletion/PendingReplacement statuses.
    /// </summary>
    private static void MigrateToV4(SqliteConnection connection, SqliteTransaction transaction, ILogger logger)
    {
        ExecuteAlterIfColumnMissing(
            connection,
            transaction,
            "ALTER TABLE SyncItems ADD COLUMN PendingType INTEGER",
            logger);

        // Migrate old statuses: PendingDeletion (5) -> Pending (0) with PendingType.Deletion (2)
        using var migrateDeletionCmd = connection.CreateCommand();
        migrateDeletionCmd.Transaction = transaction;
        migrateDeletionCmd.CommandText = "UPDATE SyncItems SET Status = 0, PendingType = 2 WHERE Status = 5";
        var deletionCount = migrateDeletionCmd.ExecuteNonQuery();
        if (deletionCount > 0)
        {
            logger.LogInformation("Migrated {Count} PendingDeletion items to Pending with PendingType.Deletion", deletionCount);
        }

        // Migrate old statuses: PendingReplacement (6) -> Pending (0) with PendingType.Replacement (1)
        using var migrateReplacementCmd = connection.CreateCommand();
        migrateReplacementCmd.Transaction = transaction;
        migrateReplacementCmd.CommandText = "UPDATE SyncItems SET Status = 0, PendingType = 1 WHERE Status = 6";
        var replacementCount = migrateReplacementCmd.ExecuteNonQuery();
        if (replacementCount > 0)
        {
            logger.LogInformation("Migrated {Count} PendingReplacement items to Pending with PendingType.Replacement", replacementCount);
        }

        // Set PendingType.Download (0) for existing Pending items without a type
        using var migrateDownloadCmd = connection.CreateCommand();
        migrateDownloadCmd.Transaction = transaction;
        migrateDownloadCmd.CommandText = "UPDATE SyncItems SET PendingType = 0 WHERE Status = 0 AND PendingType IS NULL";
        migrateDownloadCmd.ExecuteNonQuery();

        logger.LogInformation("Migration v4: Added PendingType column and migrated legacy statuses");
    }

    /// <summary>
    /// Migration to v5: Status value 5 is now Deleting instead of old PendingDeletion.
    /// </summary>
    private static void MigrateToV5(ILogger logger)
    {
        // No schema changes, but status value 5 is now Deleting instead of old PendingDeletion
        // Old PendingDeletion items were already migrated to Pending+PendingType.Deletion in v4
        logger.LogInformation("Migration v5: Deleting status (5) is now available");
    }

    /// <summary>
    /// Migration to v6: Add CompanionFiles column to track downloaded companion files.
    /// </summary>
    private static void MigrateToV6(SqliteConnection connection, SqliteTransaction transaction, ILogger logger)
    {
        ExecuteAlterIfColumnMissing(
            connection,
            transaction,
            "ALTER TABLE SyncItems ADD COLUMN CompanionFiles TEXT",
            logger);

        logger.LogInformation("Migration v6: Added CompanionFiles column for tracking companion files");
    }

    /// <summary>
    /// Migration to v7: Add HistorySyncItems and UserSyncItems tables for modular sync.
    /// </summary>
    private static void MigrateToV7(SqliteConnection connection, SqliteTransaction transaction, ILogger logger)
    {
        // Create HistorySyncItems table
        using var historyCmd = connection.CreateCommand();
        historyCmd.Transaction = transaction;
        historyCmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS HistorySyncItems (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SourceUserId TEXT NOT NULL,
                LocalUserId TEXT NOT NULL,
                SourceLibraryId TEXT NOT NULL,
                LocalLibraryId TEXT NOT NULL,
                SourceItemId TEXT NOT NULL,
                LocalItemId TEXT,
                ItemName TEXT,
                SourcePath TEXT,
                LocalPath TEXT,
                SourceIsPlayed INTEGER,
                SourcePlayCount INTEGER,
                SourcePlaybackPositionTicks INTEGER,
                SourceLastPlayedDate TEXT,
                SourceIsFavorite INTEGER,
                LocalIsPlayed INTEGER,
                LocalPlayCount INTEGER,
                LocalPlaybackPositionTicks INTEGER,
                LocalLastPlayedDate TEXT,
                LocalIsFavorite INTEGER,
                MergedIsPlayed INTEGER,
                MergedPlayCount INTEGER,
                MergedPlaybackPositionTicks INTEGER,
                MergedLastPlayedDate TEXT,
                MergedIsFavorite INTEGER,
                Status INTEGER NOT NULL,
                StatusDate TEXT NOT NULL,
                LastSyncTime TEXT,
                ErrorMessage TEXT,
                UNIQUE(SourceUserId, SourceItemId)
            );
            CREATE INDEX IF NOT EXISTS idx_history_user ON HistorySyncItems(SourceUserId, LocalUserId);
            CREATE INDEX IF NOT EXISTS idx_history_status ON HistorySyncItems(Status);
            CREATE INDEX IF NOT EXISTS idx_history_library ON HistorySyncItems(SourceLibraryId);
        ";
        historyCmd.ExecuteNonQuery();

        // Create UserSyncItems table (scaffolding for future use)
        using var userCmd = connection.CreateCommand();
        userCmd.Transaction = transaction;
        userCmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS UserSyncItems (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SourceUserId TEXT NOT NULL,
                LocalUserId TEXT NOT NULL,
                PropertyName TEXT NOT NULL,
                SourceValue TEXT,
                LocalValue TEXT,
                MergedValue TEXT,
                Status INTEGER NOT NULL,
                StatusDate TEXT NOT NULL,
                LastSyncTime TEXT,
                ErrorMessage TEXT,
                UNIQUE(SourceUserId, PropertyName)
            );
            CREATE INDEX IF NOT EXISTS idx_user_sync_user ON UserSyncItems(SourceUserId, LocalUserId);
            CREATE INDEX IF NOT EXISTS idx_user_sync_status ON UserSyncItems(Status);
        ";
        userCmd.ExecuteNonQuery();

        logger.LogInformation("Migration v7: Added HistorySyncItems and UserSyncItems tables");
    }

    /// <summary>
    /// Executes an ALTER TABLE statement, ignoring "duplicate column" errors.
    /// </summary>
    private static void ExecuteAlterIfColumnMissing(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string statement,
        ILogger logger)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = statement;
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug("Column already exists, skipping: {Statement}", statement);
        }
    }
}
