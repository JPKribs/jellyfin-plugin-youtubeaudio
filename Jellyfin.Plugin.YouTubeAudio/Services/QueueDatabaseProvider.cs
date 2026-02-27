using System;
using System.IO;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeAudio.Services;

/// <summary>
/// Provides lazy-initialized access to the shared <see cref="QueueDatabase"/> singleton.
/// </summary>
public sealed class QueueDatabaseProvider : IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly string _dataPath;
    private readonly object _lock = new();
    private volatile QueueDatabase? _database;
    private volatile bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueueDatabaseProvider"/> class.
    /// </summary>
    public QueueDatabaseProvider(ILoggerFactory loggerFactory, IApplicationPaths applicationPaths)
    {
        _loggerFactory = loggerFactory;
        _dataPath = applicationPaths.DataPath;
    }

    /// <summary>
    /// Gets the QueueDatabase instance, creating it lazily if needed.
    /// </summary>
    public QueueDatabase Database
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(QueueDatabaseProvider));

            if (_database != null)
            {
                return _database;
            }

            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, nameof(QueueDatabaseProvider));

                _database ??= new QueueDatabase(
                    _loggerFactory.CreateLogger<QueueDatabase>(),
                    _dataPath);
                return _database;
            }
        }
    }

    /// <summary>
    /// Gets the resolved cache directory path.
    /// If CacheDirectoryOverride is set in config, uses that; otherwise {DataPath}/youtubeaudio.
    /// </summary>
    public string GetCacheDirectory()
    {
        var config = Plugin.Instance?.Configuration;
        if (config != null && !string.IsNullOrWhiteSpace(config.CacheDirectoryOverride))
        {
            return config.CacheDirectoryOverride;
        }

        return Path.Combine(_dataPath, "youtubeaudio");
    }

    /// <summary>
    /// Gets the path to the audio file cache subdirectory.
    /// </summary>
    public string GetAudioCacheDirectory()
    {
        var cacheDir = Path.Combine(GetCacheDirectory(), "cache");
        Directory.CreateDirectory(cacheDir);
        return cacheDir;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            if (!_disposed)
            {
                _database?.Dispose();
                _disposed = true;
            }
        }
    }
}
