using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.ServerSync.Models.Configuration;

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
                    // Block path traversal from untrusted source paths
                    if (part == ".." || part == ".")
                    {
                        continue;
                    }

                    result = Path.Combine(result, part);
                }

                // Final safety check: ensure the result is still under localRoot
                var normalizedResult = Path.GetFullPath(result);
                var normalizedRoot = Path.GetFullPath(localRoot + Path.DirectorySeparatorChar);
                if (!normalizedResult.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(normalizedResult, Path.GetFullPath(localRoot), StringComparison.OrdinalIgnoreCase))
                {
                    // Path escaped the local root — return safe fallback
                    return Path.Combine(localRoot, Path.GetFileName(sourcePath));
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
    /// Determines whether an item should be filtered (skipped) based on the library's filter mode
    /// and filtered items list.
    /// </summary>
    /// <param name="sourcePath">The full source server path of the item.</param>
    /// <param name="sourceRootPath">The root path of the library on the source server.</param>
    /// <param name="filterMode">The filter mode (AllowAll, Whitelist, Blacklist).</param>
    /// <param name="filteredItems">List of filtered items with paths.</param>
    /// <returns>True if the item should be SKIPPED (filtered out).</returns>
    public static bool IsItemFiltered(
        string sourcePath,
        string sourceRootPath,
        LibraryFilterMode filterMode,
        List<FilteredItem>? filteredItems)
    {
        if (filterMode == LibraryFilterMode.AllowAll || filteredItems == null || filteredItems.Count == 0)
        {
            return false;
        }

        if (string.IsNullOrEmpty(sourcePath))
        {
            // No path to match — in whitelist mode this means skip (not in list), in blacklist mode allow
            return filterMode == LibraryFilterMode.Whitelist;
        }

        var normalizedSourcePath = sourcePath.Replace('\\', '/').TrimEnd('/');
        var normalizedRoot = sourceRootPath.TrimEnd(PathSeparators).Replace('\\', '/');

        bool matchesAny = false;
        foreach (var fi in filteredItems)
        {
            if (string.IsNullOrEmpty(fi.Path))
            {
                continue;
            }

            var normalizedFilterPath = fi.Path.Replace('\\', '/').TrimEnd('/');

            // Build the full path to compare against
            string fullFilterPrefix;
            if (normalizedFilterPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                // Path is already absolute — use as-is
                fullFilterPrefix = normalizedFilterPath;
            }
            else
            {
                // Path is relative to root
                fullFilterPrefix = normalizedRoot + "/" + normalizedFilterPath.TrimStart('/');
            }

            // Check if the source path is this item or a child of this item
            if (normalizedSourcePath.StartsWith(fullFilterPrefix, StringComparison.OrdinalIgnoreCase)
                && (normalizedSourcePath.Length == fullFilterPrefix.Length
                    || normalizedSourcePath[fullFilterPrefix.Length] == '/'))
            {
                matchesAny = true;
                break;
            }
        }

        return filterMode switch
        {
            LibraryFilterMode.Whitelist => !matchesAny, // Skip if NOT in whitelist
            LibraryFilterMode.Blacklist => matchesAny,  // Skip if IN blacklist
            _ => false
        };
    }
}
