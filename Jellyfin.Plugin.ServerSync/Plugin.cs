using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync;

/// <summary>
/// Plugin
/// Main plugin entry point for Server Sync.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IDisposable
{
    private readonly ILogger<Plugin> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IApplicationPaths _applicationPaths;
    private readonly IServerConfigurationManager _serverConfigurationManager;
    private readonly object _databaseLock = new();
    private SyncDatabase? _database;
    private bool _disposed;

    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        ILogger<Plugin> logger,
        ILoggerFactory loggerFactory,
        IServerConfigurationManager serverConfigurationManager)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _applicationPaths = applicationPaths;
        _serverConfigurationManager = serverConfigurationManager;

        _logger.LogInformation("Server Sync plugin initialized");
    }

    public override string Name => "Server Sync";

    public override Guid Id => Guid.Parse("ebd650b5-6f4c-4ccb-b10d-23dffb3a7286");

    public override string Description => "Sync media from a source Jellyfin server to this server.";

    public static Plugin? Instance { get; private set; }

    public ILoggerFactory LoggerFactory => _loggerFactory;

    public new IApplicationPaths ApplicationPaths => _applicationPaths;

    /// <summary>
    /// Gets the local server's friendly name.
    /// </summary>
    public string LocalServerName => _serverConfigurationManager.Configuration.ServerName ?? Environment.MachineName;

    public SyncDatabase Database
    {
        get
        {
            if (_database != null)
            {
                return _database;
            }

            lock (_databaseLock)
            {
                _database ??= new SyncDatabase(
                    _loggerFactory.CreateLogger<SyncDatabase>(),
                    _applicationPaths.DataPath);
                return _database;
            }
        }
    }

    /// <summary>
    /// GetTempDownloadPath
    /// Returns the configured temp path or falls back to cache directory.
    /// </summary>
    /// <returns>Path to temp download directory.</returns>
    public string GetTempDownloadPath()
    {
        if (!string.IsNullOrWhiteSpace(Configuration.TempDownloadPath))
        {
            return Configuration.TempDownloadPath;
        }

        return Path.Combine(_applicationPaths.CachePath, "serversync");
    }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        var ns = typeof(Plugin).Namespace;

        // Sync page (main entry point, shows in menu, default tab)
        yield return new PluginPageInfo
        {
            Name = "serversync_sync",
            EmbeddedResourcePath = $"{ns}.Configuration.serversync_sync.html",
            MenuSection = "plugin",
            DisplayName = "Server Sync"
        };

        yield return new PluginPageInfo
        {
            Name = "serversync_sync.js",
            EmbeddedResourcePath = $"{ns}.Configuration.serversync_sync.js"
        };

        // Settings page
        yield return new PluginPageInfo
        {
            Name = "serversync_settings",
            EmbeddedResourcePath = $"{ns}.Configuration.serversync_settings.html"
        };

        yield return new PluginPageInfo
        {
            Name = "serversync_settings.js",
            EmbeddedResourcePath = $"{ns}.Configuration.serversync_settings.js"
        };

        // Shared resources (CSS and JS used by sync pages)
        yield return new PluginPageInfo
        {
            Name = "serversync_shared.css",
            EmbeddedResourcePath = $"{ns}.Configuration.serversync_shared.css"
        };

        yield return new PluginPageInfo
        {
            Name = "serversync_shared.js",
            EmbeddedResourcePath = $"{ns}.Configuration.serversync_shared.js"
        };
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _database?.Dispose();
            _disposed = true;
        }
    }
}
