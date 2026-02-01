using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ServerSync.Models.Configuration;
using Jellyfin.Plugin.ServerSync.Models.ContentSync;
using Jellyfin.Plugin.ServerSync.Utilities;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Services;

/// <summary>
/// SourceServerClient
/// Client for communicating with the source Jellyfin server using the official SDK.
/// </summary>
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

    /// <summary>
    /// Initializes a new instance of the <see cref="SourceServerClient"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="serverUrl">Source server URL.</param>
    /// <param name="apiKey">API key for authentication.</param>
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

    /// <summary>
    /// TestConnectionAsync
    /// Tests connection to the source server and returns server info.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Connection test result with server info.</returns>
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

    /// <summary>
    /// GetLibrariesAsync
    /// Gets all libraries from the source server.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of virtual folder info.</returns>
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

    /// <summary>
    /// GetUsersAsync
    /// Gets all users from the source server.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of user DTOs.</returns>
    public async Task<List<UserDto>> GetUsersAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var client = GetApiClient();
            var users = await client.Users.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return users ?? new List<UserDto>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get users from source server");
            return new List<UserDto>();
        }
    }

    /// <summary>
    /// GetLibraryItemsAsync
    /// Gets items from a library with pagination support.
    /// </summary>
    /// <param name="libraryId">Library ID.</param>
    /// <param name="startIndex">Starting index for pagination.</param>
    /// <param name="limit">Maximum items to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Query result with items.</returns>
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

    /// <summary>
    /// GetLibraryItemsWithMetadataAsync
    /// Gets items from a library with extended metadata fields for metadata sync.
    /// </summary>
    /// <param name="libraryId">Library ID.</param>
    /// <param name="startIndex">Starting index for pagination.</param>
    /// <param name="limit">Maximum items to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Query result with items including metadata.</returns>
    public async Task<BaseItemDtoQueryResult?> GetLibraryItemsWithMetadataAsync(
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
                    config.QueryParameters.Fields = new[]
                    {
                        ItemFields.Path,
                        ItemFields.DateCreated,
                        ItemFields.Overview,
                        ItemFields.Genres,
                        ItemFields.Tags,
                        ItemFields.Studios,
                        ItemFields.People,
                        ItemFields.ProviderIds,
                        ItemFields.OriginalTitle,
                        ItemFields.SortName,
                        ItemFields.ProductionLocations
                    };
                    config.QueryParameters.StartIndex = startIndex;
                    config.QueryParameters.Limit = limit;
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get items with metadata from library {LibraryId}", libraryId);
            return null;
        }
    }

    /// <summary>
    /// GetLibraryItemCountAsync
    /// Gets the total count of items in a library without fetching item details.
    /// </summary>
    /// <param name="libraryId">Library ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Total item count or 0 if failed.</returns>
    public async Task<int> GetLibraryItemCountAsync(Guid libraryId, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = GetApiClient();
            var result = await client.Items.GetAsync(
                config =>
                {
                    config.QueryParameters.ParentId = libraryId;
                    config.QueryParameters.Recursive = true;
                    config.QueryParameters.IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Audio, BaseItemKind.Video };
                    config.QueryParameters.StartIndex = 0;
                    config.QueryParameters.Limit = 0;
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return result?.TotalRecordCount ?? 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get item count for library {LibraryId}", libraryId);
            return 0;
        }
    }

    /// <summary>
    /// GetItemDetailsAsync
    /// Gets detailed item info including media sources and streams.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Item details or null if not found.</returns>
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

    /// <summary>
    /// DownloadFileAsync
    /// Downloads a file from the source server.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Stream of file content or null on failure.</returns>
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

    /// <summary>
    /// GetCompanionFilesAsync
    /// Gets external companion files (subtitles, etc.) for an item.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of companion file info.</returns>
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

    /// <summary>
    /// DownloadCompanionFileAsync
    /// Downloads an external subtitle or companion file by its path.
    /// </summary>
    /// <param name="itemId">Item ID.</param>
    /// <param name="filePath">Source file path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Stream of file content or null on failure.</returns>
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

    // ===== User Sync Methods =====

    /// <summary>
    /// Gets a user's full details including Policy and Configuration.
    /// </summary>
    /// <param name="userId">User ID on the source server.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>User details or null if not found.</returns>
    public async Task<UserDto?> GetUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = GetApiClient();
            return await client.Users[userId].GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get user details for {UserId}", userId);
            return null;
        }
    }

    /// <summary>
    /// Gets a user's profile image as a stream.
    /// </summary>
    /// <param name="userId">User ID on the source server.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Image stream or null if not found.</returns>
    public async Task<Stream?> GetUserImageAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"{_serverUrl}/Users/{userId}/Images/Primary";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddAuthorizationHeader(request);

            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // User has no profile image
                    return null;
                }

                response.EnsureSuccessStatusCode();
            }

            return await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get profile image for user {UserId}", userId);
            return null;
        }
    }

    /// <summary>
    /// Gets the content length of a user's profile image without downloading.
    /// </summary>
    /// <param name="userId">User ID on the source server.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Image size in bytes, or null if no image.</returns>
    public async Task<long?> GetUserImageSizeAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"{_serverUrl}/Users/{userId}/Images/Primary";
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            AddAuthorizationHeader(request);

            var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return response.Content.Headers.ContentLength;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get profile image size for user {UserId}", userId);
            return null;
        }
    }

    /// <summary>
    /// Gets the SHA256 hash of a user's profile image.
    /// Downloads the image to compute hash, then disposes of the data.
    /// </summary>
    /// <param name="userId">User ID on the source server.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>SHA256 hash (truncated) as hex string, or null if no image.</returns>
    public async Task<string?> GetUserImageHashAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var imageStream = await GetUserImageAsync(userId, cancellationToken).ConfigureAwait(false);
            if (imageStream == null)
            {
                return null;
            }

            return HashUtilities.ComputeSha256Hash(imageStream);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get profile image hash for user {UserId}", userId);
            return null;
        }
    }

    // ===== History Sync Methods =====

    /// <summary>
    /// Gets items with user playback data for a specific user in a library.
    /// Uses the Items endpoint with UserId parameter to get user-specific data.
    /// </summary>
    /// <param name="userId">User ID on the source server.</param>
    /// <param name="libraryId">Library ID.</param>
    /// <param name="startIndex">Starting index for pagination.</param>
    /// <param name="limit">Maximum items to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Query result with items including user data.</returns>
    public async Task<BaseItemDtoQueryResult?> GetUserLibraryItemsAsync(
        Guid userId,
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
                    config.QueryParameters.UserId = userId;
                    config.QueryParameters.ParentId = libraryId;
                    config.QueryParameters.Recursive = true;
                    config.QueryParameters.IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Audio, BaseItemKind.Video };
                    config.QueryParameters.Fields = new[]
                    {
                        ItemFields.Path,
                        ItemFields.DateCreated,
                        ItemFields.MediaSources
                    };
                    config.QueryParameters.EnableUserData = true;
                    config.QueryParameters.StartIndex = startIndex;
                    config.QueryParameters.Limit = limit;
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get user items for user {UserId} in library {LibraryId}", userId, libraryId);
            return null;
        }
    }

    /// <summary>
    /// Gets items that have been played by a specific user in a library.
    /// </summary>
    /// <param name="userId">User ID on the source server.</param>
    /// <param name="libraryId">Library ID.</param>
    /// <param name="startIndex">Starting index for pagination.</param>
    /// <param name="limit">Maximum items to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Query result with played items.</returns>
    public async Task<BaseItemDtoQueryResult?> GetUserPlayedItemsAsync(
        Guid userId,
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
                    config.QueryParameters.UserId = userId;
                    config.QueryParameters.ParentId = libraryId;
                    config.QueryParameters.Recursive = true;
                    config.QueryParameters.IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Audio, BaseItemKind.Video };
                    config.QueryParameters.Fields = new[]
                    {
                        ItemFields.Path,
                        ItemFields.DateCreated,
                        ItemFields.MediaSources
                    };
                    config.QueryParameters.EnableUserData = true;
                    config.QueryParameters.IsPlayed = true;
                    config.QueryParameters.StartIndex = startIndex;
                    config.QueryParameters.Limit = limit;
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get played items for user {UserId} in library {LibraryId}", userId, libraryId);
            return null;
        }
    }

    /// <summary>
    /// Gets the user's favorite items in a library.
    /// </summary>
    /// <param name="userId">User ID on the source server.</param>
    /// <param name="libraryId">Library ID.</param>
    /// <param name="startIndex">Starting index for pagination.</param>
    /// <param name="limit">Maximum items to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Query result with favorite items.</returns>
    public async Task<BaseItemDtoQueryResult?> GetUserFavoritesAsync(
        Guid userId,
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
                    config.QueryParameters.UserId = userId;
                    config.QueryParameters.ParentId = libraryId;
                    config.QueryParameters.Recursive = true;
                    config.QueryParameters.IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Audio, BaseItemKind.Video };
                    config.QueryParameters.Fields = new[]
                    {
                        ItemFields.Path,
                        ItemFields.DateCreated,
                        ItemFields.MediaSources
                    };
                    config.QueryParameters.EnableUserData = true;
                    config.QueryParameters.IsFavorite = true;
                    config.QueryParameters.StartIndex = startIndex;
                    config.QueryParameters.Limit = limit;
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get favorites for user {UserId} in library {LibraryId}", userId, libraryId);
            return null;
        }
    }

    /// <summary>
    /// Gets count of items with history data for a user in a library.
    /// </summary>
    /// <param name="userId">User ID on the source server.</param>
    /// <param name="libraryId">Library ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Total count of items with history.</returns>
    public async Task<int> GetUserLibraryItemCountAsync(
        Guid userId,
        Guid libraryId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = GetApiClient();
            var result = await client.Items.GetAsync(
                config =>
                {
                    config.QueryParameters.UserId = userId;
                    config.QueryParameters.ParentId = libraryId;
                    config.QueryParameters.Recursive = true;
                    config.QueryParameters.IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Audio, BaseItemKind.Video };
                    config.QueryParameters.StartIndex = 0;
                    config.QueryParameters.Limit = 0;
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return result?.TotalRecordCount ?? 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get item count for user {UserId} in library {LibraryId}", userId, libraryId);
            return 0;
        }
    }

    /// <summary>
    /// AddAuthorizationHeader
    /// Adds the MediaBrowser authorization header to HTTP requests.
    /// </summary>
    /// <param name="request">HTTP request message.</param>
    private void AddAuthorizationHeader(HttpRequestMessage request)
    {
        var authValue = $"MediaBrowser Client=\"{ClientName}\", Device=\"{DeviceName}\", DeviceId=\"{DeviceId}\", Version=\"{ClientVersion}\", Token=\"{_apiKey}\"";
        request.Headers.Authorization = new AuthenticationHeaderValue("MediaBrowser", authValue);
    }

    /// <summary>
    /// GetApiClient
    /// Returns the API client, creating it lazily if needed.
    /// </summary>
    /// <returns>Jellyfin API client instance.</returns>
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
