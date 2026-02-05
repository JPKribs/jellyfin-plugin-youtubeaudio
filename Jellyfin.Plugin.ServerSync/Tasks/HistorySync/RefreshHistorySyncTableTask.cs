using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks;

/// <summary>
/// Scheduled task to refresh the history sync table from source and local servers.
/// </summary>
public class RefreshHistorySyncTableTask : IScheduledTask
{
    private readonly ILogger<RefreshHistorySyncTableTask> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="RefreshHistorySyncTableTask"/> class.
    /// </summary>
    public RefreshHistorySyncTableTask(
        ILogger<RefreshHistorySyncTableTask> logger,
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
    public string Name => "Refresh Sync Table";

    /// <inheritdoc />
    public string Key => "ServerSyncRefreshHistoryTable";

    /// <inheritdoc />
    public string Description => "Scans source and local servers for watch history differences and updates the history sync table.";

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
        var historyService = new HistorySyncTableService(_logger, _libraryManager, _userManager, _userDataManager);

        // Progress tracking
        const double InitProgress = 10.0;
        const double ProcessingProgress = 80.0;

        progress.Report(0);

        // Get total item count for progress tracking
        var totalItems = await historyService.GetTotalItemCountAsync(
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
                            var itemProgress = (double)processedItems / totalItems * ProcessingProgress;
                            progress.Report(InitProgress + itemProgress);
                        }
                    }).ConfigureAwait(false);
            }
        }

        progress.Report(100);

        // Update last sync time
        config.LastHistorySyncTime = DateTime.UtcNow;
        plugin.SaveConfiguration();

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
