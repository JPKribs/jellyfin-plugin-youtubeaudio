using Jellyfin.Sdk.Generated.Models;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Utilities for working with Jellyfin media items.
/// </summary>
public static class MediaItemUtilities
{
    /// <summary>
    /// Extracts the file size from an item's media sources.
    /// </summary>
    /// <param name="item">The media item DTO.</param>
    /// <returns>The file size in bytes, or 0 if not available.</returns>
    public static long GetItemSize(BaseItemDto item)
    {
        if (item.MediaSources != null && item.MediaSources.Count > 0)
        {
            var firstSource = item.MediaSources[0];
            if (firstSource.Size.HasValue)
            {
                return firstSource.Size.Value;
            }
        }

        return 0;
    }
}
