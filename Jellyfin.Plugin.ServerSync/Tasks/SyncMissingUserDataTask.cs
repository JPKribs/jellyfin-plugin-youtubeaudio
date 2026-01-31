using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks;

/// <summary>
/// Scheduled task to apply queued user sync changes to the local server.
/// This is scaffolding for future user sync functionality.
/// </summary>
public class SyncMissingUserDataTask : IScheduledTask
{
    private readonly ILogger<SyncMissingUserDataTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncMissingUserDataTask"/> class.
    /// </summary>
    public SyncMissingUserDataTask(ILogger<SyncMissingUserDataTask> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Sync Missing User Data";

    /// <inheritdoc />
    public string Key => "ServerSyncMissingUserData";

    /// <inheritdoc />
    public string Description => "Applies queued user setting changes from the source server to the local server. (Future feature)";

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
