using System;
using System.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services;

// RecyclingBinService
// Handles moving files to the recycling bin and cleaning up old files.
public static class RecyclingBinService
{
    // MoveToRecyclingBin
    // Moves a file to the recycling bin with a timestamped name.
    // Returns true if successful, false otherwise.
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
            var recycledFileName = GenerateRecycledFileName(filePath);
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

    // MoveWithCompanionsToRecyclingBin
    // Moves a file and its companion files (subtitles, etc.) to the recycling bin.
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
            var companionExtensions = new[] { ".srt", ".sub", ".ass", ".ssa", ".vtt", ".nfo", ".jpg", ".png" };

            foreach (var ext in companionExtensions)
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
                catch
                {
                    // Ignore pattern match errors
                }
            }
        }
        catch
        {
            // Ignore companion file errors - main file was handled
        }

        return mainSuccess;
    }

    // CleanupExpiredFiles
    // Deletes files in the recycling bin older than the retention period.
    // Returns the number of files deleted.
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
                    var fileTime = ExtractTimestampFromFileName(file) ?? fileInfo.LastWriteTimeUtc;

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
                    FormatBytes(totalBytes));
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

    // GenerateRecycledFileName
    // Generates a recycled file name with path encoded and timestamp.
    // Format: path.to.file_2026-01-29_17-30-45.ext
    private static string GenerateRecycledFileName(string originalPath)
    {
        var fileName = Path.GetFileName(originalPath);
        var directory = Path.GetDirectoryName(originalPath) ?? string.Empty;

        // Encode the directory path: replace path separators with periods
        var encodedPath = directory
            .Replace(Path.DirectorySeparatorChar, '.')
            .Replace(Path.AltDirectorySeparatorChar, '.')
            .Replace(':', '.')
            .Trim('.');

        // Get timestamp
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss");

        // Get file parts
        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        // Combine: encodedPath.filename_timestamp.ext
        if (!string.IsNullOrEmpty(encodedPath))
        {
            return $"{encodedPath}.{nameWithoutExt}_{timestamp}{extension}";
        }

        return $"{nameWithoutExt}_{timestamp}{extension}";
    }

    // ExtractTimestampFromFileName
    // Attempts to extract the UTC timestamp from a recycled file name.
    private static DateTime? ExtractTimestampFromFileName(string filePath)
    {
        try
        {
            var fileName = Path.GetFileNameWithoutExtension(filePath);

            // Look for timestamp pattern: _YYYY-MM-DD_HH-mm-ss at the end
            var lastUnderscore = fileName.LastIndexOf('_');
            if (lastUnderscore < 0) return null;

            var secondLastUnderscore = fileName.LastIndexOf('_', lastUnderscore - 1);
            if (secondLastUnderscore < 0) return null;

            var timestampPart = fileName[(secondLastUnderscore + 1)..];

            // Parse: 2026-01-29_17-30-45
            if (DateTime.TryParseExact(
                timestampPart,
                "yyyy-MM-dd_HH-mm-ss",
                null,
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out var timestamp))
            {
                return timestamp;
            }
        }
        catch
        {
            // Ignore parsing errors
        }

        return null;
    }

    // FormatBytes
    // Formats bytes to human-readable string.
    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        var unitIndex = 0;

        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size:F2} {units[unitIndex]}";
    }
}
