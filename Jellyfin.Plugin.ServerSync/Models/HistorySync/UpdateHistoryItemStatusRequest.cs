namespace Jellyfin.Plugin.ServerSync.Models.HistorySync;

/// <summary>
/// Request to update a history sync item's status.
/// </summary>
public class UpdateHistoryItemStatusRequest
{
    /// <summary>
    /// Gets or sets the database ID.
    /// Preferred way to identify the item.
    /// </summary>
    public long? Id { get; set; }

    /// <summary>
    /// Gets or sets the source user ID (legacy).
    /// </summary>
    public string SourceUserId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source item ID (legacy).
    /// </summary>
    public string SourceItemId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the new status.
    /// </summary>
    public string Status { get; set; } = string.Empty;
}
