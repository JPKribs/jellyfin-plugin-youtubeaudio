using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks;

/// <summary>
/// Scheduled task to refresh the history sync table from source and local servers.
/// </summary>
public class RefreshHistorySyncTableTask : IScheduledTask
{
    private readonly ILogger<RefreshHistorySyncTableTask> _logger;
    private readonly IPluginConfigurationManager _configManager;
    private readonly ISyncDatabaseProvider _databaseProvider;
    private readonly ISourceServerClientFactory _clientFactory;
    private readonly HistorySyncTableService _historyService;

    /// <summary>
    /// Initializes a new instance of the <see cref="RefreshHistorySyncTableTask"/> class.
    /// </summary>
    public RefreshHistorySyncTableTask(
        ILogger<RefreshHistorySyncTableTask> logger,
        IPluginConfigurationManager configManager,
        ISyncDatabaseProvider databaseProvider,
        ISourceServerClientFactory clientFactory,
        HistorySyncTableService historyService)
    {
        _logger = logger;
        _configManager = configManager;
        _databaseProvider = databaseProvider;
        _clientFactory = clientFactory;
        _historyService = historyService;
    }

    /// <inheritdoc />
    public string Name => "Refresh History Sync Table";

    /// <inheritdoc />
    public string Key => "ServerSyncRefreshHistoryTable";

    /// <inheritdoc />
    public string Description => "Scans source and local servers for watch history differences and updates the history sync table.";

    /// <inheritdoc />
    public string Category => "History Sync";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = _configManager.Configuration;

        // Check if history sync is enabled
        if (!config.EnableHistorySync)
        {
            return;
        }

        // Validate source server configuration
        if (string.IsNullOrWhiteSpace(config.SourceServerUrl))
        {
            _logger.LogWarning("History sync skipped: source server URL not configured");
            return;
        }

        if (string.IsNullOrWhiteSpace(config.SourceServerApiKey))
        {
            _logger.LogWarning("History sync skipped: API key not configured");
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

        _logger.LogInformation("Starting history sync table refresh from {SourceUrl}", config.SourceServerUrl);

        using var client = _clientFactory.Create(config.SourceServerUrl, config.SourceServerApiKey);

        // Test connection
        var connectionResult = await client.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (!connectionResult.Success)
        {
            _logger.LogError("Failed to connect to source server at {SourceUrl}: {Error}",
                config.SourceServerUrl, connectionResult.ErrorMessage ?? "Unknown error");
            return;
        }

        var database = _databaseProvider.Database;

        // Progress tracking: 1% for init, 98% for processing items, 1% for finalization
        const double InitProgress = 1.0;
        const double ProcessingProgress = 98.0;

        progress.Report(0);

        // Get total item count for progress tracking
        var totalItems = await _historyService.GetTotalItemCountAsync(
            client,
            enabledUserMappings,
            enabledLibraryMappings,
            cancellationToken).ConfigureAwait(false);

        progress.Report(InitProgress);

        // Process each user mapping and library mapping combination
        var processedItems = 0;

        foreach (var userMapping in enabledUserMappings)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            foreach (var libraryMapping in enabledLibraryMappings)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                await _historyService.ProcessUserLibraryAsync(
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
                            var itemProgress = (double)processedItems / totalItems * ProcessingProgress;
                            progress.Report(InitProgress + itemProgress);
                        }
                    }).ConfigureAwait(false);
            }
        }

        progress.Report(100);

        // Update last sync time
        config.LastHistorySyncTime = DateTime.UtcNow;
        _configManager.SaveConfiguration();

        _logger.LogInformation("History sync table refresh completed");
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(4).Ticks
            }
        };
    }
}
