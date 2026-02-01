using System;
using Jellyfin.Plugin.ServerSync.Models.Common;

namespace Jellyfin.Plugin.ServerSync.Models.MetadataSync;

/// <summary>
/// Response model for metadata sync status.
/// </summary>
public class MetadataSyncStatusResponse : BaseSyncStatusResponse
{
    /// <summary>
    /// Gets or sets the last sync time.
    /// </summary>
    public DateTime? LastSyncTime { get; set; }

    /// <summary>
    /// Gets or sets the number of libraries mapped.
    /// </summary>
    public int LibraryCount { get; set; }
}
