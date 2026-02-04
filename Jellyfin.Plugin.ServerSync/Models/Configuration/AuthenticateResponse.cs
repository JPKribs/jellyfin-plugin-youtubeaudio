namespace Jellyfin.Plugin.ServerSync.Models.Configuration;

/// <summary>
/// Response from authentication attempt.
/// </summary>
public class AuthenticateResponse
{
    public bool Success { get; set; }

    /// <summary>
    /// The access token to use for API calls.
    /// </summary>
    public string? AccessToken { get; set; }

    /// <summary>
    /// The username that was authenticated.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Server name returned from the authentication.
    /// </summary>
    public string? ServerName { get; set; }

    /// <summary>
    /// Server ID returned from the authentication.
    /// </summary>
    public string? ServerId { get; set; }

    /// <summary>
    /// Error message if authentication failed.
    /// </summary>
    public string? Message { get; set; }
}
