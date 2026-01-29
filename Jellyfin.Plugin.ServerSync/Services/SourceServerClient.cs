using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services;

// CompanionFileInfo
// Information about a companion file (subtitle, etc.) for an item.
public class CompanionFileInfo
{
    public string SourcePath { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string? Language { get; set; }

    public string? Codec { get; set; }

    public bool IsExternal { get; set; }

    public int StreamIndex { get; set; }
}

// ConnectionTestResult
// Result of a connection test containing server info.
public class ConnectionTestResult
{
    public bool Success { get; set; }

    public string? ServerName { get; set; }

    public string? ServerId { get; set; }

    public string? ErrorMessage { get; set; }
}

// SourceServerClient
// Client for communicating with the source Jellyfin server using the official SDK.
// Uses API key authentication only.
public class SourceServerClient : IDisposable
{
    private const string ClientName = "Jellyfin Server Sync";
    private const string ClientVersion = "1.0.0";
    private const string DeviceName = "Server Sync Plugin";
    private static readonly string DeviceId = "serversync-plugin-" + Environment.MachineName.ToLowerInvariant();

    // Default timeout for API operations (connection, authentication, etc.)
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly ILogger<SourceServerClient> _logger;
    private readonly string _serverUrl;
    private readonly string _apiKey;
    private readonly HttpClient _httpClient;
    private readonly JellyfinSdkSettings _sdkSettings;
    private JellyfinApiClient? _apiClient;
    private bool _disposed;

    // SourceServerClient
    // Creates client using API key authentication.
    public SourceServerClient(ILogger<SourceServerClient> logger, string serverUrl, string apiKey)
    {
        _logger = logger;
        _serverUrl = serverUrl.TrimEnd('/');
        _apiKey = apiKey;
        _httpClient = new HttpClient
        {
            Timeout = DefaultTimeout
        };

        _sdkSettings = new JellyfinSdkSettings();
        _sdkSettings.SetServerUrl(_serverUrl);
        _sdkSettings.Initialize(
            clientName: ClientName,
            clientVersion: ClientVersion,
            deviceName: DeviceName,
            deviceId: DeviceId);
        _sdkSettings.SetAccessToken(_apiKey);
    }

    // TestConnectionAsync
    // Tests connection to the source server and returns server info.
    public async Task<ConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var client = GetApiClient();
            var info = await client.System.Info.Public.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            return new ConnectionTestResult
            {
                Success = true,
                ServerName = info?.ServerName,
                ServerId = info?.Id
            };
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogWarning("Connection to source server timed out");
            return new ConnectionTestResult
            {
                Success = false,
                ErrorMessage = "Connection timed out - server may be unreachable or slow to respond"
            };
        }
        catch (OperationCanceledException)
        {
            return new ConnectionTestResult
            {
                Success = false,
                ErrorMessage = "Connection test was cancelled"
            };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error connecting to source server");
            return new ConnectionTestResult
            {
                Success = false,
                ErrorMessage = $"HTTP error: {ex.Message}"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to source server");
            return new ConnectionTestResult
            {
                Success = false,
                ErrorMessage = $"Connection failed: {ex.Message}"
            };
        }
    }

    // GetLibrariesAsync
    // Gets all libraries from the source server.
    public async Task<List<VirtualFolderInfo>> GetLibrariesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var client = GetApiClient();
            var folders = await client.Library.VirtualFolders.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return folders ?? new List<VirtualFolderInfo>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get libraries from source server");
            return new List<VirtualFolderInfo>();
        }
    }

    // GetLibraryItemsAsync
    // Gets items from a library with pagination support.
    public async Task<BaseItemDtoQueryResult?> GetLibraryItemsAsync(
        Guid libraryId,
        int startIndex = 0,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = GetApiClient();
            return await client.Items.GetAsync(
                config =>
                {
                    config.QueryParameters.ParentId = libraryId;
                    config.QueryParameters.Recursive = true;
                    config.QueryParameters.IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Audio, BaseItemKind.Video };
                    config.QueryParameters.Fields = new[] { ItemFields.Path, ItemFields.DateCreated, ItemFields.MediaSources, ItemFields.Etag };
                    config.QueryParameters.StartIndex = startIndex;
                    config.QueryParameters.Limit = limit;
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get items from library {LibraryId}", libraryId);
            return null;
        }
    }

    // GetItemDetailsAsync
    // Gets detailed item info including media sources and streams.
    public async Task<BaseItemDto?> GetItemDetailsAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = GetApiClient();
            var result = await client.Items.GetAsync(
                config =>
                {
                    config.QueryParameters.Ids = new Guid?[] { itemId };
                    config.QueryParameters.Fields = new[]
                    {
                        ItemFields.Path,
                        ItemFields.DateCreated,
                        ItemFields.MediaSources,
                        ItemFields.MediaStreams
                    };
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return result?.Items?.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get item details for {ItemId}", itemId);
            return null;
        }
    }

    // DownloadFileAsync
    // Downloads a file from the source server.
    public async Task<Stream?> DownloadFileAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"{_serverUrl}/Items/{itemId}/Download";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddAuthorizationHeader(request);

            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download item {ItemId}", itemId);
            return null;
        }
    }

    // GetCompanionFilesAsync
    // Gets external companion files (subtitles, etc.) for an item.
    public async Task<List<CompanionFileInfo>> GetCompanionFilesAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        var companions = new List<CompanionFileInfo>();

        try
        {
            var item = await GetItemDetailsAsync(itemId, cancellationToken).ConfigureAwait(false);
            if (item?.MediaSources == null || item.MediaSources.Count == 0)
            {
                return companions;
            }

            var mediaSource = item.MediaSources.FirstOrDefault();
            if (mediaSource?.MediaStreams == null)
            {
                return companions;
            }

            companions.AddRange(
                mediaSource.MediaStreams
                    .Where(stream => stream.IsExternal == true && !string.IsNullOrEmpty(stream.Path))
                    .Select(stream => new CompanionFileInfo
                    {
                        SourcePath = stream.Path!,
                        FileName = Path.GetFileName(stream.Path)!,
                        Language = stream.Language,
                        Codec = stream.Codec,
                        IsExternal = true,
                        StreamIndex = stream.Index ?? 0
                    }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get companion files for {ItemId}", itemId);
        }

        return companions;
    }

    // DownloadCompanionFileAsync
    // Downloads an external subtitle or companion file by its path.
    public async Task<Stream?> DownloadCompanionFileAsync(Guid itemId, string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var encodedPath = Uri.EscapeDataString(filePath);
            var url = $"{_serverUrl}/Videos/{itemId}/Subtitles/Stream?mediaSourceId={itemId}&path={encodedPath}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddAuthorizationHeader(request);

            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var directUrl = $"{_serverUrl}/Items/{itemId}/File?path={encodedPath}";
                using var directRequest = new HttpRequestMessage(HttpMethod.Get, directUrl);
                AddAuthorizationHeader(directRequest);

                response = await _httpClient.SendAsync(directRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download companion file {FilePath} for {ItemId}", filePath, itemId);
            return null;
        }
    }

    // AddAuthorizationHeader
    // Adds the MediaBrowser authorization header to HTTP requests.
    private void AddAuthorizationHeader(HttpRequestMessage request)
    {
        var authValue = $"MediaBrowser Client=\"{ClientName}\", Device=\"{DeviceName}\", DeviceId=\"{DeviceId}\", Version=\"{ClientVersion}\", Token=\"{_apiKey}\"";
        request.Headers.Authorization = new AuthenticationHeaderValue("MediaBrowser", authValue);
    }

    // GetApiClient
    // Returns the API client, creating it lazily if needed.
    private JellyfinApiClient GetApiClient()
    {
        if (_apiClient == null)
        {
            var authProvider = new JellyfinAuthenticationProvider(_sdkSettings);
            var adapter = new JellyfinRequestAdapter(authProvider, _sdkSettings, _httpClient);
            _apiClient = new JellyfinApiClient(adapter);
        }

        return _apiClient;
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
            _httpClient.Dispose();
            _disposed = true;
        }
    }
}
