using System.Collections.Generic;

namespace Jellyfin.Plugin.YouTubeAudio.Models;

/// <summary>
/// Result of an import operation.
/// </summary>
public class ImportResult
{
    /// <summary>Gets or sets the list of successfully imported item IDs.</summary>
    public List<string> ImportedIds { get; set; } = new();

    /// <summary>Gets or sets the list of item IDs that replaced existing files.</summary>
    public List<string> ReplacedIds { get; set; } = new();

    /// <summary>Gets or sets the list of item IDs skipped because duplicates exist.</summary>
    public List<string> SkippedIds { get; set; } = new();

    /// <summary>Gets or sets any errors that occurred during import.</summary>
    public List<string> Errors { get; set; } = new();
}
