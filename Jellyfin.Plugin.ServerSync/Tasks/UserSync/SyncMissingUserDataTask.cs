using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks;

/// <summary>
/// Scheduled task to sync user data from the source server.
/// Applies queued changes from the user sync table.
/// </summary>
public class SyncMissingUserDataTask : IScheduledTask
{
    private readonly ILogger<SyncMissingUserDataTask> _logger;
    private readonly IPluginConfigurationManager _configManager;
    private readonly ISourceServerClientFactory _clientFactory;
    private readonly UserSyncStateService _stateService;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncMissingUserDataTask"/> class.
    /// </summary>
    public SyncMissingUserDataTask(
        ILogger<SyncMissingUserDataTask> logger,
        IPluginConfigurationManager configManager,
        ISourceServerClientFactory clientFactory,
        UserSyncStateService stateService)
    {
        _logger = logger;
        _configManager = configManager;
        _clientFactory = clientFactory;
        _stateService = stateService;
    }

    /// <inheritdoc />
    public string Name => "Sync User Data";

    /// <inheritdoc />
    public string Key => "ServerSyncMissingUserData";

    /// <inheritdoc />
    public string Description => "Applies queued user setting changes from the sync table to the local server.";

    /// <inheritdoc />
    public string Category => "User Sync";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = _configManager.Configuration;

        // Check if user sync is enabled
        if (!config.EnableUserSync)
        {
            _logger.LogDebug("User sync is disabled, skipping sync");
            return;
        }

        // Validate configuration
        if (string.IsNullOrWhiteSpace(config.SourceServerUrl) ||
            string.IsNullOrWhiteSpace(config.SourceServerApiKey))
        {
            _logger.LogWarning("Source server not configured, skipping user sync");
            return;
        }

        var enabledMappings = config.UserMappings?.FindAll(m => m.IsEnabled) ?? [];
        if (enabledMappings.Count == 0)
        {
            _logger.LogWarning("No enabled user mappings, skipping user sync");
            return;
        }

        _logger.LogInformation("Starting user data sync from {SourceUrl}", config.SourceServerUrl);

        using var sourceClient = _clientFactory.Create(config.SourceServerUrl, config.SourceServerApiKey);

        // Test connection (needed for profile image downloads)
        var connectionResult = await sourceClient.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (!connectionResult.Success)
        {
            _logger.LogError("Failed to connect to source server: {Error}", connectionResult.ErrorMessage);
            return;
        }

        // Apply queued changes
        _logger.LogInformation("Applying queued user changes");

        var itemsSynced = await _stateService.ApplyQueuedChangesAsync(
            sourceClient,
            progress,
            cancellationToken).ConfigureAwait(false);

        // Update last sync time
        config.LastUserSyncTime = DateTime.UtcNow;
        _configManager.SaveConfiguration();

        _logger.LogInformation("User data sync complete. {Count} items synced", itemsSynced);
        progress.Report(100);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(24).Ticks
            }
        };
    }
}
