using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks;

/// <summary>
/// Scheduled task to refresh the user sync table from source and local servers.
/// </summary>
public class RefreshUserSyncTableTask : IScheduledTask
{
    private readonly ILogger<RefreshUserSyncTableTask> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="RefreshUserSyncTableTask"/> class.
    /// </summary>
    public RefreshUserSyncTableTask(
        ILogger<RefreshUserSyncTableTask> logger,
        ILoggerFactory loggerFactory,
        IUserManager userManager)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _userManager = userManager;
    }

    /// <inheritdoc />
    public string Name => "Refresh Sync Table";

    /// <inheritdoc />
    public string Key => "ServerSyncRefreshUserTable";

    /// <inheritdoc />
    public string Description => "Scans source and local servers for user setting differences and updates the user sync table.";

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
            _logger.LogDebug("User sync is disabled, skipping refresh");
            return;
        }

        // Validate configuration
        if (string.IsNullOrWhiteSpace(config.SourceServerUrl) ||
            string.IsNullOrWhiteSpace(config.SourceServerApiKey))
        {
            _logger.LogWarning("Source server not configured, skipping user sync refresh");
            return;
        }

        var enabledMappings = config.UserMappings?.FindAll(m => m.IsEnabled) ?? [];
        if (enabledMappings.Count == 0)
        {
            _logger.LogWarning("No enabled user mappings, skipping user sync refresh");
            return;
        }

        _logger.LogInformation("Starting user sync table refresh");

        using var sourceClient = new SourceServerClient(
            _loggerFactory.CreateLogger<SourceServerClient>(),
            config.SourceServerUrl,
            config.SourceServerApiKey);

        // Test connection
        var connectionResult = await sourceClient.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (!connectionResult.Success)
        {
            _logger.LogError("Failed to connect to source server: {Error}", connectionResult.ErrorMessage);
            return;
        }

        // Create table service and refresh
        var tableService = new UserSyncTableService(
            _loggerFactory.CreateLogger<UserSyncTableService>(),
            plugin.Database,
            _userManager);

        var itemsProcessed = await tableService.RefreshUserSyncTableAsync(
            sourceClient,
            config,
            progress,
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("User sync table refresh complete. {Count} properties compared", itemsProcessed);
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
                IntervalTicks = TimeSpan.FromHours(22).Ticks
            }
        };
    }
}
