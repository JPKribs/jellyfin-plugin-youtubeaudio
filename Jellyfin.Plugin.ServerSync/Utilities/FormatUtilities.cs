namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Utility methods for formatting values.
/// </summary>
public static class FormatUtilities
{
    /// <summary>
    /// Formats bytes to human-readable string.
    /// </summary>
    /// <param name="bytes">The number of bytes to format.</param>
    /// <returns>A formatted string like "1.50 GB".</returns>
    public static string FormatBytes(long bytes)
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
