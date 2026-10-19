using System.Collections.Generic;
using Jellyfin.Plugin.YouTubeAudio.Models;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.YouTubeAudio.Configuration;

/// <summary>
/// Configuration settings for the YouTube Audio plugin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets an optional override path for the yt-dlp binary.
    /// When empty, the plugin downloads and updates its own copy in the plugin folder, and only
    /// falls back to a system install when that download is impossible.
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
    /// Gets or sets a value indicating whether downloads are looked up in Apple's music catalog
    /// to fill in the album, album artist, release year, track number and genre.
    /// </summary>
    public bool LookUpCatalogMetadata { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the album cover from Apple's catalog is embedded
    /// in downloaded files.
    /// </summary>
    public bool EmbedCoverArt { get; set; } = true;

    /// <summary>
    /// Gets or sets the order of the text lines a generated cover can show, comma-separated:
    /// Artist, Album and Year. A string rather than a list, since Jellyfin's settings file
    /// would add a list's defaults to the saved items again on every load.
    /// </summary>
    public string GeneratedCoverTextOrder { get; set; } = "Album,Artist,Year";

    /// <summary>Gets or sets which of those lines a generated cover shows, comma-separated.</summary>
    public string GeneratedCoverText { get; set; } = "Album";

    /// <summary>
    /// Gets or sets what a generated cover's colours are drawn from: Album, AlbumYear or
    /// AlbumArtistYear. The same value always gives the same colours.
    /// </summary>
    public string GeneratedCoverColors { get; set; } = "Album";

    /// <summary>
    /// Gets or sets an optional override for the cache directory.
    /// When empty, defaults to {IApplicationPaths.DataPath}/youtubeaudio.
    /// </summary>
    public string CacheDirectoryOverride { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the ids of non-admin users approved to submit download links.
    /// Approved users can queue downloads but cannot import into the library.
    /// </summary>
    public List<string> ApprovedUserIds { get; set; } = new();

    /// <summary>
    /// Gets or sets the most videos an approved user may queue from one playlist link; larger
    /// playlists are refused. 0 means no limit. Administrators are never limited.
    /// </summary>
    public int MaxPublicPlaylistItems { get; set; } = 200;
}
