using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Utilities;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// Service for validating file operations before execution.
/// </summary>
public static class FileValidationService
{
    /// <summary>
    /// Validates a path for download operations.
    /// Checks for empty path, path traversal, library boundaries, and collisions.
    /// </summary>
    /// <param name="localPath">The local path to validate.</param>
    /// <param name="sourceItemId">Source item ID for collision checking.</param>
    /// <param name="config">Plugin configuration for library boundaries.</param>
    /// <param name="database">Sync database for collision checking.</param>
    /// <returns>Validation result indicating if path is valid.</returns>
    public static PathValidationResult ValidatePath(
        string? localPath,
        string sourceItemId,
        PluginConfiguration config,
        SyncDatabase database)
    {
        if (string.IsNullOrEmpty(localPath))
        {
            return new PathValidationResult(false, "No local path configured");
        }

        // Check for path traversal
        var normalizedPath = Path.GetFullPath(localPath);
        if (!normalizedPath.Equals(localPath, StringComparison.OrdinalIgnoreCase))
        {
            // Path was normalized, check if it's still within allowed boundaries
            var isWithinLibrary = config.LibraryMappings
                .Where(m => m.IsEnabled && !string.IsNullOrEmpty(m.LocalRootPath))
                .Any(m => normalizedPath.StartsWith(m.LocalRootPath, StringComparison.OrdinalIgnoreCase));

            if (!isWithinLibrary)
            {
                return new PathValidationResult(false, "Path traversal detected - path escapes library boundaries");
            }
        }

        // Check for path collision
        if (database.CheckPathCollision(localPath, sourceItemId))
        {
            return new PathValidationResult(false, "Path collision - another item is already using this path");
        }

        return new PathValidationResult(true, null);
    }

    /// <summary>
    /// Validates that a path is within one of the configured library paths.
    /// </summary>
    /// <param name="path">Path to validate.</param>
    /// <param name="config">Plugin configuration containing library mappings.</param>
    /// <returns>True if path is within a configured library.</returns>
    public static bool IsPathWithinLibrary(string? path, PluginConfiguration config)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var normalizedPath = Path.GetFullPath(path);

        foreach (var mapping in config.LibraryMappings.Where(m => m.IsEnabled && !string.IsNullOrEmpty(m.LocalRootPath)))
        {
            var normalizedLibraryPath = Path.GetFullPath(mapping.LocalRootPath);
            if (normalizedPath.StartsWith(normalizedLibraryPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if a download should be skipped because the local file already exists and is valid.
    /// </summary>
    /// <param name="item">Sync item to check.</param>
    /// <param name="skipReason">Output parameter with reason for skipping.</param>
    /// <returns>True if download should be skipped.</returns>
    public static bool ShouldSkipDownload(SyncItem item, out string? skipReason)
    {
        skipReason = null;

        if (string.IsNullOrEmpty(item.LocalPath))
        {
            return false;
        }

        if (!File.Exists(item.LocalPath))
        {
            return false;
        }

        try
        {
            var localInfo = new FileInfo(item.LocalPath);

            // If sizes match, the file is likely valid
            if (item.SourceSize > 0 && localInfo.Length == item.SourceSize)
            {
                skipReason = $"Local file already exists with matching size ({FormatUtilities.FormatBytes(localInfo.Length)})";
                return true;
            }

            // Size mismatch - needs re-download
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if write permission exists for a file's target directory.
    /// Creates the directory if it doesn't exist.
    /// </summary>
    /// <param name="filePath">Target file path.</param>
    /// <returns>True if write permission exists.</returns>
    public static bool HasWritePermission(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directory))
        {
            return false;
        }

        return FileOperationUtilities.HasWritePermission(directory);
    }
}
