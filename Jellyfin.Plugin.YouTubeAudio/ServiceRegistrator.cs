using Jellyfin.Plugin.YouTubeAudio.Services;
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

        // Singleton: download service (owns SemaphoreSlim for download serialization)
        serviceCollection.AddSingleton<DownloadService>();

        // Transient: stateless library query service
        serviceCollection.AddTransient<LibraryService>();
    }
}
