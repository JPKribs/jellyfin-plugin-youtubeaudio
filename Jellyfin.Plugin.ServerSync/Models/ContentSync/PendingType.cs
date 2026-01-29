namespace Jellyfin.Plugin.ServerSync.Models.ContentSync;

/// <summary>
/// Specifies the type of pending operation for items awaiting approval.
/// </summary>
public enum PendingType
{
    /// <summary>New item awaiting approval for initial download.</summary>
    Download = 0,

    /// <summary>Existing item awaiting approval to be replaced with updated version.</summary>
    Replacement = 1,

    /// <summary>Item awaiting approval for deletion from local server.</summary>
    Deletion = 2
}
