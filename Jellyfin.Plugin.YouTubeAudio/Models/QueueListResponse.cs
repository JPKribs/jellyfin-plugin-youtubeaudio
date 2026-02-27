using System.Collections.Generic;

namespace Jellyfin.Plugin.YouTubeAudio.Models;

/// <summary>
/// Response wrapper for paginated queue item lists.
/// </summary>
public class QueueListResponse
{
    /// <summary>Gets or sets the list of queue items.</summary>
    public List<QueueItemDto> Items { get; set; } = new();

    /// <summary>Gets or sets the total count of items.</summary>
    public int TotalCount { get; set; }
}
