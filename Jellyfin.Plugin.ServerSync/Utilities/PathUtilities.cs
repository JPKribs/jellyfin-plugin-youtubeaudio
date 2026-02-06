using System;
using System.Collections.Generic;
using System.IO;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Utilities for path manipulation and translation.
/// </summary>
public static class PathUtilities
{
    private static readonly char[] PathSeparators = { '/', '\\' };

    /// <summary>
    /// Translates a source server path to the corresponding local path.
    /// </summary>
    /// <param name="sourcePath">The source server file path.</param>
    /// <param name="sourceRoot">The root path on the source server.</param>
    /// <param name="localRoot">The corresponding local root path.</param>
    /// <returns>The translated local path.</returns>
    public static string TranslatePath(string sourcePath, string sourceRoot, string localRoot)
    {
        if (string.IsNullOrEmpty(sourcePath))
        {
            return localRoot;
        }

        sourceRoot = sourceRoot.TrimEnd(PathSeparators);
        localRoot = localRoot.TrimEnd(PathSeparators);

        if (sourcePath.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase))
        {
            var relativePath = sourcePath.Substring(sourceRoot.Length).TrimStart(PathSeparators);

            var pathParts = relativePath.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (pathParts.Length > 0)
            {
                var result = localRoot;
                foreach (var part in pathParts)
                {
                    result = Path.Combine(result, part);
                }

                return result;
            }

            return localRoot;
        }

        // Fallback: just use the filename
        var fileName = Path.GetFileName(sourcePath);
        return Path.Combine(localRoot, fileName);
    }

    /// <summary>
    /// Checks whether a source path falls under any of the ignored path prefixes.
    /// Each ignored path is relative to the source root (e.g., "The Simpsons" or "The Simpsons/Season 2").
    /// A trailing <c>*</c> wildcard matches any folder name that starts with the given prefix
    /// (e.g., "Star Wars*" matches "Star Wars", "Star Wars Episode IV", etc.).
    /// </summary>
    /// <param name="sourcePath">The full source server path of the item.</param>
    /// <param name="sourceRootPath">The root path of the library on the source server.</param>
    /// <param name="ignoredPaths">List of source-relative folder paths to ignore.</param>
    /// <returns>True if the source path matches any ignored path prefix.</returns>
    public static bool IsPathIgnored(string sourcePath, string sourceRootPath, List<string>? ignoredPaths)
    {
        if (ignoredPaths == null || ignoredPaths.Count == 0)
        {
            return false;
        }

        if (string.IsNullOrEmpty(sourcePath))
        {
            return false;
        }

        // Normalize source path separators to forward slash for consistent comparison
        var normalizedSourcePath = sourcePath.Replace('\\', '/').TrimEnd('/');
        var normalizedRoot = sourceRootPath.TrimEnd(PathSeparators).Replace('\\', '/');

        foreach (var ignoredPath in ignoredPaths)
        {
            if (string.IsNullOrWhiteSpace(ignoredPath))
            {
                continue;
            }

            var trimmed = ignoredPath.Trim().TrimStart(PathSeparators).Replace('\\', '/');
            var isWildcard = trimmed.EndsWith('*');

            if (isWildcard)
            {
                // Wildcard mode: "Star Wars*" matches any folder starting with "Star Wars"
                var pattern = trimmed.TrimEnd('*').TrimEnd(PathSeparators);
                var fullWildcardPrefix = normalizedRoot + "/" + pattern;

                // Match if the source path starts with the prefix (the folder name can continue with any characters)
                if (normalizedSourcePath.StartsWith(fullWildcardPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else
            {
                // Exact folder mode: "The Simpsons" matches only that exact folder and its children
                var normalizedIgnored = trimmed.TrimEnd(PathSeparators);
                var fullIgnoredPrefix = normalizedRoot + "/" + normalizedIgnored;

                // Check if source path starts with the ignored prefix (exact folder match or deeper)
                if (normalizedSourcePath.StartsWith(fullIgnoredPrefix, StringComparison.OrdinalIgnoreCase)
                    && (normalizedSourcePath.Length == fullIgnoredPrefix.Length
                        || normalizedSourcePath[fullIgnoredPrefix.Length] == '/'))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
