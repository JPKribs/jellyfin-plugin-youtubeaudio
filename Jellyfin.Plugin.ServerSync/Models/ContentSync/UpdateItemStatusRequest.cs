using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Plugin.ServerSync.Models.ContentSync;

/// <summary>
/// Request to update an item's status.
/// </summary>
public class UpdateItemStatusRequest
{
    [Required]
    public string SourceItemId { get; set; } = string.Empty;

    [Required]
    public string Status { get; set; } = string.Empty;
}
