namespace Jellyfin.Plugin.ServerSync.Models.ContentSync;

/// <summary>
/// Information about a companion file (subtitle, etc.) for an item.
/// </summary>
public class CompanionFileInfo
{
    public string SourcePath { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string? Language { get; set; }

    public string? Codec { get; set; }

    public bool IsExternal { get; set; }

    public int StreamIndex { get; set; }
}
