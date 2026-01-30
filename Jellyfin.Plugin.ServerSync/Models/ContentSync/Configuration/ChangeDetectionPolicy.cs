namespace Jellyfin.Plugin.ServerSync.Models.ContentSync.Configuration;

/// <summary>
/// Controls how file changes are detected between source and local servers.
/// Path is always checked as the primary identifier.
/// </summary>
public enum ChangeDetectionPolicy
{
    /// <summary>Detect changes using file size only (most stable, recommended).</summary>
    SizeOnly = 0,

    /// <summary>Detect changes using ETag only (sensitive to metadata changes).</summary>
    ETagOnly = 1,

    /// <summary>Detect changes using both size and ETag.</summary>
    Both = 2
}
