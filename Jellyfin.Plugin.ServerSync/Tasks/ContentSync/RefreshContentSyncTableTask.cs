using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;
using Jellyfin.Plugin.ServerSync.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks;

/// <summary>
/// Scheduled task to update sync tables from the source server.
/// </summary>
public class UpdateSyncTablesTask : IScheduledTask
{
    private readonly ILogger<UpdateSyncTablesTask> _logger;
    private readonly ILibraryManager _libraryManager;

    public UpdateSyncTablesTask(ILogger<UpdateSyncTablesTask> logger, ILibraryManager libraryManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
    }

    public string Name => "Refresh Sync Table";

    public string Key => "ServerSyncUpdateTables";

    public string Description => "Fetches item list from source server and updates the sync tracking table.";

    public string Category => "Content Sync";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return;
        }

        var config = plugin.Configuration;

        if (!config.EnableContentSync)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(config.SourceServerUrl))
        {
            _logger.LogWarning("Sync table update skipped: source server URL not configured");
            return;
        }

        if (string.IsNullOrWhiteSpace(config.SourceServerApiKey))
        {
            _logger.LogWarning("Sync table update skipped: API key not configured");
            return;
        }

        var enabledMappings = config.LibraryMappings?.Where(m => m.IsEnabled).ToList() ?? new List<LibraryMapping>();
        if (enabledMappings.Count == 0)
        {
            _logger.LogDebug("Sync table update skipped: no enabled library mappings");
            return;
        }

        _logger.LogInformation("Starting sync table update from {SourceUrl}", config.SourceServerUrl);

        using var client = new SourceServerClient(
            plugin.LoggerFactory.CreateLogger<SourceServerClient>(),
            config.SourceServerUrl,
            config.SourceServerApiKey);

        var connectionResult = await client.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (!connectionResult.Success)
        {
            _logger.LogError("Failed to connect to source server at {SourceUrl}: {Error}",
                config.SourceServerUrl, connectionResult.ErrorMessage ?? "Unknown error");
            return;
        }

        var database = plugin.Database;
        var syncTableService = new SyncTableService(_logger, _libraryManager);

        // Progress tracking: 10% for init, 80% for processing items, 10% for finalization
        const double InitProgress = 10.0;
        const double ProcessingProgress = 80.0;

        progress.Report(0);

        // Get total item counts for all libraries to enable granular progress
        var totalItems = 0;

        foreach (var mapping in enabledMappings)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var count = await client.GetLibraryItemCountAsync(
                Guid.Parse(mapping.SourceLibraryId),
                cancellationToken).ConfigureAwait(false);

            totalItems += count;
        }

        progress.Report(InitProgress);

        // Process each library with item-level progress tracking
        var processedItems = 0;

        foreach (var mapping in enabledMappings)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            await syncTableService.ProcessLibraryAsync(
                client,
                database,
                mapping,
                config.DownloadNewContentMode,
                config.ReplaceExistingContentMode,
                config.DeleteMissingContentMode,
                config.DetectUpdatedFiles,
                config.ChangeDetectionPolicy,
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

        progress.Report(InitProgress + ProcessingProgress);

        // Resolve LocalItemIds for synced items that don't have them yet
        syncTableService.ResolveLocalItemIds(database);

        progress.Report(100);
        _logger.LogInformation("Sync table update completed");
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(10).Ticks
            }
        };
    }
}
