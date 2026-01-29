namespace Jellyfin.Plugin.ServerSync.Models.ContentSync;

/// <summary>
/// Information about disk space availability.
/// </summary>
public class DiskSpaceInfo
{
    public long FreeBytes { get; set; }

    public long TotalBytes { get; set; }

    public long RequiredBytes { get; set; }

    public bool IsSufficient { get; set; }

    public string Path { get; set; } = string.Empty;
}
