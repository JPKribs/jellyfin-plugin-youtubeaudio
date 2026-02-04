using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Plugin.ServerSync.Models.Configuration;

/// <summary>
/// Request to authenticate with a source server using username and password.
/// </summary>
public class AuthenticateRequest
{
    [Required]
    public string ServerUrl { get; set; } = string.Empty;

    [Required]
    public string Username { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;
}
