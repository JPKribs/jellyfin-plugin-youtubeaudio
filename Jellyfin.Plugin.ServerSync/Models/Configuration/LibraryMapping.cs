namespace Jellyfin.Plugin.ServerSync.Models.Configuration;

/// <summary>
/// Represents a mapping between a source library and a local library.
/// </summary>
public class LibraryMapping
{
    public string SourceLibraryId { get; set; } = string.Empty;

    public string SourceLibraryName { get; set; } = string.Empty;

    public string SourceRootPath { get; set; } = string.Empty;

    public string LocalLibraryId { get; set; } = string.Empty;

    public string LocalLibraryName { get; set; } = string.Empty;

    public string LocalRootPath { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;
}
