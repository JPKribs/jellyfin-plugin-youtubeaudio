using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeAudio.Services;

/// <summary>
/// Cleans the download cache once when the server starts (see <see cref="DownloadService.CleanUpCache"/>).
/// </summary>
public sealed class CacheCleanupService : IHostedService
{
    private readonly DownloadService _downloadService;
    private readonly ILogger<CacheCleanupService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CacheCleanupService"/> class.
    /// </summary>
    /// <param name="downloadService">The download service that owns the cache.</param>
    /// <param name="logger">The logger.</param>
    public CacheCleanupService(DownloadService downloadService, ILogger<CacheCleanupService> logger)
    {
        _downloadService = downloadService;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Off the startup path: the sweep touches the disk and must never delay or fail startup.
        _ = Task.Run(
            () =>
            {
                try
                {
                    _downloadService.CleanUpCache();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not clean up the download cache");
                }
            },
            CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
