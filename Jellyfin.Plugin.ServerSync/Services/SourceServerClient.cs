using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Configuration;
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

    public string? AccessToken { get; set; }

    public string? ErrorMessage { get; set; }
}

// SourceServerClient
// Client for communicating with the source Jellyfin server using the official SDK.
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
    private readonly AuthenticationMethod _authMethod;
    private readonly string? _apiKey;
    private readonly string? _username;
    private readonly string? _password;
    private string? _accessToken;
    private readonly HttpClient _httpClient;
    private readonly JellyfinSdkSettings _sdkSettings;
    private JellyfinApiClient? _apiClient;
    private bool _disposed;

    // SourceServerClient (API Key)
    // Creates client using API key authentication (recommended).
    public SourceServerClient(ILogger<SourceServerClient> logger, string serverUrl, string apiKey)
    {
        _logger = logger;
        _serverUrl = serverUrl.TrimEnd('/');
        _authMethod = AuthenticationMethod.ApiKey;
        _apiKey = apiKey;
        _accessToken = apiKey;
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

    // SourceServerClient (User Credentials)
    // Creates client using username/password authentication.
    public SourceServerClient(
        ILogger<SourceServerClient> logger,
        string serverUrl,
        string username,
        string password,
        string? cachedAccessToken = null)
    {
        _logger = logger;
        _serverUrl = serverUrl.TrimEnd('/');
        _authMethod = AuthenticationMethod.UserCredentials;
        _username = username;
        _password = password;
        _accessToken = cachedAccessToken;
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

        if (!string.IsNullOrEmpty(cachedAccessToken))
        {
            _sdkSettings.SetAccessToken(cachedAccessToken);
        }
    }

    // AuthenticateAsync
    // Authenticates with the server using username/password and returns the access token.
    public async Task<ConnectionTestResult> AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        if (_authMethod == AuthenticationMethod.ApiKey)
        {
            return new ConnectionTestResult
            {
                Success = true,
                AccessToken = _apiKey,
                ErrorMessage = "API key authentication does not require explicit authentication"
            };
        }

        try
        {
            var client = GetApiClient();
            var authResult = await client.Users.AuthenticateByName.PostAsync(
                new AuthenticateUserByName
                {
                    Username = _username,
                    Pw = _password
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (authResult?.AccessToken == null)
            {
                return new ConnectionTestResult
                {
                    Success = false,
                    ErrorMessage = "Authentication failed: No access token received"
                };
            }

            _accessToken = authResult.AccessToken;
            _sdkSettings.SetAccessToken(_accessToken);

            _logger.LogInformation("Successfully authenticated with source server as user {Username}", _username);

            return new ConnectionTestResult
            {
                Success = true,
                ServerName = authResult.ServerId,
                AccessToken = _accessToken
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to authenticate with source server");
            return new ConnectionTestResult
            {
                Success = false,
                ErrorMessage = $"Authentication failed: {ex.Message}"
            };
        }
    }

    // AccessToken
    // Gets the current access token (API key or authenticated token).
    public string? AccessToken => _accessToken;

    // TestConnectionAsync
    // Tests connection to the source server and returns server info.
    public async Task<ConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        return await TestConnectionInternalAsync(0, cancellationToken).ConfigureAwait(false);
    }

    // TestConnectionInternalAsync
    // Internal implementation with retry depth tracking to prevent infinite recursion.
    private async Task<ConnectionTestResult> TestConnectionInternalAsync(int retryDepth, CancellationToken cancellationToken)
    {
        const int maxRetryDepth = 1; // Only allow one re-authentication attempt

        try
        {
            // If using user credentials and no access token, authenticate first
            if (_authMethod == AuthenticationMethod.UserCredentials && string.IsNullOrEmpty(_accessToken))
            {
                var authResult = await AuthenticateAsync(cancellationToken).ConfigureAwait(false);
                if (!authResult.Success)
                {
                    return authResult;
                }
            }

            var client = GetApiClient();
            var info = await client.System.Info.Public.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            return new ConnectionTestResult
            {
                Success = true,
                ServerName = info?.ServerName,
                ServerId = info?.Id,
                AccessToken = _accessToken
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

            // If token might be expired and using user credentials, try re-authenticating (only once)
            if (_authMethod == AuthenticationMethod.UserCredentials && !string.IsNullOrEmpty(_accessToken) && retryDepth < maxRetryDepth)
            {
                _logger.LogInformation("Attempting to re-authenticate with source server (attempt {Attempt})", retryDepth + 1);
                _accessToken = null;
                _sdkSettings.SetAccessToken(string.Empty);
                return await TestConnectionInternalAsync(retryDepth + 1, cancellationToken).ConfigureAwait(false);
            }

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
            await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false);
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
            await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false);
            var client = GetApiClient();
            return await client.Items.GetAsync(
                config =>
                {
                    config.QueryParameters.ParentId = libraryId;
                    config.QueryParameters.Recursive = true;
                    config.QueryParameters.IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Audio, BaseItemKind.Video };
                    config.QueryParameters.Fields = new[] { ItemFields.Path, ItemFields.DateCreated, ItemFields.MediaSources };
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
            await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false);
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
            await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false);

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
            await EnsureAuthenticatedAsync(cancellationToken).ConfigureAwait(false);

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

    // EnsureAuthenticatedAsync
    // Ensures the client is authenticated before making API calls.
    private async Task EnsureAuthenticatedAsync(CancellationToken cancellationToken)
    {
        if (_authMethod == AuthenticationMethod.UserCredentials && string.IsNullOrEmpty(_accessToken))
        {
            var result = await AuthenticateAsync(cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                throw new InvalidOperationException($"Authentication failed: {result.ErrorMessage}");
            }
        }
    }

    // AddAuthorizationHeader
    // Adds the MediaBrowser authorization header to HTTP requests.
    private void AddAuthorizationHeader(HttpRequestMessage request)
    {
        var authValue = $"MediaBrowser Client=\"{ClientName}\", Device=\"{DeviceName}\", DeviceId=\"{DeviceId}\", Version=\"{ClientVersion}\", Token=\"{_accessToken}\"";
        request.Headers.Authorization = new AuthenticationHeaderValue("MediaBrowser", authValue);
    }

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
