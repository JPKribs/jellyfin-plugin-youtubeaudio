using Jellyfin.Plugin.YouTubeAudio.Models;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.YouTubeAudio;

/// <summary>
/// Configuration settings for the YouTube Audio plugin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets an optional override path for the yt-dlp binary.
    /// When empty, the plugin searches the system PATH and common install locations automatically.
    /// </summary>
    public string YtDlpPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the preferred audio format for downloads.</summary>
    public AudioFormat AudioFormat { get; set; } = AudioFormat.Opus;

    /// <summary>Gets or sets the selected Jellyfin music library ID.</summary>
    public string MusicLibraryId { get; set; } = string.Empty;

    /// <summary>Gets or sets the resolved filesystem path of the selected music library.</summary>
    public string MusicLibraryPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether duplicate files should be replaced during import.
    /// When false, files that already exist at the destination are skipped.
    /// </summary>
    public bool ReplaceDuplicates { get; set; } = true;

    /// <summary>
    /// Gets or sets an optional override for the cache directory.
    /// When empty, defaults to {IApplicationPaths.DataPath}/youtubeaudio.
    /// </summary>
    public string CacheDirectoryOverride { get; set; } = string.Empty;
}
