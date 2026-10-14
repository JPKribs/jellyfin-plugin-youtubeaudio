using Jellyfin.Plugin.YouTubeAudio.Services;
using Jellyfin.Plugin.YouTubeAudio.Services.Catalog;
using Jellyfin.Plugin.YouTubeAudio.Services.Playlists;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.YouTubeAudio;

/// <summary>
/// Registers plugin services with the Jellyfin DI container.
/// </summary>
public class ServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Singleton: database provider (lazy-creates SQLite DB)
        serviceCollection.AddSingleton<QueueDatabaseProvider>();

        // Singleton: Apple catalog lookups (rate-limited, cached for the server's lifetime)
        serviceCollection.AddSingleton<AppleCatalogClient>();

        // Singleton: download service (owns SemaphoreSlim for download serialization)
        serviceCollection.AddSingleton<DownloadService>();

        // Playlist export formats. Add a source here to support another service's exports.
        serviceCollection.AddSingleton<IPlaylistSource, AppleMusicPlaylistSource>();
        serviceCollection.AddSingleton<IPlaylistSource, AppleMusicLibrarySource>();
        serviceCollection.AddSingleton<IPlaylistSource, SpotifyPlaylistSource>();
        serviceCollection.AddSingleton<PlaylistImportService>();

        // Singleton: stateless library queries, shared with the playlist sync service
        serviceCollection.AddSingleton<LibraryService>();

        // Fills imported playlists as their songs reach the library (listens to library scans)
        serviceCollection.AddSingleton<PlaylistSyncService>();
        serviceCollection.AddHostedService(provider => provider.GetRequiredService<PlaylistSyncService>());
    }
}
