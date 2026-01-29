namespace Jellyfin.Plugin.ServerSync.Models.Configuration;

/// <summary>
/// Controls how sync operations are handled for different action types.
/// </summary>
public enum ApprovalMode
{
    /// <summary>Operations are automatically queued and processed without user intervention.</summary>
    Enabled = 0,

    /// <summary>Operations require manual approval before being processed.</summary>
    RequireApproval = 1,

    /// <summary>Operations are not performed at all.</summary>
    Disabled = 2
}
