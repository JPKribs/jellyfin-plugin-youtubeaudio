using System.Collections.Generic;

namespace Jellyfin.Plugin.YouTubeAudio.Models;

/// <summary>
/// Request body for importing downloaded files to the library.
/// </summary>
public class ImportRequest
{
    /// <summary>Gets or sets the list of queue item IDs to import.</summary>
    public List<string> Ids { get; set; } = new();
}
