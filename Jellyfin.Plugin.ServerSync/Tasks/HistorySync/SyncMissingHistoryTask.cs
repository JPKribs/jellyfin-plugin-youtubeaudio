using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;
using Jellyfin.Plugin.ServerSync.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks;

/// <summary>
/// Scheduled task to sync watch history from the source server.
/// Applies queued changes from the history sync table.
/// </summary>
public class SyncMissingHistoryTask : IScheduledTask
{
    private readonly ILogger<SyncMissingHistoryTask> _logger;
    private readonly IPluginConfigurationManager _configManager;
    private readonly ISyncDatabaseProvider _databaseProvider;
    private readonly LocalServerClient _localClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncMissingHistoryTask"/> class.
    /// </summary>
    public SyncMissingHistoryTask(
        ILogger<SyncMissingHistoryTask> logger,
        IPluginConfigurationManager configManager,
        ISyncDatabaseProvider databaseProvider,
        LocalServerClient localClient)
    {
        _logger = logger;
        _configManager = configManager;
        _databaseProvider = databaseProvider;
        _localClient = localClient;
    }

    /// <inheritdoc />
    public string Name => "Sync History";

    /// <inheritdoc />
    public string Key => "ServerSyncMissingHistory";

    /// <inheritdoc />
    public string Description => "Applies queued watch history changes from the sync table to the local server.";

    /// <inheritdoc />
    public string Category => "History Sync";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = _configManager.Configuration;

        // Check if history sync is enabled
        if (!config.EnableHistorySync)
        {
            return Task.CompletedTask;
        }

        // Validate source server configuration
        if (string.IsNullOrWhiteSpace(config.SourceServerUrl) ||
            string.IsNullOrWhiteSpace(config.SourceServerApiKey))
        {
            _logger.LogWarning("History sync skipped: source server not configured");
            return Task.CompletedTask;
        }

        // Get enabled mappings
        var enabledUserMappings = config.UserMappings?.Where(m => m.IsEnabled).ToList() ?? new List<UserMapping>();
        var enabledLibraryMappings = config.LibraryMappings?.Where(m => m.IsEnabled).ToList() ?? new List<LibraryMapping>();

        if (enabledUserMappings.Count == 0)
        {
            _logger.LogDebug("History sync skipped: no enabled user mappings");
            return Task.CompletedTask;
        }

        if (enabledLibraryMappings.Count == 0)
        {
            _logger.LogDebug("History sync skipped: no enabled library mappings");
            return Task.CompletedTask;
        }

        _logger.LogInformation("Starting history sync");

        var database = _databaseProvider.Database;

        // Apply queued changes
        _logger.LogInformation("Applying queued history changes");

        // Get all queued history items
        var queuedItems = database.GetHistoryItemsByStatus(BaseSyncStatus.Queued);
        var totalItems = queuedItems.Count;

        if (totalItems == 0)
        {
            _logger.LogInformation("No queued history items to sync");
            progress.Report(100);
            return Task.CompletedTask;
        }

        _logger.LogInformation("Processing {Count} queued history items", totalItems);

        var processedCount = 0;
        var successCount = 0;
        var errorCount = 0;

        foreach (var item in queuedItems)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                var success = SyncHistoryItem(item, _localClient, database);

                if (success)
                {
                    successCount++;
                }
                else
                {
                    errorCount++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to sync history item {ItemName}", item.ItemName);
                item.Status = BaseSyncStatus.Errored;
                item.ErrorMessage = ex.Message;
                item.StatusDate = DateTime.UtcNow;
                database.UpsertHistoryItem(item);
                errorCount++;
            }

            processedCount++;
            progress.Report((double)processedCount / totalItems * 100);
        }

        // Update last sync time
        config.LastHistorySyncTime = DateTime.UtcNow;
        _configManager.SaveConfiguration();

        _logger.LogInformation(
            "History sync completed: {Success} succeeded, {Error} failed out of {Total}",
            successCount, errorCount, totalItems);

        progress.Report(100);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Syncs a single history item to the local server.
    /// </summary>
    private bool SyncHistoryItem(
        HistorySyncItem item,
        LocalServerClient localClient,
        SyncDatabase database)
    {
        // Validate we have a local item ID
        if (string.IsNullOrEmpty(item.LocalItemId))
        {
            _logger.LogWarning("Cannot sync history for {ItemName}: local item not found", item.ItemName);
            item.Status = BaseSyncStatus.Errored;
            item.ErrorMessage = "Local item not found";
            item.StatusDate = DateTime.UtcNow;
            database.UpsertHistoryItem(item);
            return false;
        }

        // Validate we have a local user ID
        if (string.IsNullOrEmpty(item.LocalUserId))
        {
            _logger.LogWarning("Cannot sync history for {ItemName}: local user not found", item.ItemName);
            item.Status = BaseSyncStatus.Errored;
            item.ErrorMessage = "Local user not found";
            item.StatusDate = DateTime.UtcNow;
            database.UpsertHistoryItem(item);
            return false;
        }

        // Parse IDs
        if (!Guid.TryParse(item.LocalUserId, out var localUserId) ||
            !Guid.TryParse(item.LocalItemId, out var localItemId))
        {
            _logger.LogWarning("Cannot sync history for {ItemName}: invalid user or item ID", item.ItemName);
            item.Status = BaseSyncStatus.Errored;
            item.ErrorMessage = "Invalid user or item ID";
            item.StatusDate = DateTime.UtcNow;
            database.UpsertHistoryItem(item);
            return false;
        }

        // Apply the merged history data
        var success = localClient.UpdateUserItemData(
            localUserId,
            localItemId,
            item.MergedIsPlayed,
            item.MergedPlayCount,
            item.MergedPlaybackPositionTicks,
            item.MergedLastPlayedDate,
            item.MergedIsFavorite);

        if (success)
        {
            _logger.LogDebug(
                "Synced history for {ItemName}: {Changes}",
                item.ItemName,
                HistorySyncMergeService.GetChangeSummary(item));

            // Update item status
            item.Status = BaseSyncStatus.Synced;
            item.LastSyncTime = DateTime.UtcNow;
            item.StatusDate = DateTime.UtcNow;
            item.ErrorMessage = null;

            // Update local state to match merged state
            item.LocalIsPlayed = item.MergedIsPlayed;
            item.LocalPlayCount = item.MergedPlayCount;
            item.LocalPlaybackPositionTicks = item.MergedPlaybackPositionTicks;
            item.LocalLastPlayedDate = item.MergedLastPlayedDate;
            item.LocalIsFavorite = item.MergedIsFavorite;

            database.UpsertHistoryItem(item);
            return true;
        }
        else
        {
            _logger.LogWarning("Failed to sync history for {ItemName}", item.ItemName);
            item.Status = BaseSyncStatus.Errored;
            item.ErrorMessage = "Failed to update user data";
            item.StatusDate = DateTime.UtcNow;
            database.UpsertHistoryItem(item);
            return false;
        }
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(6).Ticks
            }
        };
    }
}
