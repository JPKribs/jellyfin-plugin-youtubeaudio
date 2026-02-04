namespace Jellyfin.Plugin.ServerSync.Models.Configuration;

/// <summary>
/// Result of an authentication attempt with username/password.
/// </summary>
public class TokenAuthenticationResult
{
    public bool Success { get; set; }

    /// <summary>
    /// The access token to use for API calls.
    /// </summary>
    public string? AccessToken { get; set; }

    /// <summary>
    /// The authenticated username.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// The server ID.
    /// </summary>
    public string? ServerId { get; set; }

    /// <summary>
    /// Error message if authentication failed.
    /// </summary>
    public string? ErrorMessage { get; set; }
}
