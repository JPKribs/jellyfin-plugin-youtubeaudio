namespace Jellyfin.Plugin.ServerSync.Models.UserSync;

/// <summary>
/// Request to update a user sync item's status.
/// </summary>
public class UpdateUserSyncItemStatusRequest
{
    /// <summary>
    /// Gets or sets the database ID.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Gets or sets the new status.
    /// </summary>
    public string Status { get; set; } = string.Empty;
}
