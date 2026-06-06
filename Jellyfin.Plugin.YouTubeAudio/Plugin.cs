using System;
using System.Collections.Generic;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeAudio;

/// <summary>
/// Main plugin entry point for YouTube Audio.
/// </summary>
public class Plugin : PluginBase<Plugin, PluginConfiguration>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="xmlSerializer">The XML serializer.</param>
    /// <param name="logger">The logger.</param>
    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        ILogger<Plugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        ArgumentNullException.ThrowIfNull(logger);
        logger.LogInformation("YouTube Audio plugin initialized");
    }

    /// <inheritdoc />
    public override string Name => "YouTube Audio";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("7323ea64-a200-4265-ab8f-e7ae27d06c38");

    /// <inheritdoc />
    public override string Description => "Download YouTube audio, edit metadata, and import to your Jellyfin music library.";

    /// <inheritdoc />
    public override IEnumerable<PluginPageInfo> GetPages()
    {
        var ns = typeof(Plugin).Namespace;

        // Tab 1: Download (the dashboard menu entry).
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

        // Tab 2: Import.
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

        // Tab 3: Settings.
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

        // Plugin specific shared JS layered on top of the base kit (all shared CSS now lives in the base package).
        yield return new PluginPageInfo
        {
            Name = "youtubeaudio_shared.js",
            EmbeddedResourcePath = $"{ns}.Configuration.youtubeaudio_shared.js"
        };

        // Shared base CSS and JS compiled in from the JPKribs.Jellyfin.Base package.
        foreach (var page in GetSharedPages("youtubeaudio"))
        {
            yield return page;
        }
    }
}
