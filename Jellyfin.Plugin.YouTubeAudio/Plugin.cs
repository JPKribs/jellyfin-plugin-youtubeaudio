using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeAudio;

/// <summary>
/// Main plugin entry point for YouTube Audio.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly ILogger<Plugin> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        ILogger<Plugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _logger = logger;

        _logger.LogInformation("YouTube Audio plugin initialized");
    }

    /// <inheritdoc />
    public override string Name => "YouTube Audio";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("7323ea64-a200-4265-ab8f-e7ae27d06c38");

    /// <inheritdoc />
    public override string Description => "Download YouTube audio, edit metadata, and import to your Jellyfin music library.";

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        var ns = typeof(Plugin).Namespace;

        // Download page (main entry point, shows in menu, default tab)
        yield return new PluginPageInfo
        {
            Name = "youtubeaudio_download",
            EmbeddedResourcePath = $"{ns}.Configuration.youtubeaudio_download.html",
            MenuSection = "server",
            DisplayName = "YouTube Audio",
            EnableInMainMenu = true
        };

        yield return new PluginPageInfo
        {
            Name = "youtubeaudio_download.js",
            EmbeddedResourcePath = $"{ns}.Configuration.youtubeaudio_download.js"
        };

        // Import page
        yield return new PluginPageInfo
        {
            Name = "youtubeaudio_import",
            EmbeddedResourcePath = $"{ns}.Configuration.youtubeaudio_import.html"
        };

        yield return new PluginPageInfo
        {
            Name = "youtubeaudio_import.js",
            EmbeddedResourcePath = $"{ns}.Configuration.youtubeaudio_import.js"
        };

        // Settings page
        yield return new PluginPageInfo
        {
            Name = "youtubeaudio_settings",
            EmbeddedResourcePath = $"{ns}.Configuration.youtubeaudio_settings.html"
        };

        yield return new PluginPageInfo
        {
            Name = "youtubeaudio_settings.js",
            EmbeddedResourcePath = $"{ns}.Configuration.youtubeaudio_settings.js"
        };

        // Shared resources
        yield return new PluginPageInfo
        {
            Name = "youtubeaudio_shared.css",
            EmbeddedResourcePath = $"{ns}.Configuration.youtubeaudio_shared.css"
        };

        yield return new PluginPageInfo
        {
            Name = "youtubeaudio_shared.js",
            EmbeddedResourcePath = $"{ns}.Configuration.youtubeaudio_shared.js"
        };
    }
}
