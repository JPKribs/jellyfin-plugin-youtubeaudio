using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks;

/// <summary>
/// Scheduled task to refresh the user sync table from source and local servers.
/// This is scaffolding for future user sync functionality.
/// </summary>
public class RefreshUserSyncTableTask : IScheduledTask
{
    private readonly ILogger<RefreshUserSyncTableTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RefreshUserSyncTableTask"/> class.
    /// </summary>
    public RefreshUserSyncTableTask(ILogger<RefreshUserSyncTableTask> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Refresh User Sync Table";

    /// <inheritdoc />
    public string Key => "ServerSyncRefreshUserTable";

    /// <inheritdoc />
    public string Description => "Scans source and local servers for user setting differences and updates the user sync table. (Future feature)";

    /// <inheritdoc />
    public string Category => "User Sync";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return Task.CompletedTask;
        }

        var config = plugin.Configuration;

        // Check if user sync is enabled
        if (!config.EnableUserSync)
        {
            return Task.CompletedTask;
        }

        _logger.LogInformation("User sync is not yet implemented. This task is scaffolding for future functionality.");
        progress.Report(100);

        return Task.CompletedTask;
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
