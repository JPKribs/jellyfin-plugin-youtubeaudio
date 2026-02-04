using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Common;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.HistorySync;
using Jellyfin.Plugin.ServerSync.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks;

/// <summary>
/// Scheduled task to sync watch history from the source server.
/// Refreshes the sync table first, then applies queued changes.
/// </summary>
public class SyncMissingHistoryTask : IScheduledTask
{
    private readonly ILogger<SyncMissingHistoryTask> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncMissingHistoryTask"/> class.
    /// </summary>
    public SyncMissingHistoryTask(
        ILogger<SyncMissingHistoryTask> logger,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
    }

    /// <inheritdoc />
    public string Name => "Sync History";

    /// <inheritdoc />
    public string Key => "ServerSyncMissingHistory";

    /// <inheritdoc />
    public string Description => "Refreshes the sync table and applies queued watch history changes from the source server.";

    /// <inheritdoc />
    public string Category => "History Sync";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return;
        }

        var config = plugin.Configuration;

        // Check if history sync is enabled
        if (!config.EnableHistorySync)
        {
            return;
        }

        // Validate source server configuration
        if (string.IsNullOrWhiteSpace(config.SourceServerUrl) ||
            string.IsNullOrWhiteSpace(config.SourceServerApiKey))
        {
            _logger.LogWarning("History sync skipped: source server not configured");
            return;
        }

        // Get enabled mappings
        var enabledUserMappings = config.UserMappings?.Where(m => m.IsEnabled).ToList() ?? new List<UserMapping>();
        var enabledLibraryMappings = config.LibraryMappings?.Where(m => m.IsEnabled).ToList() ?? new List<LibraryMapping>();

        if (enabledUserMappings.Count == 0)
        {
            _logger.LogDebug("History sync skipped: no enabled user mappings");
            return;
        }

        if (enabledLibraryMappings.Count == 0)
        {
            _logger.LogDebug("History sync skipped: no enabled library mappings");
            return;
        }

        _logger.LogInformation("Starting history sync from {SourceUrl}", config.SourceServerUrl);

        using var client = new SourceServerClient(
            plugin.LoggerFactory.CreateLogger<SourceServerClient>(),
            config.SourceServerUrl,
            config.SourceServerApiKey);

        // Test connection
        var connectionResult = await client.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (!connectionResult.Success)
        {
            _logger.LogError("Failed to connect to source server at {SourceUrl}: {Error}",
                config.SourceServerUrl, connectionResult.ErrorMessage ?? "Unknown error");
            return;
        }

        var database = plugin.Database;

        // Phase 1: Refresh sync table (0-50% progress)
        _logger.LogInformation("Phase 1: Refreshing history sync table");
        await RefreshSyncTableAsync(client, database, enabledUserMappings, enabledLibraryMappings,
            new Progress<double>(p => progress.Report(p * 0.5)), cancellationToken).ConfigureAwait(false);

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        // Phase 2: Apply queued changes (50-100% progress)
        _logger.LogInformation("Phase 2: Applying queued history changes");

        var localClient = new LocalServerClient(_logger, _libraryManager, _userManager, _userDataManager);

        // Get all queued history items
        var queuedItems = database.GetHistoryItemsByStatus(BaseSyncStatus.Queued);
        var totalItems = queuedItems.Count;

        if (totalItems == 0)
        {
            _logger.LogInformation("No queued history items to sync");
            progress.Report(100);
            return;
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
                var success = await SyncHistoryItemAsync(item, localClient, database).ConfigureAwait(false);

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
            progress.Report(50 + (double)processedCount / totalItems * 50);
        }

        // Update last sync time
        config.LastHistorySyncTime = DateTime.UtcNow;
        plugin.SaveConfiguration();

        _logger.LogInformation(
            "History sync completed: {Success} succeeded, {Error} failed out of {Total}",
            successCount, errorCount, totalItems);

        progress.Report(100);
    }

    /// <summary>
    /// Refreshes the history sync table from the source server.
    /// </summary>
    private async Task RefreshSyncTableAsync(
        SourceServerClient client,
        SyncDatabase database,
        List<UserMapping> userMappings,
        List<LibraryMapping> libraryMappings,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var historyService = new HistorySyncTableService(_logger, _libraryManager, _userManager, _userDataManager);

        // Get total item count for progress tracking
        var totalItems = await historyService.GetTotalItemCountAsync(
            client, userMappings, libraryMappings, cancellationToken).ConfigureAwait(false);

        // Process each user mapping and library mapping combination
        var processedItems = 0;

        foreach (var userMapping in userMappings)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            foreach (var libraryMapping in libraryMappings)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                await historyService.ProcessUserLibraryAsync(
                    client,
                    database,
                    userMapping,
                    libraryMapping,
                    cancellationToken,
                    onItemProcessed: () =>
                    {
                        processedItems++;
                        if (totalItems > 0)
                        {
                            progress.Report((double)processedItems / totalItems * 100);
                        }
                    }).ConfigureAwait(false);
            }
        }

        progress.Report(100);
    }

    /// <summary>
    /// Syncs a single history item to the local server.
    /// </summary>
    private Task<bool> SyncHistoryItemAsync(
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
            return Task.FromResult(false);
        }

        // Validate we have a local user ID
        if (string.IsNullOrEmpty(item.LocalUserId))
        {
            _logger.LogWarning("Cannot sync history for {ItemName}: local user not found", item.ItemName);
            item.Status = BaseSyncStatus.Errored;
            item.ErrorMessage = "Local user not found";
            item.StatusDate = DateTime.UtcNow;
            database.UpsertHistoryItem(item);
            return Task.FromResult(false);
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
            return Task.FromResult(false);
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
            return Task.FromResult(true);
        }
        else
        {
            _logger.LogWarning("Failed to sync history for {ItemName}", item.ItemName);
            item.Status = BaseSyncStatus.Errored;
            item.ErrorMessage = "Failed to update user data";
            item.StatusDate = DateTime.UtcNow;
            database.UpsertHistoryItem(item);
            return Task.FromResult(false);
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
