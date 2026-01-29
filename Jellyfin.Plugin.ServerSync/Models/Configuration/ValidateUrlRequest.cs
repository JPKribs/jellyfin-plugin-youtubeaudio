using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Plugin.ServerSync.Models.Configuration;

/// <summary>
/// Request to validate a server URL.
/// </summary>
public class ValidateUrlRequest
{
    [Required]
    public string Url { get; set; } = string.Empty;
}
