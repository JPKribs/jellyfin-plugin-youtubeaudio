using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.ServerSync.Configuration;
using Jellyfin.Plugin.ServerSync.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync;

// Plugin
// Main plugin entry point for Server Sync.
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IDisposable
{
    private readonly ILogger<Plugin> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IApplicationPaths _applicationPaths;
    private readonly object _databaseLock = new();
    private SyncDatabase? _database;
    private bool _disposed;

    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        ILogger<Plugin> logger,
        ILoggerFactory loggerFactory)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _applicationPaths = applicationPaths;

        _logger.LogInformation("Server Sync plugin initialized");
    }

    public override string Name => "Server Sync";

    public override Guid Id => Guid.Parse("ebd650b5-6f4c-4ccb-b10d-23dffb3a7286");

    public override string Description => "Sync media from a source Jellyfin server to this server.";

    public static Plugin? Instance { get; private set; }

    public ILoggerFactory LoggerFactory => _loggerFactory;

    public new IApplicationPaths ApplicationPaths => _applicationPaths;

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

    // GetTempDownloadPath
    // Returns the configured temp path or falls back to cache directory.
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
        yield return new PluginPageInfo
        {
            Name = "ServerSync",
            EmbeddedResourcePath = $"{typeof(Plugin).Namespace}.Configuration.configPage.html",
            MenuSection = "plugin",
            DisplayName = "Server Sync"
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
