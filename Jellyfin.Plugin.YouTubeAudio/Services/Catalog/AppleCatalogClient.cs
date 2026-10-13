using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeAudio.Services.Catalog;

/// <summary>
/// Looks songs and artists up in Apple's public music catalog (the iTunes Search API, which
/// needs no account or key). Requests are spaced out to stay inside Apple's rate limit, and
/// answers are cached for the life of the server process. Failures are logged and treated as
/// "no match", so a lookup never stops a download.
/// </summary>
public sealed class AppleCatalogClient : IDisposable
{
    private const string SearchUrl = "https://itunes.apple.com/search";

    // Apple allows roughly 20 searches a minute.
    private static readonly TimeSpan _minInterval = TimeSpan.FromSeconds(3);

    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly ILogger<AppleCatalogClient> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.Ordinal);
    private DateTime _lastRequestUtc = DateTime.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="AppleCatalogClient"/> class.
    /// </summary>
    public AppleCatalogClient(ILogger<AppleCatalogClient> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Finds the catalog song matching a download, or null when there is no confident match.
    /// </summary>
    public async Task<CatalogSong?> FindSongAsync(string? artist, string? title, double? durationSeconds, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var term = (artist + " " + title).Replace("\"", string.Empty, StringComparison.Ordinal);
        var json = await SearchAsync(term, "song", 15, cancellationToken).ConfigureAwait(false);
        return CatalogMatcher.PickBest(artist, title, durationSeconds, CatalogMatcher.ParseSongs(json));
    }

    /// <summary>
    /// Returns whether Apple lists this exact name as an artist, so a credit like
    /// "Katia &amp; Marielle Labèque" is kept as one act instead of being split.
    /// </summary>
    public async Task<bool> IsArtistAsync(string name, CancellationToken cancellationToken)
    {
        var json = await SearchAsync(name, "musicArtist", 5, cancellationToken).ConfigureAwait(false);
        return CatalogMatcher.ListsArtist(name, CatalogMatcher.ParseArtistNames(json));
    }

    /// <summary>
    /// Downloads an album cover at the given size, or returns null when it cannot be fetched.
    /// </summary>
    public async Task<byte[]?> GetArtworkAsync(string? artworkUrl100, int size, CancellationToken cancellationToken)
    {
        var url = CatalogMatcher.GetArtworkUrl(artworkUrl100, size);
        if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        try
        {
            return await _httpClient.GetByteArrayAsync(uri, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Could not download cover art: {Message}", ex.Message);
            return null;
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private async Task<string?> SearchAsync(string term, string entity, int limit, CancellationToken cancellationToken)
    {
        var query = new Dictionary<string, string>
        {
            ["term"] = term,
            ["media"] = "music",
            ["entity"] = entity,
            ["limit"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["country"] = "US"
        };
        using var content = new FormUrlEncodedContent(query);
        var url = SearchUrl + "?" + await content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (_cache.TryGetValue(url, out var cached))
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(url, out cached))
            {
                return cached;
            }

            var wait = _lastRequestUtc + _minInterval - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }

            string? json = null;
            try
            {
                json = await _httpClient.GetStringAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
                _cache[url] = json;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // Not cached: a later lookup may succeed.
                _logger.LogWarning("Apple catalog lookup failed for {Term}: {Message}", DownloadService.SanitizeForLog(term), ex.Message);
            }
            finally
            {
                _lastRequestUtc = DateTime.UtcNow;
            }

            return json;
        }
        finally
        {
            _gate.Release();
        }
    }
}
