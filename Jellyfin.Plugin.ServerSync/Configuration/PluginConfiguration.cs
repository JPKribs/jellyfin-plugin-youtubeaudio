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

    /// <summary>
    /// Gets or sets the maximum download speed in MB/s. 0 = unlimited.
    /// </summary>
    public int MaxDownloadSpeedMbps { get; set; } = 0;

    public bool RequireApprovalToSync { get; set; }

    /// <summary>
    /// Gets or sets whether to detect updated files by comparing size and date.
    /// If true, files with different sizes or newer source dates will be re-queued.
    /// </summary>
    public bool DetectUpdatedFiles { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to delete local files that no longer exist on the source server.
    /// If RequireApprovalToSync is true, items will be set to Pending for manual deletion.
    /// Deletions only affect the local server, never the source server.
    /// </summary>
    public bool DeleteIfMissingFromSource { get; set; }
}
