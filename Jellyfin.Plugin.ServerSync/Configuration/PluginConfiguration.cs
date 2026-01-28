using System.Collections.Generic;
using Jellyfin.Plugin.ServerSync.Models;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ServerSync.Configuration;

// PluginConfiguration
// Configuration settings for the Server Sync plugin.
public class PluginConfiguration : BasePluginConfiguration
{
    public string SourceServerUrl { get; set; } = string.Empty;

    public string SourceServerApiKey { get; set; } = string.Empty;

    public string SourceServerName { get; set; } = string.Empty;

    public string SourceServerId { get; set; } = string.Empty;

    public bool EnableContentSync { get; set; }

    public List<LibraryMapping> LibraryMappings { get; set; } = new();

    public string? TempDownloadPath { get; set; }

    public bool IncludeCompanionFiles { get; set; } = true;

    public int MaxConcurrentDownloads { get; set; } = 2;

    // MaxDownloadSpeedMbps
    // Maximum download speed in MB/s (0 = unlimited).
    public int MaxDownloadSpeedMbps { get; set; } = 0;

    public bool RequireApprovalToSync { get; set; }

    // DetectUpdatedFiles
    // Re-queue files with size or date mismatches when enabled.
    public bool DetectUpdatedFiles { get; set; } = true;

    // DeleteIfMissingFromSource
    // Delete local files no longer present on source server.
    public bool DeleteIfMissingFromSource { get; set; }
}
