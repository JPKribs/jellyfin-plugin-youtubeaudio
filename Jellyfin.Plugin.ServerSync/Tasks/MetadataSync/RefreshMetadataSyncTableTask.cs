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
/// Scheduled task to refresh the metadata sync table from source and local servers.
/// </summary>
public class RefreshMetadataSyncTableTask : IScheduledTask
{
    private readonly ILogger<RefreshMetadataSyncTableTask> _logger;
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="RefreshMetadataSyncTableTask"/> class.
    /// </summary>
    public RefreshMetadataSyncTableTask(
        ILogger<RefreshMetadataSyncTableTask> logger,
        ILibraryManager libraryManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
    }

    /// <inheritdoc />
    public string Name => "Refresh Sync Table";

    /// <inheritdoc />
    public string Key => "ServerSyncRefreshMetadataTable";

    /// <inheritdoc />
    public string Description => "Scans source and local servers for metadata differences and updates the metadata sync table.";

    /// <inheritdoc />
    public string Category => "Metadata Sync";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return;
        }

        var config = plugin.Configuration;

        // Check if metadata sync is enabled
        if (!config.EnableMetadataSync)
        {
            return;
        }

        // Validate source server configuration
        if (string.IsNullOrWhiteSpace(config.SourceServerUrl))
        {
            _logger.LogWarning("Metadata sync skipped: source server URL not configured");
            return;
        }

        if (string.IsNullOrWhiteSpace(config.SourceServerApiKey))
        {
            _logger.LogWarning("Metadata sync skipped: API key not configured");
            return;
        }

        // Get enabled library mappings
        var enabledLibraryMappings = config.LibraryMappings?.Where(m => m.IsEnabled).ToList() ?? new List<LibraryMapping>();

        if (enabledLibraryMappings.Count == 0)
        {
            _logger.LogDebug("Metadata sync skipped: no enabled library mappings");
            return;
        }

        // Get enabled category flags
        var syncMetadata = config.MetadataSyncMetadata;
        var syncImages = config.MetadataSyncImages;
        var syncPeople = config.MetadataSyncPeople;

        if (!syncMetadata && !syncImages && !syncPeople)
        {
            _logger.LogDebug("Metadata sync skipped: no categories enabled");
            return;
        }

        _logger.LogInformation("Starting metadata sync table refresh from {SourceUrl}", config.SourceServerUrl);

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
        var metadataService = new MetadataSyncTableService(_logger, _libraryManager);

        // Progress tracking
        const double InitProgress = 10.0;
        const double ProcessingProgress = 80.0;

        progress.Report(0);

        // Get total item count for progress tracking
        var totalItems = await metadataService.GetTotalItemCountAsync(
            client,
            enabledLibraryMappings,
            cancellationToken).ConfigureAwait(false);

        progress.Report(InitProgress);

        // Process each library mapping
        var processedItems = 0;

        foreach (var libraryMapping in enabledLibraryMappings)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            await metadataService.ProcessLibraryAsync(
                client,
                database,
                libraryMapping,
                syncMetadata,
                syncImages,
                syncPeople,
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

        progress.Report(100);

        // Update last sync time
        config.LastMetadataSyncTime = DateTime.UtcNow;
        plugin.SaveConfiguration();

        _logger.LogInformation("Metadata sync table refresh completed");
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(12).Ticks
            }
        };
    }
}
