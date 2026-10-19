using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Jellyfin.Plugin.YouTubeAudio.Configuration;
using Jellyfin.Plugin.YouTubeAudio.Models;
using Jellyfin.Plugin.YouTubeAudio.Services;
using Jellyfin.Plugin.YouTubeAudio.Services.Catalog;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.YouTubeAudio.Tests;

/// <summary>Serializes tests that set the static <see cref="Plugin.Instance"/> singleton.</summary>
[CollectionDefinition("Plugin", DisableParallelization = true)]
public class PluginCollection
{
}

/// <summary>
/// A <see cref="DownloadService"/> over a temporary data folder, queue database and music
/// library, with the network-backed lookups (Apple catalog, cover art) switched off.
/// </summary>
internal sealed class ServiceFixture : IDisposable
{
    public ServiceFixture(Action<PluginConfiguration>? configure = null)
    {
        Root = Path.Combine(Path.GetTempPath(), $"yta-svc-{Guid.NewGuid():N}");
        Library = Path.Combine(Root, "music");
        Directory.CreateDirectory(Library);

        var paths = Substitute.For<IApplicationPaths>();
        paths.DataPath.Returns(Path.Combine(Root, "data"));
        paths.PluginConfigurationsPath.Returns(Path.Combine(Root, "plugins"));
        Plugin = new Plugin(paths, Substitute.For<IXmlSerializer>(), NullLogger<Plugin>.Instance);
        var config = new PluginConfiguration
        {
            MusicLibraryPath = Library,
            LookUpCatalogMetadata = false,
            EmbedCoverArt = false
        };
        configure?.Invoke(config);
        Plugin.UpdateConfiguration(config);

        Provider = new QueueDatabaseProvider(NullLoggerFactory.Instance, paths);
        var libraryManager = Substitute.For<ILibraryManager>();
        libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem>());
        var library = new LibraryService(libraryManager, Substitute.For<ILibraryMonitor>(), NullLogger<LibraryService>.Instance);
        Encoder = Substitute.For<IMediaEncoder>();
        Encoder.EncoderPath.Returns(string.Empty);
        Catalog = new AppleCatalogClient(NullLogger<AppleCatalogClient>.Instance);
        Service = new DownloadService(NullLogger<DownloadService>.Instance, Provider, Encoder, Catalog, library);
    }

    public string Root { get; }

    public string Library { get; }

    public Plugin Plugin { get; }

    public QueueDatabaseProvider Provider { get; }

    public IMediaEncoder Encoder { get; }

    public AppleCatalogClient Catalog { get; }

    public DownloadService Service { get; }

    public QueueDatabase Db => Provider.Database;

    public string CacheDir => Provider.GetAudioCacheDirectory();

    /// <summary>
    /// Puts a tagged Opus file in the cache as a downloaded queue item and returns the item.
    /// </summary>
    public QueueItem AddDownloaded(string title, string artist, string album, uint track, uint year = 2019, PlaylistTrack? sourceTrack = null)
    {
        var item = new QueueItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Url = "https://www.youtube.com/watch?v=abc",
            Title = title,
            Artist = artist,
            FileName = Guid.NewGuid().ToString("N") + ".opus",
            Status = QueueStatus.Downloaded,
            CreatedAt = "2026-10-19T00:00:00Z",
            UpdatedAt = "2026-10-19T00:00:00Z",
            SourceTrack = sourceTrack
        };
        WriteSong(Path.Combine(CacheDir, item.FileName), title, artist, album, track, year);
        Db.AddItem(item);
        return item;
    }

    /// <summary>Writes a tagged copy of the test Opus file.</summary>
    public static void WriteSong(string path, string title, string artist, string album, uint track, uint year = 2019)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "TestData", "silence.opus"), path, overwrite: true);
        using var file = TagLib.File.Create(path);
        file.Tag.Title = title;
        file.Tag.Performers = [artist];
        file.Tag.AlbumArtists = [artist];
        file.Tag.Album = album;
        file.Tag.Track = track;
        file.Tag.Year = year;
        file.Save();
    }

    public static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    public static (string Title, uint Track) ReadTitleAndTrack(string path)
    {
        using var file = TagLib.File.Create(path);
        return (file.Tag.Title, file.Tag.Track);
    }

    public List<string> LibraryFiles()
        => Directory.EnumerateFiles(Library, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Library, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

    public void Dispose()
    {
        Service.Dispose();
        Provider.Dispose();
        Catalog.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A killed test process may still be releasing a file.
        }
    }
}
