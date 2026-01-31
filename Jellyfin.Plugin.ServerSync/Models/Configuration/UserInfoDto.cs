namespace Jellyfin.Plugin.ServerSync.Models.Configuration;

/// <summary>
/// Simple DTO for user information used in mapping UI.
/// </summary>
public class UserInfoDto
{
    /// <summary>
    /// The user's unique identifier.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// The user's display name.
    /// </summary>
    public string Name { get; set; } = string.Empty;
}
