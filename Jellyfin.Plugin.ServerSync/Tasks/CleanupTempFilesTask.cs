using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Utilities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Tasks;

// CleanupTempFilesTask
// Scheduled task to clean up orphaned temporary files from failed downloads.
public class CleanupTempFilesTask : IScheduledTask
{
    private readonly ILogger<CleanupTempFilesTask> _logger;

    // MaxTempFileAgeHours
    // Maximum age in hours before a temp file is considered orphaned.
    private const int MaxTempFileAgeHours = 24;

    public CleanupTempFilesTask(ILogger<CleanupTempFilesTask> logger)
    {
        _logger = logger;
    }

    public string Name => "Clean Temporary Directory";

    public string Key => "ServerSyncCleanupTempFiles";

    public string Description => "Removes orphaned temporary files from failed sync downloads.";

    public string Category => "Server Sync";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
        {
            return Task.CompletedTask;
        }

        var tempPath = plugin.GetTempDownloadPath();

        if (!Directory.Exists(tempPath))
        {
            _logger.LogDebug("Temp directory does not exist: {TempPath}", tempPath);
            return Task.CompletedTask;
        }

        try
        {
            var cutoffTime = DateTime.UtcNow.AddHours(-MaxTempFileAgeHours);
            var deletedCount = 0;
            var errorCount = 0;
            var totalBytes = 0L;

            string[] tempFiles;
            try
            {
                tempFiles = Directory.GetFiles(tempPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to enumerate temp directory: {TempPath}", tempPath);
                return Task.CompletedTask;
            }

            var totalFiles = tempFiles.Length;
            if (totalFiles == 0)
            {
                return Task.CompletedTask;
            }

            for (var i = 0; i < tempFiles.Length; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var file = tempFiles[i];

                try
                {
                    var fileInfo = new FileInfo(file);

                    // Delete files older than the cutoff time
                    if (fileInfo.LastWriteTimeUtc < cutoffTime)
                    {
                        var fileSize = fileInfo.Length;
                        fileInfo.Delete();
                        deletedCount++;
                        totalBytes += fileSize;
                        _logger.LogDebug("Deleted orphaned temp file: {FileName}", fileInfo.Name);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete temp file: {FilePath}", file);
                    errorCount++;
                }

                progress.Report((double)(i + 1) / totalFiles * 100);
            }

            if (deletedCount > 0)
            {
                _logger.LogInformation(
                    "Cleanup complete: deleted {Count} orphaned temp files ({Size})",
                    deletedCount,
                    FormatUtilities.FormatBytes(totalBytes));
            }

            if (errorCount > 0)
            {
                _logger.LogWarning("Failed to delete {Count} temp files", errorCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clean up temp directory: {TempPath}", tempPath);
        }

        return Task.CompletedTask;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromDays(1).Ticks
            }
        };
    }
}
