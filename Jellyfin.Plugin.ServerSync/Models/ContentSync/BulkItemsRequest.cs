using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Plugin.ServerSync.Models.ContentSync;

/// <summary>
/// Request for bulk item operations.
/// </summary>
public class BulkItemsRequest
{
    [Required]
    public List<string> SourceItemIds { get; set; } = new();
}
