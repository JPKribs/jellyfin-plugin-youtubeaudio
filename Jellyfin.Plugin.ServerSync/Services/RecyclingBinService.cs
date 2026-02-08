using System;
using System.IO;
using Jellyfin.Plugin.ServerSync.Utilities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// RecyclingBinService
/// Handles moving files to the recycling bin and cleaning up old files.
/// </summary>
public static class RecyclingBinService
{
    /// <summary>
    /// MoveToRecyclingBin
    /// Moves a file to the recycling bin with a timestamped name.
    /// </summary>
    /// <param name="filePath">Path to the file to move.</param>
    /// <param name="recyclingBinPath">Path to the recycling bin directory.</param>
    /// <param name="logger">Logger for operation output.</param>
    /// <returns>True if successful, false otherwise.</returns>
    public static bool MoveToRecyclingBin(string filePath, string recyclingBinPath, ILogger logger)
    {
        if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(recyclingBinPath))
        {
            return false;
        }

        if (!File.Exists(filePath))
        {
            logger.LogWarning("Cannot move to recycling bin - file does not exist: {FilePath}", filePath);
            return false;
        }

        try
        {
            // Ensure recycling bin directory exists
            if (!Directory.Exists(recyclingBinPath))
            {
                Directory.CreateDirectory(recyclingBinPath);
                logger.LogInformation("Created recycling bin directory: {Path}", recyclingBinPath);
            }

            // Generate recycled file name: path.with.periods_2026-01-29_17-30-45.ext
            var recycledFileName = FileOperationUtilities.GenerateRecycledFileName(filePath);
            var destinationPath = Path.Combine(recyclingBinPath, recycledFileName);

            // Handle case where destination already exists (unlikely but possible)
            if (File.Exists(destinationPath))
            {
                var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
                var ext = Path.GetExtension(recycledFileName);
                var nameWithoutExt = Path.GetFileNameWithoutExtension(recycledFileName);
                recycledFileName = $"{nameWithoutExt}_{uniqueSuffix}{ext}";
                destinationPath = Path.Combine(recyclingBinPath, recycledFileName);
            }

            File.Move(filePath, destinationPath);
            logger.LogInformation("Moved to recycling bin: {FileName} -> {RecycledName}", Path.GetFileName(filePath), recycledFileName);

            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to move file to recycling bin: {FilePath}", filePath);
            return false;
        }
    }

    /// <summary>
    /// MoveWithCompanionsToRecyclingBin
    /// Moves a file and its companion files (subtitles, etc.) to the recycling bin.
    /// </summary>
    /// <param name="filePath">Path to the main file to move.</param>
    /// <param name="recyclingBinPath">Path to the recycling bin directory.</param>
    /// <param name="logger">Logger for operation output.</param>
    /// <returns>True if the main file was moved successfully.</returns>
    public static bool MoveWithCompanionsToRecyclingBin(string filePath, string recyclingBinPath, ILogger logger)
    {
        if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(recyclingBinPath))
        {
            return false;
        }

        // Move the main file first
        var mainSuccess = MoveToRecyclingBin(filePath, recyclingBinPath, logger);

        // Move companion files
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return mainSuccess;
            }

            var fileNameWithoutExt = Path.GetFileNameWithoutExtension(filePath);

            foreach (var ext in FileOperationUtilities.CompanionExtensions)
            {
                var pattern = $"{fileNameWithoutExt}*{ext}";
                try
                {
                    var companionFiles = Directory.GetFiles(directory, pattern);
                    foreach (var companionFile in companionFiles)
                    {
                        MoveToRecyclingBin(companionFile, recyclingBinPath, logger);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Error searching for companion files with extension {Extension}", ext);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error processing companion files for {FilePath}", filePath);
        }

        return mainSuccess;
    }

    /// <summary>
    /// CleanupExpiredFiles
    /// Deletes files in the recycling bin older than the retention period.
    /// </summary>
    /// <param name="recyclingBinPath">Path to the recycling bin directory.</param>
    /// <param name="retentionDays">Number of days to retain files before deletion.</param>
    /// <param name="logger">Logger for operation output.</param>
    /// <returns>Number of files deleted.</returns>
    public static int CleanupExpiredFiles(string recyclingBinPath, int retentionDays, ILogger logger)
    {
        if (string.IsNullOrEmpty(recyclingBinPath) || !Directory.Exists(recyclingBinPath))
        {
            return 0;
        }

        var cutoffTime = DateTime.UtcNow.AddDays(-retentionDays);
        var deletedCount = 0;
        var errorCount = 0;
        long totalBytes = 0;

        try
        {
            var files = Directory.GetFiles(recyclingBinPath);

            foreach (var file in files)
            {
                try
                {
                    var fileInfo = new FileInfo(file);

                    // Check if file is older than retention period
                    // Use the timestamp from the filename if possible, otherwise use file modification time
                    var fileTime = FileOperationUtilities.ExtractTimestampFromFileName(file) ?? fileInfo.LastWriteTimeUtc;

                    if (fileTime < cutoffTime)
                    {
                        var fileSize = fileInfo.Length;
                        fileInfo.Delete();
                        deletedCount++;
                        totalBytes += fileSize;
                        logger.LogDebug("Permanently deleted from recycling bin: {FileName}", fileInfo.Name);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to delete recycled file: {FilePath}", file);
                    errorCount++;
                }
            }

            if (deletedCount > 0)
            {
                logger.LogInformation(
                    "Recycling bin cleanup: permanently deleted {Count} files ({Size})",
                    deletedCount,
                    FormatUtilities.FormatBytes(totalBytes));
            }

            if (errorCount > 0)
            {
                logger.LogWarning("Failed to delete {Count} files from recycling bin", errorCount);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to clean up recycling bin: {Path}", recyclingBinPath);
        }

        return deletedCount;
    }
}
