namespace Jellyfin.Plugin.ServerSync.Models.Configuration;

/// <summary>
/// Represents a mapping between a source server user and a local user.
/// Used for syncing watch history and user data between servers.
/// </summary>
public class UserMapping
{
    /// <summary>
    /// The user ID on the source server.
    /// </summary>
    public string SourceUserId { get; set; } = string.Empty;

    /// <summary>
    /// The username on the source server (for display purposes).
    /// </summary>
    public string SourceUserName { get; set; } = string.Empty;

    /// <summary>
    /// The user ID on the local server.
    /// </summary>
    public string LocalUserId { get; set; } = string.Empty;

    /// <summary>
    /// The username on the local server (for display purposes).
    /// </summary>
    public string LocalUserName { get; set; } = string.Empty;

    /// <summary>
    /// Whether this mapping is currently enabled.
    /// </summary>
    public bool IsEnabled { get; set; } = true;
}
