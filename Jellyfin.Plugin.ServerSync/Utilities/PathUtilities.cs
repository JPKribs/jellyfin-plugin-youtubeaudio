using System;
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
}
