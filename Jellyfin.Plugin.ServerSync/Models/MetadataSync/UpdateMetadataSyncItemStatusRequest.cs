namespace Jellyfin.Plugin.ServerSync.Models.MetadataSync;

/// <summary>
/// Request model for updating a metadata sync item's status.
/// </summary>
public class UpdateMetadataSyncItemStatusRequest
{
    /// <summary>
    /// Gets or sets the database ID of the item to update.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Gets or sets the new status.
    /// </summary>
    public string Status { get; set; } = string.Empty;
}
