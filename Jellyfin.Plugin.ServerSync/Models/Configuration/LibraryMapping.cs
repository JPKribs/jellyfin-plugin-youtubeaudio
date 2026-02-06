using System.Collections.Generic;

namespace Jellyfin.Plugin.ServerSync.Models.Configuration;

/// <summary>
/// Represents a mapping between a source library and a local library.
/// Used for syncing content and watch history between servers.
/// </summary>
public class LibraryMapping
{
    /// <summary>
    /// The library ID on the source server.
    /// </summary>
    public string SourceLibraryId { get; set; } = string.Empty;

    /// <summary>
    /// The library name on the source server (for display purposes).
    /// </summary>
    public string SourceLibraryName { get; set; } = string.Empty;

    /// <summary>
    /// The root path of the library on the source server.
    /// </summary>
    public string SourceRootPath { get; set; } = string.Empty;

    /// <summary>
    /// The library ID on the local server.
    /// </summary>
    public string LocalLibraryId { get; set; } = string.Empty;

    /// <summary>
    /// The library name on the local server (for display purposes).
    /// </summary>
    public string LocalLibraryName { get; set; } = string.Empty;

    /// <summary>
    /// The root path where files will be stored locally.
    /// </summary>
    public string LocalRootPath { get; set; } = string.Empty;

    /// <summary>
    /// Whether this mapping is currently enabled.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Source-relative folder paths to ignore during sync.
    /// Each entry is relative to <see cref="SourceRootPath"/> (e.g., "The Simpsons" or "The Simpsons/Season 2").
    /// A trailing <c>*</c> wildcard matches any folder starting with the prefix (e.g., "Star Wars*").
    /// Items whose source path falls under any ignored path are skipped across Content, Metadata, and History sync.
    /// </summary>
    public List<string> IgnoredPaths { get; set; } = new();
}
