using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.MetadataSync;

/// <summary>
/// Request model for bulk metadata sync item operations.
/// </summary>
public class BulkMetadataSyncItemsRequest
{
    /// <summary>
    /// Gets or sets the list of item IDs to operate on.
    /// </summary>
    public List<long> Ids { get; set; } = new();

    /// <summary>
    /// Gets or sets the new status to set.
    /// </summary>
    public string Status { get; set; } = string.Empty;
}
