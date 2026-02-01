using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.UserSync;

/// <summary>
/// Request for bulk user sync item operations.
/// </summary>
public class BulkUserSyncItemsRequest
{
    /// <summary>
    /// Gets or sets the list of database IDs.
    /// </summary>
    public List<long> Ids { get; set; } = new();
}
