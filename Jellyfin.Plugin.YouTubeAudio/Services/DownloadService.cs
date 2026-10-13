using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.YouTubeAudio.Configuration;
using Jellyfin.Plugin.YouTubeAudio.Models;
using Jellyfin.Plugin.YouTubeAudio.Services.Catalog;
using Jellyfin.Plugin.YouTubeAudio.Services.Playlists;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;
using YoutubeDLSharp;
using YoutubeDLSharp.Options;

namespace Jellyfin.Plugin.YouTubeAudio.Services;

/// <summary>
/// Handles downloading audio from YouTube, managing tags, and importing to the library.
/// Registered as singleton because it owns the download lock semaphore.
/// </summary>
public sealed class DownloadService : IDisposable
{
    private static readonly HttpClient _httpClient = new();

    // Reads GitHub's "latest release" redirect to learn the newest yt-dlp version without downloading it.
    private static readonly HttpClient _noRedirectClient = new(new HttpClientHandler { AllowAutoRedirect = false, CheckCertificateRevocationList = true });

    private static readonly string[] _commonDenoPaths = ["/opt/homebrew/bin/deno", "/usr/local/bin/deno", "/usr/bin/deno"];

    // How often the plugin-managed yt-dlp is checked for a newer release. YouTube changes often and
    // an outdated yt-dlp gets HTTP 403 for every download.
    private static readonly TimeSpan _ytDlpUpdateInterval = TimeSpan.FromHours(12);

    // Waits before retrying a download or search that failed for a transient reason.
    private static readonly TimeSpan[] _retryDelays = [TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10)];

    // yt-dlp error text that marks a failure worth retrying (throttling, server or network hiccups).
    private static readonly string[] _transientErrorMarkers =
    [
        "HTTP Error 403", "HTTP Error 429", "HTTP Error 500", "HTTP Error 502", "HTTP Error 503", "HTTP Error 504",
        "timed out", "Connection reset", "Temporary failure", "Unable to download webpage", "unable to download video data"
    ];
    private static readonly string[] _selfUpdateArgs = ["-U"];
    private static readonly string[] _versionArgs = ["--version"];
    private static readonly char[] _newlineChars = ['\r', '\n'];

    /// <summary>Longest file or folder name written to the library, in characters.</summary>
    internal const int MaxPathComponentLength = 100;

    /// <summary>Embedded cover art size, in pixels.</summary>
    private const int CoverArtSize = 1200;

    private readonly ILogger<DownloadService> _logger;
    private readonly QueueDatabaseProvider _dbProvider;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly AppleCatalogClient _catalog;
    private readonly LibraryService _libraryService;
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "Disposed in Dispose() method")]
    private readonly SemaphoreSlim _downloadLock = new(1, 1);
    private DateTime _lastYtDlpUpdateCheckUtc = DateTime.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="DownloadService"/> class.
    /// </summary>
    public DownloadService(
        ILogger<DownloadService> logger,
        QueueDatabaseProvider dbProvider,
        IMediaEncoder mediaEncoder,
        AppleCatalogClient catalog,
        LibraryService libraryService)
    {
        _logger = logger;
        _dbProvider = dbProvider;
        _mediaEncoder = mediaEncoder;
        _catalog = catalog;
        _libraryService = libraryService;
    }

    private static PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? throw new InvalidOperationException("Plugin is not initialized.");

    // ===== Queue Operations =====

    /// <summary>
    /// Queues a YouTube URL for download. If playlist, queues each video individually.
    /// </summary>
    public Task<List<QueueItemDto>> QueueUrlAsync(string url, CancellationToken cancellationToken = default)
        => QueueUrlAsync(url, null, null, null, null, cancellationToken);

    /// <summary>
    /// Queues a YouTube URL for download, applying submitted metadata.
    /// Artist, album, and year are applied to every resulting track. The title is applied only when the URL resolves to a single track.
    /// </summary>
    /// <param name="url">The YouTube URL.</param>
    /// <param name="artist">The artist to tag, or null.</param>
    /// <param name="album">The album to tag, or null.</param>
    /// <param name="year">The release year to tag, or null.</param>
    /// <param name="title">The song title for a single track, or null.</param>
    /// <param name="cancellationToken">Cancels the playlist metadata fetch (tied to the HTTP request).</param>
    /// <returns>The created queue items.</returns>
    public async Task<List<QueueItemDto>> QueueUrlAsync(string url, string? artist, string? album, int? year, string? title, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("URL is required.");
        }

        if (!IsValidYouTubeUrl(url))
        {
            throw new ArgumentException("URL must be a valid YouTube URL.");
        }

        var ext = Config.AudioFormat.ToFileExtension();
        var db = _dbProvider.Database;
        var createdItems = new List<QueueItem>();
        var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

        // Check if this is a playlist URL
        if (url.Contains("list=", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await EnsureYtDlpAsync().ConfigureAwait(false);
                var ytdl = CreateYoutubeDL();
                var fetchResult = await ytdl.RunVideoDataFetch(url, cancellationToken, overrideOptions: await CreateToolOptionsAsync().ConfigureAwait(false)).ConfigureAwait(false);

                if (fetchResult.Success && fetchResult.Data?.Entries != null)
                {
                    foreach (var entry in fetchResult.Data.Entries)
                    {
                        var videoUrl = entry.Url ?? entry.WebpageUrl ?? $"https://www.youtube.com/watch?v={entry.ID}";

                        // Derived titles are cleaned at write time; user-supplied titles never are.
                        var entryTitle = string.IsNullOrWhiteSpace(entry.Title)
                            ? null
                            : CleanYouTubeTitle(entry.Title, artist);
                        var item = new QueueItem
                        {
                            Id = Guid.NewGuid().ToString("N"),
                            Url = videoUrl,
                            Title = entryTitle,
                            FileName = Guid.NewGuid().ToString("N") + ext,
                            Status = QueueStatus.Queued,
                            CreatedAt = now,
                            UpdatedAt = now,
                            Artist = artist,
                            Album = album,
                            Year = year
                        };
                        db.AddItem(item);
                        createdItems.Add(item);
                    }

                    _logger.LogInformation("Queued {Count} items from playlist {Url}", createdItems.Count, SanitizeForLog(url));
                }
                else
                {
                    // Fallback: treat as single video
                    var item = CreateSingleQueueItem(url, ext, now, artist, album, year, title);
                    db.AddItem(item);
                    createdItems.Add(item);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch playlist metadata, queuing as single URL");
                var item = CreateSingleQueueItem(url, ext, now, artist, album, year, title);
                db.AddItem(item);
                createdItems.Add(item);
            }
        }
        else
        {
            var item = CreateSingleQueueItem(url, ext, now, artist, album, year, title);
            db.AddItem(item);
            createdItems.Add(item);
        }

        return createdItems.Select(item => item.ToDto()).ToList();
    }

    /// <summary>
    /// Queues tracks read from a playlist export. Each item starts without a URL; its YouTube
    /// match is searched for when it is downloaded, so a large playlist queues instantly. The
    /// source's title, artist, album and year are authoritative and never cleaned.
    /// </summary>
    /// <param name="source">The playlist source ID.</param>
    /// <param name="tracks">The parsed tracks.</param>
    /// <returns>The created queue items.</returns>
    public List<QueueItemDto> QueuePlaylistTracks(string source, IReadOnlyList<PlaylistTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        var ext = Config.AudioFormat.ToFileExtension();
        var db = _dbProvider.Database;
        var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        var created = new List<QueueItemDto>(tracks.Count);

        foreach (var track in tracks)
        {
            var item = new QueueItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Url = string.Empty,
                Title = track.Title,
                UserTitled = true,
                FileName = Guid.NewGuid().ToString("N") + ext,
                Status = QueueStatus.Queued,
                CreatedAt = now,
                UpdatedAt = now,
                Artist = track.Artist,
                Album = track.Album,
                Year = track.Year,
                Source = source,
                SourceTrack = track
            };
            db.AddItem(item);
            created.Add(item.ToDto());
        }

        return created;
    }

    /// <summary>
    /// Processes queued items by downloading them.
    /// When ids is provided, only those items are processed, and selected items that previously
    /// failed are retried. Otherwise all queued items are processed.
    /// </summary>
    public async Task ProcessQueueAsync(CancellationToken cancellationToken, IReadOnlyList<string>? ids = null)
    {
        var db = _dbProvider.Database;
        List<QueueItem> queuedItems;

        if (ids != null && ids.Count > 0)
        {
            queuedItems = new List<QueueItem>();
            foreach (var id in ids)
            {
                var item = db.GetItemById(id);
                if (item?.Status == QueueStatus.Error)
                {
                    // Selecting a failed item and starting it again is how it is retried.
                    db.UpdateStatus(item.Id, QueueStatus.Queued);
                    item.Status = QueueStatus.Queued;
                    item.ErrorMessage = null;
                }

                if (item != null && item.Status == QueueStatus.Queued)
                {
                    queuedItems.Add(item);
                }
            }
        }
        else
        {
            queuedItems = db.GetItemsByStatus(QueueStatus.Queued);
        }

        if (queuedItems.Count == 0)
        {
            _logger.LogDebug("No items in queue to process");
            return;
        }

        await _downloadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Provision yt-dlp (and keep the plugin-managed copy current) plus the Deno runtime it
            // needs for YouTube, before any download starts.
            await EnsureYtDlpAsync().ConfigureAwait(false);
            await UpdateManagedYtDlpIfDueAsync().ConfigureAwait(false);
            var toolOptions = await CreateToolOptionsAsync().ConfigureAwait(false);

            var cacheDir = _dbProvider.GetAudioCacheDirectory();
            var ytdl = CreateYoutubeDL();
            ytdl.OutputFolder = cacheDir;

            foreach (var candidate in queuedItems)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                // Re-check under the lock: another caller may have processed this item while we
                // waited for the semaphore, and re-downloading would clobber edited tags.
                var item = db.GetItemById(candidate.Id);
                if (item == null || item.Status != QueueStatus.Queued)
                {
                    continue;
                }

                db.UpdateStatus(item.Id, QueueStatus.Downloading);

                try
                {
                    // Playlist-imported tracks have no URL yet: find the YouTube upload first.
                    if (string.IsNullOrEmpty(item.Url) && !await ResolvePlaylistTrackAsync(ytdl, toolOptions, item, cancellationToken).ConfigureAwait(false))
                    {
                        continue;
                    }

                    var outputPath = Path.Combine(cacheDir, item.FileName);
                    var nameWithoutExt = Path.GetFileNameWithoutExtension(item.FileName);

                    var options = new OptionSet
                    {
                        WriteInfoJson = true,
                        EmbedThumbnail = false,
                        // A queue item always means one video: playlist URLs are expanded into
                        // individual items at queue time, so never let yt-dlp expand one here.
                        NoPlaylist = true,
                        Output = Path.Combine(cacheDir, nameWithoutExt + ".%(ext)s")
                    };
                    options = options.OverrideOptions(toolOptions);

                    var format = MapToAudioConversionFormat(Config.AudioFormat);
                    var runResult = await RunWithRetriesAsync(
                        () => ytdl.RunAudioDownload(item.Url, format, ct: cancellationToken, overrideOptions: options),
                        result => result.Success,
                        result => result.ErrorOutput,
                        "download " + item.Url,
                        cancellationToken).ConfigureAwait(false);

                    if (runResult.Success)
                    {
                        // yt-dlp's AAC postprocessor emits an MP4 whose box header TagLib cannot
                        // parse, so every later tag read/write would otherwise hit the reactive
                        // remux fallback (and submitted metadata would silently fail to apply). A
                        // one-time stream-copy remux now rewrites the container cleanly — no
                        // re-encode, no quality loss — so the cached file is TagLib-healthy.
                        if (Config.AudioFormat == AudioFormat.M4a)
                        {
                            TryRemuxContainer(outputPath);
                        }

                        // Refine the title from info.json unless the user supplied one at submit
                        // time. Prefers the YouTube Music track name over the (cleaned) video title,
                        // including for playlist entries, which were queued with the flat video title.
                        if (!item.UserTitled)
                        {
                            var infoJsonPath = Path.Combine(cacheDir, nameWithoutExt + ".info.json");
                            if (File.Exists(infoJsonPath))
                            {
                                var title = ExtractTitleFromInfoJson(infoJsonPath);
                                if (!string.IsNullOrEmpty(title))
                                {
                                    db.UpdateTitle(item.Id, title);
                                    item.Title = title;
                                }
                            }
                        }

                        ApplyQueuedMetadata(item, cacheDir);
                        await ApplyCatalogMetadataAsync(item, outputPath, cancellationToken).ConfigureAwait(false);

                        db.UpdateStatus(item.Id, QueueStatus.Downloaded);
                        _logger.LogInformation("Downloaded {Url} as {FileName}", SanitizeForLog(item.Url), item.FileName);
                    }
                    else
                    {
                        var errors = string.Join("; ", runResult.ErrorOutput ?? Array.Empty<string>());
                        db.UpdateStatus(item.Id, QueueStatus.Error, "Download failed: " + errors);
                        _logger.LogWarning("Download failed for {Url}: {Errors}", SanitizeForLog(item.Url), errors);
                    }
                }
                catch (OperationCanceledException)
                {
                    db.UpdateStatus(item.Id, QueueStatus.Queued); // Reset to queued so it can be retried
                    _logger.LogInformation("Download cancelled for {Url}", SanitizeForLog(item.Url));
                    break;
                }
                catch (Exception ex)
                {
                    db.UpdateStatus(item.Id, QueueStatus.Error, ex.Message);
                    _logger.LogError(ex, "Error downloading {Url}", SanitizeForLog(item.Url));
                }
            }
        }
        finally
        {
            _downloadLock.Release();
        }
    }

    /// <summary>
    /// Points a queue item at a different YouTube upload (to fix a wrong or failed match) and
    /// queues it again. Its playlist details, metadata and playlist entries are kept; a file
    /// already downloaded from the old link is discarded.
    /// </summary>
    /// <param name="id">The queue item ID.</param>
    /// <param name="url">The YouTube link to use.</param>
    /// <returns>The updated item.</returns>
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "File paths use GUID-based names from database lookup.")]
    public QueueItemDto ChangeUrl(string id, string url)
    {
        url = url?.Trim() ?? string.Empty;
        if (!IsValidYouTubeUrl(url))
        {
            throw new ArgumentException("Enter a YouTube link.");
        }

        var db = _dbProvider.Database;
        var item = db.GetItemById(id) ?? throw new FileNotFoundException("Queue item not found.");
        if (item.Status is QueueStatus.Downloading or QueueStatus.Imported)
        {
            throw new InvalidOperationException(item.Status == QueueStatus.Downloading
                ? "This item is downloading; change its link once it finishes."
                : "This item is already in the library.");
        }

        if (item.Status == QueueStatus.Downloaded)
        {
            DeleteCachedFiles(item);
        }

        db.UpdateResolvedUrl(item.Id, url, null);
        db.UpdateStatus(item.Id, QueueStatus.Queued);
        _logger.LogInformation("Changed the YouTube link of {Id} to {Url}", id, SanitizeForLog(url));

        return db.GetItemById(id)!.ToDto();
    }

    /// <summary>
    /// Searches YouTube for a playlist-imported track and records the best match on the item.
    /// Marks the item as Error and returns false when nothing acceptable is found.
    /// </summary>
    private async Task<bool> ResolvePlaylistTrackAsync(YoutubeDL ytdl, OptionSet toolOptions, QueueItem item, CancellationToken cancellationToken)
    {
        var db = _dbProvider.Database;
        var track = item.SourceTrack;
        if (track == null)
        {
            db.UpdateStatus(item.Id, QueueStatus.Error, "This item has no YouTube URL or track details to search for.");
            return false;
        }

        var query = YouTubeMatcher.BuildSearchQuery(track);
        var search = await RunWithRetriesAsync(
            () => ytdl.RunVideoDataFetch($"ytsearch{YouTubeMatcher.SearchResultCount}:{query}", cancellationToken, flat: true, overrideOptions: toolOptions),
            result => result.Success,
            result => result.ErrorOutput,
            "search " + query,
            cancellationToken).ConfigureAwait(false);

        if (!search.Success)
        {
            var errors = string.Join("; ", search.ErrorOutput ?? Array.Empty<string>());
            db.UpdateStatus(item.Id, QueueStatus.Error, "YouTube search failed: " + errors);
            _logger.LogWarning("YouTube search failed for {Query}: {Errors}", SanitizeForLog(query), errors);
            return false;
        }

        var candidates = (search.Data?.Entries ?? Array.Empty<YoutubeDLSharp.Metadata.VideoData>())
            .Where(entry => !string.IsNullOrEmpty(entry.ID))
            .Select(entry => new MatchCandidate(entry.ID, entry.Title ?? string.Empty, entry.Channel ?? entry.Uploader, entry.Duration));

        var match = YouTubeMatcher.PickBest(track, candidates);
        if (match == null)
        {
            db.UpdateStatus(item.Id, QueueStatus.Error, $"No matching YouTube upload found for \"{query}\".");
            _logger.LogInformation("No YouTube match for {Query}", SanitizeForLog(query));
            return false;
        }

        var url = "https://www.youtube.com/watch?v=" + match.Candidate.Id;
        var note = YouTubeMatcher.DescribeConcerns(match);
        db.UpdateResolvedUrl(item.Id, url, note);
        item.Url = url;
        item.MatchNote = note;
        _logger.LogInformation("Matched {Query} to {Url} ({Title})", SanitizeForLog(query), url, SanitizeForLog(match.Candidate.Title));
        return true;
    }

    /// <summary>
    /// Runs a yt-dlp call, retrying with a short backoff when it fails for a transient reason
    /// (YouTube throttling with 403/429, server errors, network hiccups). Permanent failures, such
    /// as a removed video, are returned straight away.
    /// </summary>
    private async Task<T> RunWithRetriesAsync<T>(
        Func<Task<T>> run,
        Func<T, bool> succeeded,
        Func<T, IEnumerable<string>?> errors,
        string what,
        CancellationToken cancellationToken)
    {
        var result = await run().ConfigureAwait(false);
        foreach (var delay in _retryDelays)
        {
            if (succeeded(result) || !IsTransientFailure(errors(result)))
            {
                break;
            }

            _logger.LogInformation("Retrying {What} in {Seconds}s after a transient failure", SanitizeForLog(what), delay.TotalSeconds);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            result = await run().ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Returns whether yt-dlp's error output describes a failure worth retrying.
    /// </summary>
    internal static bool IsTransientFailure(IEnumerable<string>? errorOutput)
    {
        if (errorOutput == null)
        {
            return false;
        }

        var text = string.Join(' ', errorOutput);
        return _transientErrorMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Starts processing the given queue items in the background. Nothing awaits the result, so
    /// failures that abort the whole run (e.g. yt-dlp could not be provisioned) are surfaced by
    /// marking the affected items as Error instead of disappearing with the discarded task.
    /// </summary>
    /// <param name="ids">The queue item ids to process, or null/empty for all queued items.</param>
    public void StartProcessing(IReadOnlyList<string>? ids = null)
    {
        var db = _dbProvider.Database;
        var targetIds = ids is { Count: > 0 }
            ? ids.ToList()
            : db.GetItemsByStatus(QueueStatus.Queued).Select(i => i.Id).ToList();

        if (targetIds.Count == 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await ProcessQueueAsync(CancellationToken.None, targetIds).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background queue processing failed");
                foreach (var id in targetIds)
                {
                    var item = db.GetItemById(id);
                    if (item != null && item.Status is QueueStatus.Queued or QueueStatus.Downloading)
                    {
                        db.UpdateStatus(id, QueueStatus.Error, "Processing failed: " + ex.Message);
                    }
                }
            }
        });
    }

    /// <summary>
    /// Stops tracking a playlist target.
    /// </summary>
    public void DeletePlaylistTarget(string id) => _dbProvider.Database.DeletePlaylistTarget(id);

    /// <summary>
    /// Lists queued tracks for duplicate detection.
    /// </summary>
    public List<QueuedTrack> GetQueuedTracks()
        => _dbProvider.Database.GetAllItems()
            .Where(item => !string.IsNullOrWhiteSpace(item.Title))
            .Select(item => new QueuedTrack
            {
                Id = item.Id,
                Title = item.Title!,
                Artist = item.Artist,
                Status = item.Status,
                ReleaseType = item.SourceTrack?.ReleaseType
            })
            .ToList();

    /// <summary>
    /// Gets the queue as the public, read-only page shows it: oldest first, without URLs or
    /// error details.
    /// </summary>
    /// <param name="sourceNames">Display names for playlist source IDs.</param>
    public List<PublicQueueItem> GetPublicQueue(IReadOnlyDictionary<string, string> sourceNames)
    {
        ArgumentNullException.ThrowIfNull(sourceNames);

        return _dbProvider.Database.GetAllItems()
            .Where(item => item.Status != QueueStatus.Imported)
            .OrderBy(item => item.CreatedAt, StringComparer.Ordinal)
            .Select(item => new PublicQueueItem
            {
                Title = item.Title,
                Artist = item.Artist,
                Album = item.Album,
                StatusCode = (int)item.Status,
                Status = item.Status switch
                {
                    QueueStatus.Queued => "Waiting to download",
                    QueueStatus.Downloading => "Downloading",
                    QueueStatus.Downloaded => "Downloaded, not yet added",
                    QueueStatus.Error => "Could not be downloaded",
                    _ => item.Status.ToString()
                },
                Source = item.Source == null ? "YouTube" : sourceNames.GetValueOrDefault(item.Source) ?? item.Source,
                CreatedAt = item.CreatedAt
            })
            .ToList();
    }

    /// <summary>
    /// Gets all queue items as DTOs.
    /// </summary>
    public List<QueueItemDto> GetQueueItems()
    {
        return _dbProvider.Database.GetAllItems().Select(item => item.ToDto()).ToList();
    }

    /// <summary>
    /// Gets downloaded items with metadata populated from tags and info.json.
    /// </summary>
    public List<QueueItemDto> GetDownloadedItems()
    {
        var db = _dbProvider.Database;
        var items = db.GetDownloadedItems();
        var cacheDir = _dbProvider.GetAudioCacheDirectory();
        var librarySongs = items.Count == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : _libraryService.GetLibraryTracks().Select(t => TrackMetadataCleaner.SongKey(t.Artist, t.Title)).ToHashSet(StringComparer.Ordinal);

        return items.Select(item =>
        {
            var dto = item.ToDto();
            var filePath = Path.Combine(cacheDir, item.FileName);

            if (File.Exists(filePath))
            {
                dto.FileSize = new FileInfo(filePath).Length;
                ReadTagsFromFile(filePath, dto);

                // Always check info.json — prefer track name over video title
                var infoJsonPath = GetInfoJsonPath(filePath);
                if (File.Exists(infoJsonPath))
                {
                    PopulateFromInfoJson(infoJsonPath, dto);
                }
            }

            dto.InLibrary = librarySongs.Contains(TrackMetadataCleaner.SongKey(dto.Artist, dto.MetadataTitle));
            return dto;
        }).ToList();
    }

    /// <summary>
    /// Gets the cached audio file of a downloaded item, for previewing it before import.
    /// Returns null unless the item is downloaded and its file is in the cache folder.
    /// </summary>
    /// <param name="id">The queue item ID.</param>
    /// <returns>The file path and its media type, or null.</returns>
    public (string Path, string ContentType)? GetPreviewFile(string id)
    {
        var item = _dbProvider.Database.GetItemById(id);
        if (item == null || item.Status != QueueStatus.Downloaded)
        {
            return null;
        }

        // File names are GUIDs from the database, but confine the path to the cache anyway.
        var cacheDir = Path.GetFullPath(_dbProvider.GetAudioCacheDirectory());
        var path = Path.GetFullPath(Path.Combine(cacheDir, item.FileName));
        if (!path.StartsWith(cacheDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path))
        {
            return null;
        }

        return (path, GetAudioContentType(path));
    }

    /// <summary>
    /// Maps a cached audio file to the media type browsers expect for playback.
    /// </summary>
    internal static string GetAudioContentType(string path)
        => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".opus" => "audio/ogg",
            ".mp3" => "audio/mpeg",
            ".m4a" => "audio/mp4",
            _ => "application/octet-stream"
        };

    // ===== Tag Operations =====

    /// <summary>
    /// Saves metadata tags to a cached audio file.
    /// </summary>
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "File paths use GUID-based names from database lookup, not user input.")]
    public void SaveTags(TagUpdateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var db = _dbProvider.Database;
        var item = db.GetItemById(request.Id)
            ?? throw new FileNotFoundException($"Queue item not found: {request.Id}");

        var filePath = Path.Combine(_dbProvider.GetAudioCacheDirectory(), item.FileName);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Audio file not found: {item.FileName}");
        }

        TagLib.File? tagFile = null;
        try
        {
            tagFile = TagLib.File.Create(filePath);
        }
        catch (Exception ex) when (ex is ArgumentNullException or TagLib.CorruptFileException)
        {
            _logger.LogWarning("TagLib failed to read {File}, attempting ffmpeg remux", item.FileName);
            if (TryRemuxContainer(filePath))
            {
                tagFile = TagLib.File.Create(filePath);
            }
            else
            {
                throw new InvalidOperationException($"File is corrupt and remux failed: {item.FileName}", ex);
            }
        }

        using var _ = tagFile;

        tagFile.Tag.Title = request.Title;

        // Build performers array: primary artist + optional featured artist(s)
        var performers = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.Artist))
        {
            performers.Add(request.Artist);
        }

        if (!string.IsNullOrWhiteSpace(request.FeaturedArtist))
        {
            performers.AddRange(request.FeaturedArtist.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        tagFile.Tag.Performers = performers.ToArray();

        // Album artist: use explicit value, or default to primary artist
        var albumArtist = !string.IsNullOrWhiteSpace(request.AlbumArtist)
            ? request.AlbumArtist
            : request.Artist;
        tagFile.Tag.AlbumArtists = !string.IsNullOrWhiteSpace(albumArtist)
            ? new[] { albumArtist }
            : Array.Empty<string>();

        tagFile.Tag.Album = request.Album;
        tagFile.Tag.Track = request.TrackNumber.HasValue ? (uint)request.TrackNumber.Value : 0;
        tagFile.Tag.Year = request.Year.HasValue ? (uint)request.Year.Value : 0;
        tagFile.Tag.Genres = ParseGenres(request.Genre);

        // Embedded cover art (from the catalog lookup) is kept.
        tagFile.Save();

        _logger.LogInformation("Tags saved for queue item {Id} ({FileName})", request.Id, item.FileName);
    }

    /// <summary>
    /// Writes metadata supplied at submit time onto the downloaded file so it carries through import.
    /// Only the submitted fields are written; auto-detected fields are left for the admin to refine.
    /// </summary>
    /// <param name="item">The queue item carrying the submitted metadata.</param>
    /// <param name="cacheDir">The audio cache directory.</param>
    private void ApplyQueuedMetadata(QueueItem item, string cacheDir)
    {
        var hasMetadata = !string.IsNullOrWhiteSpace(item.Artist)
            || !string.IsNullOrWhiteSpace(item.Album)
            || item.Year.HasValue
            || !string.IsNullOrWhiteSpace(item.Title)
            || item.SourceTrack != null;
        if (!hasMetadata)
        {
            return;
        }

        var filePath = Path.Combine(cacheDir, item.FileName);
        if (!File.Exists(filePath))
        {
            return;
        }

        try
        {
            using var tagFile = TagLib.File.Create(filePath);

            if (!string.IsNullOrWhiteSpace(item.Title))
            {
                tagFile.Tag.Title = item.Title;
            }

            if (!string.IsNullOrWhiteSpace(item.Artist))
            {
                tagFile.Tag.Performers = new[] { item.Artist! };
                tagFile.Tag.AlbumArtists = new[] { item.Artist! };
            }

            if (!string.IsNullOrWhiteSpace(item.Album))
            {
                tagFile.Tag.Album = item.Album;
            }

            if (item.Year.HasValue)
            {
                tagFile.Tag.Year = (uint)item.Year.Value;
            }

            if (item.SourceTrack != null)
            {
                ApplySourceTrackTags(tagFile.Tag, item.SourceTrack);
            }

            tagFile.Save();
            _logger.LogInformation("Applied submitted metadata to {FileName}", item.FileName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to apply submitted metadata to {FileName}", item.FileName);
        }
    }

    /// <summary>
    /// Looks a download up in Apple's catalog and fills in what YouTube does not provide: the
    /// title, album, album artist, release year, track and disc numbers, genre and cover art. Only a
    /// confident match is used. A title, artist, album or year given when the song was queued
    /// is kept. Playlist imports already carry the export's details, so they only get the
    /// cover. A failed or empty lookup leaves the file as it is.
    /// </summary>
    private async Task ApplyCatalogMetadataAsync(QueueItem item, string filePath, CancellationToken cancellationToken)
    {
        var lookUp = Config.LookUpCatalogMetadata;
        if ((!lookUp && !Config.EmbedCoverArt) || !File.Exists(filePath))
        {
            return;
        }

        try
        {
            var info = ReadInfoJson(GetInfoJsonPath(filePath));
            using var tagFile = TagLib.File.Create(filePath);
            var tag = tagFile.Tag;
            var artist = !string.IsNullOrWhiteSpace(item.Artist) ? item.Artist : tag.FirstPerformer ?? info.Artist;
            var title = tag.Title ?? item.Title;
            var duration = tagFile.Properties.Duration.TotalSeconds > 0 ? tagFile.Properties.Duration.TotalSeconds : info.Duration;

            var song = await _catalog.FindSongAsync(artist, title, duration, cancellationToken).ConfigureAwait(false);
            if (song == null)
            {
                _logger.LogInformation("No Apple Music match for {Artist} - {Title}", SanitizeForLog(artist), SanitizeForLog(title));
                return;
            }

            if (lookUp && item.SourceTrack == null)
            {
                // Apple's title is cleaner than a video title; a title typed by the user stays.
                var (appleTitle, _) = TrackMetadataCleaner.ExtractFeatured(song.TrackName);
                appleTitle = TrackMetadataCleaner.StripTitleDesignations(appleTitle);
                if (!item.UserTitled && appleTitle.Length > 0)
                {
                    tag.Title = appleTitle;
                    _dbProvider.Database.UpdateTitle(item.Id, appleTitle);
                    item.Title = appleTitle;
                }

                if (string.IsNullOrWhiteSpace(item.Artist))
                {
                    var (_, titleFeatured) = TrackMetadataCleaner.ExtractFeatured(song.TrackName);
                    var performers = await ResolveCreditAsync(song.ArtistName, null, cancellationToken).ConfigureAwait(false);
                    tag.Performers = performers.Concat(titleFeatured)
                        .DistinctBy(TrackMetadataCleaner.Normalize, StringComparer.Ordinal)
                        .ToArray();
                    tag.AlbumArtists = new[] { await ResolveAlbumArtistAsync(song.AlbumArtist, null, cancellationToken).ConfigureAwait(false) };
                }

                if (string.IsNullOrWhiteSpace(item.Album))
                {
                    tag.Album = CatalogMatcher.CleanAlbumName(song.AlbumName);
                }

                if (!item.Year.HasValue && song.Year.HasValue)
                {
                    tag.Year = (uint)song.Year.Value;
                }

                tag.Track = (uint)(song.TrackNumber ?? 0);
                tag.TrackCount = (uint)(song.TrackCount ?? 0);
                tag.Disc = (uint)(song.DiscNumber ?? 0);
                tag.DiscCount = (uint)(song.DiscCount ?? 0);
                if (tag.Genres.Length == 0 && !string.IsNullOrWhiteSpace(song.Genre))
                {
                    tag.Genres = ParseGenres(song.Genre);
                }
            }

            if (Config.EmbedCoverArt && tag.Pictures.Length == 0)
            {
                var artwork = await _catalog.GetArtworkAsync(song.ArtworkUrl, CoverArtSize, cancellationToken).ConfigureAwait(false);
                if (artwork is { Length: > 0 })
                {
                    tag.Pictures = new TagLib.IPicture[]
                    {
                        new TagLib.Picture(new TagLib.ByteVector(artwork)) { Type = TagLib.PictureType.FrontCover, MimeType = "image/jpeg", Description = "Cover" }
                    };
                }
            }

            tagFile.Save();
            _logger.LogInformation("Matched {Artist} - {Title} to {Album} ({Year}) on Apple Music", SanitizeForLog(artist), SanitizeForLog(title), SanitizeForLog(song.AlbumName), song.Year);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Apple Music lookup failed for {FileName}", item.FileName);
        }
    }

    /// <summary>
    /// Splits a joint artist credit into its artists, keeping names the library or Apple knows
    /// as a single act whole, and spells each one the way the library already does.
    /// </summary>
    private async Task<List<string>> ResolveCreditAsync(string credit, Dictionary<string, string>? spellings, CancellationToken cancellationToken)
    {
        spellings ??= _libraryService.GetArtistSpellings();
        if (!Config.LookUpCatalogMetadata)
        {
            // Without Apple's artist list, "Simon & Garfunkel" cannot be told from a joint
            // credit, so only the explicit multi-artist separator splits.
            return credit.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(name => spellings.GetValueOrDefault(TrackMetadataCleaner.Normalize(name), name))
                .ToList();
        }

        var oneActs = new HashSet<string>(StringComparer.Ordinal);

        // The whole credit and any "A & B" pair inside a list could be one act's name.
        var candidates = new[] { credit }
            .Concat(credit.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(c => c.Contains(" & ", StringComparison.Ordinal) || c.Contains(',', StringComparison.Ordinal) || c.Contains(';', StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var key = TrackMetadataCleaner.Normalize(candidate);
            if (spellings.ContainsKey(key)
                || (Config.LookUpCatalogMetadata && await _catalog.IsArtistAsync(candidate, cancellationToken).ConfigureAwait(false)))
            {
                oneActs.Add(key);
            }
        }

        return TrackMetadataCleaner.SplitCredit(credit, name => oneActs.Contains(TrackMetadataCleaner.Normalize(name)))
            .Select(name => spellings.GetValueOrDefault(TrackMetadataCleaner.Normalize(name), name))
            .ToList();
    }

    /// <summary>
    /// Gets the album artist for a credit: "Various Artists" stays, a joint credit is filed
    /// under its first artist, and the library's spelling is used.
    /// </summary>
    private async Task<string> ResolveAlbumArtistAsync(string credit, Dictionary<string, string>? spellings, CancellationToken cancellationToken)
    {
        if (string.Equals(credit, "Various Artists", StringComparison.OrdinalIgnoreCase))
        {
            return credit;
        }

        var artists = await ResolveCreditAsync(credit, spellings, cancellationToken).ConfigureAwait(false);
        return artists.Count > 0 ? artists[0] : credit;
    }

    /// <summary>
    /// Writes the playlist export's extra fields (beyond title, artist, album and year). Only
    /// fields the source provided are written. The album artist replaces the track artist that
    /// was written as album artist, so compilations file under "Various Artists".
    /// </summary>
    internal static void ApplySourceTrackTags(TagLib.Tag tag, PlaylistTrack track)
    {
        // Primary artist first, then featured: how the Import tab reads performers back.
        if (track.FeaturedArtists is { Count: > 0 } && !string.IsNullOrWhiteSpace(track.Artist))
        {
            tag.Performers = new[] { track.Artist }.Concat(track.FeaturedArtists).ToArray();
        }

        if (!string.IsNullOrWhiteSpace(track.AlbumArtist))
        {
            tag.AlbumArtists = new[] { track.AlbumArtist };
        }

        if (!string.IsNullOrWhiteSpace(track.Composer))
        {
            tag.Composers = new[] { track.Composer };
        }

        if (!string.IsNullOrWhiteSpace(track.Genre))
        {
            tag.Genres = ParseGenres(track.Genre);
        }

        if (track.TrackNumber.HasValue)
        {
            tag.Track = (uint)track.TrackNumber.Value;
        }

        if (track.TrackCount.HasValue)
        {
            tag.TrackCount = (uint)track.TrackCount.Value;
        }

        if (track.DiscNumber.HasValue)
        {
            tag.Disc = (uint)track.DiscNumber.Value;
        }

        if (track.DiscCount.HasValue)
        {
            tag.DiscCount = (uint)track.DiscCount.Value;
        }
    }

    // ===== Import Operations =====

    /// <summary>
    /// Imports downloaded files to the Jellyfin music library.
    /// </summary>
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "File paths use GUID-based names from database lookup; destination paths are sanitized.")]
    public async Task<ImportResult> ImportToLibraryAsync(List<string> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var result = new ImportResult();
        var db = _dbProvider.Database;
        var libraryPath = Config.MusicLibraryPath;

        if (string.IsNullOrWhiteSpace(libraryPath))
        {
            result.Errors.Add("Music library is not configured. Please select a library in Settings.");
            return result;
        }

        var cacheDir = _dbProvider.GetAudioCacheDirectory();
        var spellings = _libraryService.GetArtistSpellings();

        foreach (var id in ids)
        {
            try
            {
                var item = db.GetItemById(id);
                if (item == null)
                {
                    result.Errors.Add($"Queue item not found: {id}");
                    continue;
                }

                if (item.Status != QueueStatus.Downloaded)
                {
                    result.Errors.Add($"Item {item.Title ?? item.Id} is not in Downloaded status.");
                    continue;
                }

                var sourcePath = Path.Combine(cacheDir, item.FileName);
                if (!File.Exists(sourcePath))
                {
                    result.Errors.Add($"Audio file not found for {item.Title ?? item.Id}");
                    continue;
                }

                // Read tags to determine destination path.
                // Some Opus files have corrupt Vorbis Comment headers; remux with ffmpeg if needed.
                TagLib.File? tagFile = null;
                try
                {
                    tagFile = TagLib.File.Create(sourcePath);
                }
                catch (Exception ex) when (ex is ArgumentNullException or TagLib.CorruptFileException)
                {
                    _logger.LogWarning("TagLib failed to read {File}, attempting ffmpeg remux: {Msg}", item.FileName, ex.Message);
                    if (TryRemuxContainer(sourcePath))
                    {
                        tagFile = TagLib.File.Create(sourcePath);
                    }
                    else
                    {
                        result.Errors.Add($"Cannot read tags for {item.Title ?? item.Id}: file is corrupt and remux failed.");
                        continue;
                    }
                }

                string title;
                string ext;
                string destDir;
                string destPath;
                bool isDuplicate;

                using (tagFile)
                {
                    await NormalizeArtistsAsync(tagFile, spellings, cancellationToken).ConfigureAwait(false);

                    // Titles are cleaned when written (queue/download time) and user edits are
                    // saved verbatim, so trust the tag as-is here — re-cleaning would mangle a
                    // deliberately saved title like "Song (Lyrics)".
                    var rawTitle = tagFile.Tag.Title;
                    if (string.IsNullOrWhiteSpace(rawTitle))
                    {
                        rawTitle = Path.GetFileNameWithoutExtension(item.FileName);
                    }

                    var artist = SanitizePathComponent(tagFile.Tag.FirstAlbumArtist ?? tagFile.Tag.FirstPerformer ?? "Unknown Artist");
                    var album = SanitizePathComponent(tagFile.Tag.Album ?? "Unknown Album");
                    var year = tagFile.Tag.Year > 0 ? tagFile.Tag.Year.ToString(CultureInfo.InvariantCulture) : string.Empty;
                    title = SanitizePathComponent(rawTitle);
                    ext = Path.GetExtension(item.FileName);

                    // Build: library/artist/album (year)/title.ext
                    var albumFolder = !string.IsNullOrEmpty(year) ? $"{album} ({year})" : album;
                    destDir = Path.Combine(libraryPath, artist, albumFolder);
                    Directory.CreateDirectory(destDir);

                    destPath = Path.Combine(destDir, title + ext);
                    isDuplicate = File.Exists(destPath);
                }

                // Handle duplicates based on setting
                if (isDuplicate && !Config.ReplaceDuplicates)
                {
                    result.SkippedIds.Add(id);
                    _logger.LogInformation("Skipped duplicate {Title} at {Dest}", title, destPath);
                    continue;
                }

                // Move file with cross-volume fallback
                try
                {
                    File.Move(sourcePath, destPath, overwrite: true);
                }
                catch (IOException)
                {
                    // Cross-volume: fallback to copy + delete
                    File.Copy(sourcePath, destPath, overwrite: true);
                    File.Delete(sourcePath);
                }

                // An album version replaces the single (or other lesser version) already in the library.
                if (item.SourceTrack?.ReplacesFiles is { Count: > 0 } replaced)
                {
                    var removed = RemoveReplacedFiles(replaced, destPath, libraryPath);
                    result.RemovedFiles += removed;

                    // Playlists holding the deleted single should get the album version instead.
                    if (removed > 0)
                    {
                        db.ResetPlaylistEntriesForSong(TrackMetadataCleaner.SongKey(item.Artist, item.Title));
                    }
                }

                // Clean up info.json from cache
                var infoJsonPath = GetInfoJsonPath(sourcePath);
                if (File.Exists(infoJsonPath))
                {
                    File.Delete(infoJsonPath);
                }

                // The file has left the cache, so the queue row has nothing left to track. Playlists
                // waiting on this song now wait for the library scan instead.
                db.MarkPlaylistEntriesImported(TrackMetadataCleaner.SongKey(item.Artist, item.Title));
                db.DeleteItem(id);
                result.ImportedIds.Add(id);
                if (isDuplicate)
                {
                    result.ReplacedIds.Add(id);
                    _logger.LogInformation("Replaced {Title} at {Dest}", title, destPath);
                }
                else
                {
                    _logger.LogInformation("Imported {Title} to {Dest}", title, destPath);
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Failed to import {id}: {ex.Message}");
                _logger.LogWarning(ex, "Error importing queue item {Id}", id);
            }
        }

        return result;
    }

    /// <summary>
    /// Final artist cleanup before a file is filed: joint credits are split into separate
    /// artists (the album files under the first), and each artist is spelled the way the
    /// library already spells it. Saves the file only when something changed.
    /// </summary>
    private async Task NormalizeArtistsAsync(TagLib.File tagFile, Dictionary<string, string> spellings, CancellationToken cancellationToken)
    {
        var tag = tagFile.Tag;
        var performers = new List<string>();
        foreach (var credit in tag.Performers ?? Array.Empty<string>())
        {
            performers.AddRange(await ResolveCreditAsync(credit, spellings, cancellationToken).ConfigureAwait(false));
        }

        performers = performers.DistinctBy(TrackMetadataCleaner.Normalize, StringComparer.Ordinal).ToList();
        var albumArtists = new List<string>();
        foreach (var credit in tag.AlbumArtists ?? Array.Empty<string>())
        {
            albumArtists.Add(await ResolveAlbumArtistAsync(credit, spellings, cancellationToken).ConfigureAwait(false));
        }

        albumArtists = albumArtists.DistinctBy(TrackMetadataCleaner.Normalize, StringComparer.Ordinal).ToList();
        if (performers.SequenceEqual(tag.Performers ?? Array.Empty<string>(), StringComparer.Ordinal)
            && albumArtists.SequenceEqual(tag.AlbumArtists ?? Array.Empty<string>(), StringComparer.Ordinal))
        {
            return;
        }

        tag.Performers = performers.ToArray();
        tag.AlbumArtists = albumArtists.ToArray();
        tagFile.Save();
    }

    /// <summary>
    /// Deletes library files superseded by a better version of the same song, then removes any
    /// album or artist folders left empty. Only files inside the import library are touched.
    /// </summary>
    /// <returns>The number of files deleted.</returns>
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Paths come from Jellyfin's own library items and are confined to the configured library root.")]
    private int RemoveReplacedFiles(IEnumerable<string> paths, string keptPath, string libraryPath)
    {
        var root = Path.GetFullPath(libraryPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var kept = Path.GetFullPath(keptPath);
        var removed = 0;

        foreach (var path in paths)
        {
            try
            {
                var full = Path.GetFullPath(path);
                if (!full.StartsWith(root, StringComparison.Ordinal) || string.Equals(full, kept, StringComparison.Ordinal) || !File.Exists(full))
                {
                    continue;
                }

                File.Delete(full);
                removed++;
                _logger.LogInformation("Removed {Path}, replaced by the album version at {Kept}", full, kept);

                // Drop the album folder, then the artist folder, if nothing is left in them.
                var dir = Path.GetDirectoryName(full);
                for (var depth = 0; depth < 2 && dir != null && dir.StartsWith(root, StringComparison.Ordinal); depth++)
                {
                    if (Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        break;
                    }

                    Directory.Delete(dir);
                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not remove replaced file {Path}", path);
            }
        }

        return removed;
    }

    // ===== Delete / Reset Operations =====

    /// <summary>
    /// Deletes a cached file and its database entry.
    /// </summary>
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "File paths use GUID-based names from database lookup.")]
    public void DeleteCachedFile(string id)
    {
        var db = _dbProvider.Database;
        var item = db.GetItemById(id);

        if (item != null)
        {
            DeleteCachedFiles(item);
            db.DeleteItem(id);
            _logger.LogInformation("Deleted cached file for queue item {Id}", id);
        }
    }

    /// <summary>
    /// Deletes an item's cached audio file and info.json sidecar, if present.
    /// </summary>
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "File paths use GUID-based names from database lookup.")]
    private void DeleteCachedFiles(QueueItem item)
    {
        var filePath = Path.Combine(_dbProvider.GetAudioCacheDirectory(), item.FileName);
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        var infoJsonPath = GetInfoJsonPath(filePath);
        if (File.Exists(infoJsonPath))
        {
            File.Delete(infoJsonPath);
        }
    }

    /// <summary>
    /// Resets the download queue (database only).
    /// </summary>
    public void ResetQueue()
    {
        _dbProvider.Database.ResetQueue();
        _logger.LogInformation("Queue reset");
    }

    /// <summary>
    /// Resets the cache: deletes all cached files and resets the queue database.
    /// </summary>
    public void ResetCache()
    {
        var cacheDir = _dbProvider.GetAudioCacheDirectory();

        if (Directory.Exists(cacheDir))
        {
            foreach (var file in Directory.GetFiles(cacheDir))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete cache file {File}", Path.GetFileName(file));
                }
            }
        }

        _dbProvider.Database.ResetQueue();
        _logger.LogInformation("Cache and queue reset");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _downloadLock.Dispose();
    }

    // ===== Private Helpers =====

    private static QueueItem CreateSingleQueueItem(string url, string ext, string now, string? artist = null, string? album = null, int? year = null, string? title = null)
    {
        var userTitle = string.IsNullOrWhiteSpace(title) ? null : title;
        return new QueueItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Url = url,
            Title = userTitle,
            UserTitled = userTitle != null,
            FileName = Guid.NewGuid().ToString("N") + ext,
            Status = QueueStatus.Queued,
            CreatedAt = now,
            UpdatedAt = now,
            Artist = artist,
            Album = album,
            Year = year
        };
    }


    /// <summary>
    /// Ensures yt-dlp is available. Unless a path is set in Settings, the plugin uses its own
    /// standalone copy in the plugin directory (downloaded on first use, kept current by
    /// <see cref="UpdateManagedYtDlpIfDueAsync"/>), never a possibly outdated system install; a
    /// system copy is only the fallback when the download is impossible.
    /// Must be called before CreateYoutubeDL(). Safe to call under _downloadLock.
    /// </summary>
    private async Task EnsureYtDlpAsync()
    {
        if (GetConfiguredYtDlp() != null || GetManagedYtDlp() != null)
        {
            return;
        }

        var pluginDir = GetPluginDirectory()
            ?? throw new InvalidOperationException("Could not determine the plugin directory. Set the yt-dlp path manually in Settings.");

        _logger.LogInformation("Downloading the standalone yt-dlp to: {Dir}", pluginDir);
        try
        {
            await DownloadStandaloneYtDlpAsync(pluginDir).ConfigureAwait(false);
            _logger.LogInformation("Downloaded yt-dlp to: {Path}", Path.Combine(pluginDir, GetStandaloneBinaryName()));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            if (FindSystemYtDlp() is { } system)
            {
                _logger.LogWarning(ex, "Could not download yt-dlp; falling back to the system copy at {Path}, which may be outdated", system);
                return;
            }

            throw new InvalidOperationException(
                $"Failed to download yt-dlp automatically: {ex.Message}. " +
                "Check your internet connection, or install yt-dlp manually and set the path in Settings.",
                ex);
        }
    }

    /// <summary>
    /// Replaces the plugin-managed yt-dlp when a newer release exists, checking at most every
    /// <see cref="_ytDlpUpdateInterval"/>. Skipped when a path is set in Settings. Failures only
    /// log: the current copy keeps working until the next check. Call under _downloadLock.
    /// </summary>
    private async Task UpdateManagedYtDlpIfDueAsync()
    {
        var managed = GetManagedYtDlp();
        if (managed == null || GetConfiguredYtDlp() != null || DateTime.UtcNow - _lastYtDlpUpdateCheckUtc < _ytDlpUpdateInterval)
        {
            return;
        }

        _lastYtDlpUpdateCheckUtc = DateTime.UtcNow;
        try
        {
            var latest = await GetLatestYtDlpVersionAsync().ConfigureAwait(false);
            var current = (await RunProcessAsync(managed, _versionArgs, TimeSpan.FromSeconds(30)).ConfigureAwait(false)).Trim();
            if (latest == null || string.Equals(latest, current, StringComparison.Ordinal))
            {
                return;
            }

            _logger.LogInformation("Updating yt-dlp from {Current} to {Latest}", current, latest);
            await DownloadStandaloneYtDlpAsync(Path.GetDirectoryName(managed)!).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidOperationException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Could not check for a newer yt-dlp; keeping the current copy");
        }
    }

    /// <summary>
    /// Reads the newest yt-dlp release tag (e.g. "2026.08.19") from GitHub's latest-release
    /// redirect, without downloading anything.
    /// </summary>
    private static async Task<string?> GetLatestYtDlpVersionAsync()
    {
        using var response = await _noRedirectClient.GetAsync(new Uri("https://github.com/yt-dlp/yt-dlp/releases/latest")).ConfigureAwait(false);
        var location = response.Headers.Location?.ToString();
        var marker = location?.LastIndexOf("/tag/", StringComparison.Ordinal) ?? -1;
        return marker < 0 ? null : location![(marker + "/tag/".Length)..].Trim();
    }

    /// <summary>
    /// Builds the options every yt-dlp call needs: the Deno JavaScript runtime, which YouTube
    /// requires; without it downloads intermittently fail with HTTP 403.
    /// </summary>
    private async Task<OptionSet> CreateToolOptionsAsync()
    {
        var options = new OptionSet();
        var deno = await EnsureDenoAsync().ConfigureAwait(false);
        if (deno != null)
        {
            options.AddCustomOption("--js-runtimes", "deno:" + deno);
        }

        return options;
    }

    /// <summary>
    /// Finds Deno (plugin-managed copy first, then a system install), downloading the official
    /// release into the plugin directory when there is none. Returns null if it cannot be had, in
    /// which case downloads still run but YouTube may refuse some of them.
    /// </summary>
    private async Task<string?> EnsureDenoAsync()
    {
        var pluginDir = GetPluginDirectory();
        var denoName = OperatingSystem.IsWindows() ? "deno.exe" : "deno";
        var managed = pluginDir == null ? null : Path.Combine(pluginDir, denoName);
        if (managed != null && File.Exists(managed))
        {
            return managed;
        }

        var system = Utils.GetFullPath("deno")
            ?? _commonDenoPaths.FirstOrDefault(File.Exists);
        if (system != null)
        {
            return system;
        }

        if (managed == null || GetDenoTarget() is not { } target)
        {
            _logger.LogWarning("No Deno build for this platform; YouTube downloads may be refused (HTTP 403)");
            return null;
        }

        try
        {
            _logger.LogInformation("Downloading the Deno JavaScript runtime for yt-dlp to: {Dir}", pluginDir);
            var url = new Uri($"https://github.com/denoland/deno/releases/latest/download/deno-{target}.zip");
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var temp = managed + ".download";
            using (var archive = new System.IO.Compression.ZipArchive(await response.Content.ReadAsStreamAsync().ConfigureAwait(false)))
            {
                var entry = archive.GetEntry(denoName) ?? throw new IOException("The Deno archive has no " + denoName);
                using var source = await entry.OpenAsync().ConfigureAwait(false);
                using var destination = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);
                await source.CopyToAsync(destination).ConfigureAwait(false);
            }

            MakeExecutable(temp);
            File.Move(temp, managed, overwrite: true);
            _logger.LogInformation("Downloaded Deno to: {Path}", managed);
            return managed;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Could not download Deno; YouTube downloads may be refused (HTTP 403)");
            return null;
        }
    }

    /// <summary>
    /// Gets the Deno release target for this platform, or null when Deno publishes none.
    /// </summary>
    internal static string? GetDenoTarget()
    {
        var arm = RuntimeInformation.OSArchitecture == Architecture.Arm64;
        if (OperatingSystem.IsWindows())
        {
            return arm ? "aarch64-pc-windows-msvc" : "x86_64-pc-windows-msvc";
        }

        if (OperatingSystem.IsMacOS())
        {
            return arm ? "aarch64-apple-darwin" : "x86_64-apple-darwin";
        }

        if (OperatingSystem.IsLinux())
        {
            return arm ? "aarch64-unknown-linux-gnu" : "x86_64-unknown-linux-gnu";
        }

        return null;
    }

    private YoutubeDL CreateYoutubeDL()
    {
        var ytdlpPath = FindYtDlp()
            ?? throw new InvalidOperationException(
                "yt-dlp was not found. Set the path in Settings → yt-dlp.");

        _logger.LogInformation("Using yt-dlp at: {Path}", ytdlpPath);

        return new YoutubeDL
        {
            YoutubeDLPath = ytdlpPath,
            FFmpegPath = _mediaEncoder.EncoderPath
        };
    }

    /// <summary>
    /// Finds the yt-dlp to run: the Settings path, then the plugin-managed copy, then (only as a
    /// fallback) a system install.
    /// </summary>
    private string? FindYtDlp()
        => GetConfiguredYtDlp() ?? GetManagedYtDlp() ?? FindSystemYtDlp();

    private static string? GetConfiguredYtDlp()
    {
        var configPath = Plugin.Instance?.Configuration.YtDlpPath;
        return !string.IsNullOrWhiteSpace(configPath) && File.Exists(configPath) ? configPath : null;
    }

    private static string? GetManagedYtDlp()
    {
        var pluginDir = GetPluginDirectory();
        if (string.IsNullOrEmpty(pluginDir))
        {
            return null;
        }

        var standalonePath = Path.Combine(pluginDir, GetStandaloneBinaryName());
        if (File.Exists(standalonePath))
        {
            return standalonePath;
        }

        // Legacy YoutubeDLSharp binary name from older plugin versions.
        var bundledPath = Path.Combine(pluginDir, Utils.YtDlpBinaryName);
        return File.Exists(bundledPath) ? bundledPath : null;
    }

    private string? FindSystemYtDlp()
    {
        var pathResult = Utils.GetFullPath("yt-dlp");
        if (pathResult != null)
        {
            return pathResult;
        }

        // Common install locations (macOS GUI apps have limited PATH)
        string[] commonPaths =
        [
            "/opt/homebrew/bin/yt-dlp",     // macOS ARM Homebrew
            "/usr/local/bin/yt-dlp",        // macOS Intel Homebrew / manual installs
            "/usr/bin/yt-dlp",              // Linux package managers
            "/snap/bin/yt-dlp"              // Snap installs
        ];

        var found = commonPaths.FirstOrDefault(File.Exists);
        if (found != null)
        {
            _logger.LogInformation("Found yt-dlp at common location: {Path}", found);
        }

        return found;
    }

    private static string? GetPluginDirectory()
    {
        var assemblyLocation = typeof(Plugin).Assembly.Location;
        return string.IsNullOrEmpty(assemblyLocation) ? null : Path.GetDirectoryName(assemblyLocation);
    }

    /// <summary>
    /// Gets the platform-specific standalone yt-dlp binary name.
    /// Unlike the Python script version, standalone binaries don't require python3.
    /// </summary>
    private static string GetStandaloneBinaryName()
    {
        if (OperatingSystem.IsWindows())
        {
            return "yt-dlp.exe";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "yt-dlp_macos";
        }

        return RuntimeInformation.OSArchitecture == Architecture.Arm64
            ? "yt-dlp_linux_aarch64"
            : "yt-dlp_linux";
    }

    /// <summary>
    /// Downloads the standalone yt-dlp binary from the official GitHub releases. Written to a
    /// temporary file and moved into place, so a copy that is running is never half-overwritten.
    /// </summary>
    private static async Task DownloadStandaloneYtDlpAsync(string directory)
    {
        var binaryName = GetStandaloneBinaryName();
        var url = new Uri($"https://github.com/yt-dlp/yt-dlp/releases/latest/download/{binaryName}");
        var destPath = Path.Combine(directory, binaryName);
        var tempPath = destPath + ".download";

        using (var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            using var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await response.Content.CopyToAsync(fs).ConfigureAwait(false);
        }

        MakeExecutable(tempPath);
        File.Move(tempPath, destPath, overwrite: true);
    }

    private static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    /// <summary>
    /// Updates yt-dlp. When a custom path is configured, that binary's own self-updater is run
    /// (yt-dlp -U); otherwise the latest standalone binary is downloaded to the plugin directory,
    /// which takes precedence over any system-installed copy in <see cref="FindYtDlp"/>.
    /// Serialized against downloads so the binary is never replaced mid-download.
    /// </summary>
    /// <returns>A human-readable result message.</returns>
    public async Task<string> UpdateYtDlpAsync()
    {
        if (!await _downloadLock.WaitAsync(TimeSpan.Zero).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Downloads are in progress. Try again after they finish.");
        }

        try
        {
            var configPath = Plugin.Instance?.Configuration.YtDlpPath;
            if (!string.IsNullOrWhiteSpace(configPath))
            {
                if (!File.Exists(configPath))
                {
                    throw new InvalidOperationException($"Configured yt-dlp path does not exist: {configPath}");
                }

                _logger.LogInformation("Running self-update for configured yt-dlp at {Path}", configPath);
                var output = await RunProcessAsync(configPath, _selfUpdateArgs, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
                return string.IsNullOrWhiteSpace(output) ? "yt-dlp self-update finished." : output;
            }

            var pluginDir = GetPluginDirectory()
                ?? throw new InvalidOperationException("Could not determine the plugin directory.");

            _logger.LogInformation("Downloading latest standalone yt-dlp to {Dir}", pluginDir);
            await DownloadStandaloneYtDlpAsync(pluginDir).ConfigureAwait(false);
            _lastYtDlpUpdateCheckUtc = DateTime.UtcNow;

            var binaryPath = Path.Combine(pluginDir, GetStandaloneBinaryName());
            var version = await RunProcessAsync(binaryPath, _versionArgs, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            var deno = await EnsureDenoAsync().ConfigureAwait(false);
            return deno != null
                ? $"Downloaded latest yt-dlp ({version}), using Deno at {deno}."
                : $"Downloaded latest yt-dlp ({version}). Deno could not be set up, so YouTube may refuse some downloads.";
        }
        finally
        {
            _downloadLock.Release();
        }
    }

    /// <summary>
    /// Runs a process and returns its combined output flattened to one line, killing it after the
    /// timeout. Used only for short yt-dlp maintenance commands (-U, --version).
    /// </summary>
    private static async Task<string> RunProcessAsync(string fileName, IReadOnlyList<string> args, TimeSpan timeout)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.Start();
        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // process already exited
            }

            throw new InvalidOperationException($"{Path.GetFileName(fileName)} timed out after {timeout.TotalSeconds:0} seconds.");
        }

        var stdout = await stdOutTask.ConfigureAwait(false);
        var stderr = await stdErrTask.ConfigureAwait(false);
        var lines = (stdout + "\n" + stderr).Split(_newlineChars, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var combined = string.Join(" | ", lines);
        return combined.Length > 500 ? combined[..500] + "..." : combined;
    }

    /// <summary>
    /// Splits a genre string into one cleaned entry per genre (see
    /// <see cref="TrackMetadataCleaner.CleanGenres"/>). Each entry becomes its own tag value (e.g.
    /// a separate "GENRE=" Vorbis comment), which is how players — including Jellyfin — recognize
    /// multiple genres. Never join them back into a single value.
    /// </summary>
    internal static string[] ParseGenres(string? genre) => TrackMetadataCleaner.CleanGenres(genre);


    internal static AudioConversionFormat MapToAudioConversionFormat(AudioFormat format)
    {
        return format switch
        {
            AudioFormat.Opus => AudioConversionFormat.Opus,
            AudioFormat.Mp3 => AudioConversionFormat.Mp3,
            AudioFormat.M4a => AudioConversionFormat.Aac,
            _ => AudioConversionFormat.Opus
        };
    }

    internal static bool IsValidYouTubeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        // Host alone is not enough: file://youtube.com/... parses with a matching host.
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var host = uri.Host.ToLowerInvariant();
        return host is "youtube.com" or "www.youtube.com" or "m.youtube.com"
            or "music.youtube.com" or "youtu.be";
    }

    private string? ExtractTitleFromInfoJson(string infoJsonPath)
    {
        try
        {
            var json = File.ReadAllText(infoJsonPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // The YouTube Music track name is authoritative; a raw video title is cleaned here,
            // at write time, so it is never re-cleaned downstream.
            var track = GetJsonString(root, "track");
            if (!string.IsNullOrEmpty(track))
            {
                return track;
            }

            var videoTitle = GetJsonString(root, "title");
            if (string.IsNullOrEmpty(videoTitle))
            {
                return null;
            }

            var artist = GetJsonString(root, "artist") ?? GetJsonString(root, "uploader");
            var cleaned = CleanYouTubeTitle(videoTitle, artist);
            return string.IsNullOrEmpty(cleaned) ? null : cleaned;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not extract title from {Path}", Path.GetFileName(infoJsonPath));
            return null;
        }
    }

    private void ReadTagsFromFile(string filePath, QueueItemDto dto)
    {
        try
        {
            TagLib.File? tagFile = null;
            try
            {
                tagFile = TagLib.File.Create(filePath);
            }
            catch (Exception ex) when (ex is ArgumentNullException or TagLib.CorruptFileException)
            {
                _logger.LogWarning("TagLib failed to read {File}, attempting ffmpeg remux", Path.GetFileName(filePath));
                if (TryRemuxContainer(filePath))
                {
                    tagFile = TagLib.File.Create(filePath);
                }
                else
                {
                    throw;
                }
            }

            using var _ = tagFile;

            // First performer is primary artist; rest are featured
            var performers = tagFile.Tag.Performers ?? Array.Empty<string>();
            dto.Artist = performers.Length > 0 ? performers[0] : string.Empty;
            dto.FeaturedArtist = performers.Length > 1
                ? string.Join("; ", performers.Skip(1))
                : string.Empty;

            // The tag title is either user-saved or was cleaned when written — show it verbatim.
            dto.MetadataTitle = tagFile.Tag.Title ?? string.Empty;

            // Album artist: first AlbumArtists entry, or fall back to primary artist
            var albumArtists = tagFile.Tag.AlbumArtists ?? Array.Empty<string>();
            dto.AlbumArtist = albumArtists.Length > 0 ? albumArtists[0] : string.Empty;

            dto.Album = tagFile.Tag.Album ?? string.Empty;
            dto.TrackNumber = tagFile.Tag.Track > 0 ? (int)tagFile.Tag.Track : null;
            dto.Year = tagFile.Tag.Year > 0 ? (int)tagFile.Tag.Year : null;

            // Join all genres with semicolons
            var genres = tagFile.Tag.Genres ?? Array.Empty<string>();
            dto.Genre = genres.Length > 0 ? string.Join("; ", genres) : string.Empty;

            dto.Duration = tagFile.Properties.Duration.TotalSeconds > 0
                ? tagFile.Properties.Duration.TotalSeconds
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read tags from {File}", Path.GetFileName(filePath));
        }
    }

    private void PopulateFromInfoJson(string infoJsonPath, QueueItemDto dto)
    {
        try
        {
            var json = File.ReadAllText(infoJsonPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Fill the title only when the file tags had none (a user-saved or submit-time title
            // lands in the tags and must win). Prefer the YouTube Music track name; fall back to
            // the cleaned video title.
            if (string.IsNullOrEmpty(dto.MetadataTitle))
            {
                var trackName = GetJsonString(root, "track");
                if (!string.IsNullOrEmpty(trackName))
                {
                    dto.MetadataTitle = trackName;
                }
                else
                {
                    var videoTitle = GetJsonString(root, "title");
                    var artist = GetJsonString(root, "artist") ?? dto.Artist;
                    dto.MetadataTitle = CleanYouTubeTitle(videoTitle ?? string.Empty, artist);
                }
            }

            if (string.IsNullOrEmpty(dto.Artist))
            {
                dto.Artist = GetJsonString(root, "artist") ?? GetJsonString(root, "uploader") ?? dto.Artist;
            }

            // Only fill fields from info.json when the file tags had no value.
            // This prevents overwriting user-edited metadata saved via SaveTags.
            if (string.IsNullOrEmpty(dto.Album))
            {
                dto.Album = TrackMetadataCleaner.CleanAlbum(GetJsonString(root, "album")) ?? dto.Album;
            }

            if (!dto.TrackNumber.HasValue
                && root.TryGetProperty("playlist_index", out var trackNum)
                && trackNum.ValueKind == JsonValueKind.Number)
            {
                dto.TrackNumber = trackNum.GetInt32();
            }

            // YouTube Music's release year beats the upload date, which for a re-upload or a
            // label's channel can be years after the song came out.
            if (!dto.Year.HasValue && root.TryGetProperty("release_year", out var releaseYear) && releaseYear.ValueKind == JsonValueKind.Number)
            {
                dto.Year = releaseYear.GetInt32();
            }

            if (!dto.Year.HasValue)
            {
                var uploadDate = GetJsonString(root, "release_date") ?? GetJsonString(root, "upload_date");
                if (uploadDate?.Length >= 4 && int.TryParse(uploadDate[..4], out var year))
                {
                    dto.Year = year;
                }
            }

            if (string.IsNullOrEmpty(dto.Genre))
            {
                var genres = TrackMetadataCleaner.CleanGenres(GetJsonString(root, "genre"));
                dto.Genre = genres.Length > 0 ? string.Join("; ", genres) : dto.Genre;
            }

            if (root.TryGetProperty("duration", out var duration) && duration.ValueKind == JsonValueKind.Number)
            {
                dto.Duration = duration.GetDouble();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not parse .info.json: {Path}", Path.GetFileName(infoJsonPath));
        }
    }

    /// <summary>
    /// Rewrites an audio file's container with a stream-copy remux (ffmpeg -c copy). No re-encode,
    /// so no quality loss. Used both proactively — yt-dlp's M4a output has a box header TagLib
    /// cannot parse — and reactively to recover files TagLib reports as corrupt.
    /// </summary>
    /// <returns>True if the container was remuxed successfully.</returns>
    private bool TryRemuxContainer(string filePath)
    {
        try
        {
            var ffmpegPath = _mediaEncoder.EncoderPath;
            if (string.IsNullOrEmpty(ffmpegPath) || !File.Exists(ffmpegPath))
            {
                _logger.LogWarning("Cannot remux file: ffmpeg not found");
                return false;
            }

            var tempPath = filePath + ".remux" + Path.GetExtension(filePath);
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-y -i \"{filePath}\" -c copy \"{tempPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var exited = process.WaitForExit(30000);

            if (!exited)
            {
                _logger.LogWarning("ffmpeg remux timed out for {File}, killing process", Path.GetFileName(filePath));
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* process already exited */ }

                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }

                return false;
            }

            if (process.ExitCode == 0 && File.Exists(tempPath))
            {
                File.Move(tempPath, filePath, overwrite: true);
                _logger.LogInformation("Remuxed audio container: {File}", Path.GetFileName(filePath));
                return true;
            }

            // Clean up temp file on failure
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            _logger.LogWarning("ffmpeg remux failed with exit code {Code}", process.ExitCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception during ffmpeg remux of {File}", Path.GetFileName(filePath));
            return false;
        }
    }

    /// <summary>
    /// Cleans up a YouTube video title to extract the actual track name.
    /// Strips common YouTube patterns like "(Official Video)" and "Artist - " prefix.
    /// </summary>
    internal static string CleanYouTubeTitle(string title, string? artist)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var cleaned = title;

        // Strip "Artist - " prefix (case-insensitive)
        if (!string.IsNullOrWhiteSpace(artist))
        {
            var prefix = artist + " - ";
            if (cleaned.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned[prefix.Length..];
            }

            // Also handle "Artist- " and "Artist -" variants
            prefix = artist + "- ";
            if (cleaned.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned[prefix.Length..];
            }

            prefix = artist + " -";
            if (cleaned.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned[prefix.Length..].TrimStart();
            }
        }

        // Strip common YouTube video suffixes (parenthesized and bracketed)
        string[] patterns =
        [
            "(Official Video)",
            "(Official Music Video)",
            "(Official HD Video)",
            "(Official Audio)",
            "(Official Lyric Video)",
            "(Official Visualizer)",
            "(Official Live Video)",
            "(Lyric Video)",
            "(Lyrics)",
            "(Visualizer)",
            "(Audio)",
            "(Audio Only)",
            "(Video)",
            "(HD)",
            "(HQ)",
            "[Official Video]",
            "[Official Music Video]",
            "[Official HD Video]",
            "[Official Audio]",
            "[Official Lyric Video]",
            "[Lyric Video]",
            "[Lyrics]",
            "[Audio]",
            "[HD]",
            "[HQ]"
        ];

        foreach (var pattern in patterns)
        {
            var idx = cleaned.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                cleaned = string.Concat(cleaned.AsSpan(0, idx), cleaned.AsSpan(idx + pattern.Length));
            }
        }

        cleaned = cleaned.Trim();

        // If cleaning removed everything, return original
        return string.IsNullOrWhiteSpace(cleaned) ? title.Trim() : cleaned;
    }

    /// <summary>
    /// Reads the artist and length of a download from its info.json. The artist is YouTube
    /// Music's credit when present, otherwise the uploading channel without its " - Topic" or
    /// "VEVO" suffix.
    /// </summary>
    private InfoJsonDetails ReadInfoJson(string infoJsonPath)
    {
        if (!File.Exists(infoJsonPath))
        {
            return new InfoJsonDetails(null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(infoJsonPath));
            var root = doc.RootElement;
            var artist = GetJsonString(root, "artist") ?? GetJsonString(root, "creator")
                ?? ChannelToArtist(GetJsonString(root, "channel") ?? GetJsonString(root, "uploader"));
            double? duration = root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : null;
            return new InfoJsonDetails(artist, duration);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogDebug(ex, "Could not read {Path}", Path.GetFileName(infoJsonPath));
            return new InfoJsonDetails(null, null);
        }
    }

    /// <summary>
    /// Turns a channel name into an artist name: "Lang Lang - Topic" and "LangLangVEVO" give
    /// the artist; other channels are returned as they are.
    /// </summary>
    internal static string? ChannelToArtist(string? channel)
    {
        if (string.IsNullOrWhiteSpace(channel))
        {
            return null;
        }

        var name = channel.Trim();
        if (name.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^" - Topic".Length];
        }
        else if (name.EndsWith("VEVO", StringComparison.OrdinalIgnoreCase) && name.Length > 4)
        {
            name = name[..^4];
        }

        name = name.Trim();
        return name.Length == 0 ? null : name;
    }

    private static string GetInfoJsonPath(string audioFilePath)
    {
        var dir = Path.GetDirectoryName(audioFilePath) ?? string.Empty;
        var nameWithoutExt = Path.GetFileNameWithoutExtension(audioFilePath);
        return Path.Combine(dir, nameWithoutExt + ".info.json");
    }

    private static string? GetJsonString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            var value = prop.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }

    /// <summary>
    /// Sanitizes a single path component (file or folder name) by removing invalid
    /// and problematic characters. Metadata tags are NOT affected — this is only for filenames.
    /// Names must also work over network shares (SMB) and on Windows, so characters those
    /// reject are removed even on Linux (":" becomes " - "), and long names are shortened to
    /// <see cref="MaxPathComponentLength"/> characters at a word break.
    /// </summary>
    internal static string SanitizePathComponent(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Unknown";
        }

        // Characters that are problematic across platforms (network shares, web servers, shells)
        var extraBadChars = new HashSet<char> { '#', '%', '{', '}', '`', '!', '$', '@', '~', '=', '<', '>', '|', '?', '*', '\\', '/' };

        var invalidChars = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);

        foreach (var c in name)
        {
            if (c == ':')
            {
                sb.Append(" - ");
            }
            else if (c == '"')
            {
                sb.Append('\'');
            }
            else if (invalidChars.Contains(c) || extraBadChars.Contains(c) || char.IsControl(c))
            {
                continue; // Strip the character
            }
            else
            {
                sb.Append(c);
            }
        }

        // Replace & with 'and' for readability
        var sanitized = sb.ToString().Replace("&", "and", StringComparison.Ordinal);
        sanitized = string.Join(' ', sanitized.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        if (sanitized.Length > MaxPathComponentLength)
        {
            var cut = sanitized.LastIndexOf(' ', MaxPathComponentLength);
            sanitized = sanitized[..(cut > MaxPathComponentLength / 2 ? cut : MaxPathComponentLength)];
        }

        // Remove leading/trailing dots, dashes and spaces which cause issues on some filesystems
        sanitized = sanitized.Trim('.', ' ', '-');

        return string.IsNullOrEmpty(sanitized) ? "Unknown" : sanitized;
    }

    internal static string SanitizeForLog(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return "[empty]";
        }

        var sanitized = new string(input.Where(c => !char.IsControl(c)).ToArray());
        return sanitized.Length > 200 ? string.Concat(sanitized.AsSpan(0, 200), "...") : sanitized;
    }
}

/// <summary>
/// What a download's info.json says about it.
/// </summary>
/// <param name="Artist">The artist, when known.</param>
/// <param name="Duration">The length in seconds, when known.</param>
internal sealed record InfoJsonDetails(string? Artist, double? Duration);
