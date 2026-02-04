using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Services;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks;

/// <summary>
/// Scheduled task to sync user data from the source server.
/// Refreshes the sync table first, then applies queued changes.
/// </summary>
public class SyncMissingUserDataTask : IScheduledTask
{
    private readonly ILogger<SyncMissingUserDataTask> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IUserManager _userManager;
    private readonly IProviderManager _providerManager;
    private readonly IServerConfigurationManager _serverConfigurationManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncMissingUserDataTask"/> class.
    /// </summary>
    public SyncMissingUserDataTask(
        ILogger<SyncMissingUserDataTask> logger,
        ILoggerFactory loggerFactory,
        IUserManager userManager,
        IProviderManager providerManager,
        IServerConfigurationManager serverConfigurationManager)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _userManager = userManager;
        _providerManager = providerManager;
        _serverConfigurationManager = serverConfigurationManager;
    }

    /// <inheritdoc />
    public string Name => "Sync User Data";

    /// <inheritdoc />
    public string Key => "ServerSyncMissingUserData";

    /// <inheritdoc />
    public string Description => "Refreshes the sync table and applies queued user setting changes from the source server.";

    /// <inheritdoc />
    public string Category => "User Sync";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return;
        }

        var config = plugin.Configuration;

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

        var enabledMappings = config.UserMappings.FindAll(m => m.IsEnabled);
        if (enabledMappings.Count == 0)
        {
            _logger.LogWarning("No enabled user mappings, skipping user sync");
            return;
        }

        _logger.LogInformation("Starting user data sync from {SourceUrl}", config.SourceServerUrl);

        using var sourceClient = new SourceServerClient(
            _loggerFactory.CreateLogger<SourceServerClient>(),
            config.SourceServerUrl,
            config.SourceServerApiKey);

        // Test connection (needed for profile image downloads)
        var connectionResult = await sourceClient.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (!connectionResult.Success)
        {
            _logger.LogError("Failed to connect to source server: {Error}", connectionResult.ErrorMessage);
            return;
        }

        // Phase 1: Refresh sync table (0-50% progress)
        _logger.LogInformation("Phase 1: Refreshing user sync table");
        var tableService = new UserSyncTableService(
            _loggerFactory.CreateLogger<UserSyncTableService>(),
            plugin.Database,
            _userManager);

        await tableService.RefreshUserSyncTableAsync(
            sourceClient,
            config,
            new Progress<double>(p => progress.Report(p * 0.5)),
            cancellationToken).ConfigureAwait(false);

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        // Phase 2: Apply queued changes (50-100% progress)
        _logger.LogInformation("Phase 2: Applying queued user changes");

        // Create state service and apply changes
        var stateService = new UserSyncStateService(
            _loggerFactory.CreateLogger<UserSyncStateService>(),
            plugin.Database,
            _userManager,
            _providerManager,
            _serverConfigurationManager);

        var itemsSynced = await stateService.ApplyQueuedChangesAsync(
            sourceClient,
            new Progress<double>(p => progress.Report(50 + p * 0.5)),
            cancellationToken).ConfigureAwait(false);

        // Update last sync time
        config.LastUserSyncTime = DateTime.UtcNow;
        plugin.SaveConfiguration();

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
