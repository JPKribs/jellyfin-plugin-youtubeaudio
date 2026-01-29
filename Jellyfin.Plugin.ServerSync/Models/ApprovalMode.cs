namespace Jellyfin.Plugin.ServerSync.Models;

// ApprovalMode
// Controls how sync operations are handled for different action types.
public enum ApprovalMode
{
    // Operations are automatically queued and processed without user intervention.
    Enabled = 0,

    // Operations require manual approval before being processed.
    RequireApproval = 1,

    // Operations are not performed at all.
    Disabled = 2
}
