using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.YouTubeAudio.Models;
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
    private readonly ILogger<DownloadService> _logger;
    private readonly QueueDatabaseProvider _dbProvider;
    private readonly IMediaEncoder _mediaEncoder;
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "Disposed in Dispose() method")]
    private readonly SemaphoreSlim _downloadLock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="DownloadService"/> class.
    /// </summary>
    public DownloadService(
        ILogger<DownloadService> logger,
        QueueDatabaseProvider dbProvider,
        IMediaEncoder mediaEncoder)
    {
        _logger = logger;
        _dbProvider = dbProvider;
        _mediaEncoder = mediaEncoder;
    }

    private static PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? throw new InvalidOperationException("Plugin is not initialized.");

    // ===== Queue Operations =====

    /// <summary>
    /// Queues a YouTube URL for download. If playlist, queues each video individually.
    /// </summary>
    public async Task<List<QueueItemDto>> QueueUrlAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("URL is required.");
        }

        if (!IsValidYouTubeUrl(url))
        {
            throw new ArgumentException("URL must be a valid YouTube URL.");
        }

        var ext = GetFileExtension(Config.AudioFormat);
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
                var fetchResult = await ytdl.RunVideoDataFetch(url).ConfigureAwait(false);

                if (fetchResult.Success && fetchResult.Data?.Entries != null)
                {
                    foreach (var entry in fetchResult.Data.Entries)
                    {
                        var videoUrl = entry.Url ?? entry.WebpageUrl ?? $"https://www.youtube.com/watch?v={entry.ID}";
                        var item = new QueueItem
                        {
                            Id = Guid.NewGuid().ToString("N"),
                            Url = videoUrl,
                            Title = entry.Title,
                            FileName = Guid.NewGuid().ToString("N") + ext,
                            Status = QueueStatus.Queued,
                            CreatedAt = now,
                            UpdatedAt = now
                        };
                        db.AddItem(item);
                        createdItems.Add(item);
                    }

                    _logger.LogInformation("Queued {Count} items from playlist {Url}", createdItems.Count, SanitizeForLog(url));
                }
                else
                {
                    // Fallback: treat as single video
                    var item = CreateSingleQueueItem(url, ext, now);
                    db.AddItem(item);
                    createdItems.Add(item);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch playlist metadata, queuing as single URL");
                var item = CreateSingleQueueItem(url, ext, now);
                db.AddItem(item);
                createdItems.Add(item);
            }
        }
        else
        {
            var item = CreateSingleQueueItem(url, ext, now);
            db.AddItem(item);
            createdItems.Add(item);
        }

        return createdItems.Select(MapToDto).ToList();
    }

    /// <summary>
    /// Processes queued items by downloading them.
    /// When ids is provided, only those items are processed. Otherwise all queued items are processed.
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
            // Auto-download yt-dlp if not available
            await EnsureYtDlpAsync().ConfigureAwait(false);

            var cacheDir = _dbProvider.GetAudioCacheDirectory();
            var ytdl = CreateYoutubeDL();
            ytdl.OutputFolder = cacheDir;

            foreach (var item in queuedItems)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                db.UpdateStatus(item.Id, QueueStatus.Downloading);

                try
                {
                    var outputPath = Path.Combine(cacheDir, item.FileName);
                    var nameWithoutExt = Path.GetFileNameWithoutExtension(item.FileName);

                    var options = new OptionSet
                    {
                        WriteInfoJson = true,
                        EmbedThumbnail = false,
                        Output = Path.Combine(cacheDir, nameWithoutExt + ".%(ext)s")
                    };

                    var format = MapToAudioConversionFormat(Config.AudioFormat);
                    var runResult = await ytdl.RunAudioDownload(
                        item.Url,
                        format,
                        ct: cancellationToken,
                        overrideOptions: options).ConfigureAwait(false);

                    if (runResult.Success)
                    {
                        // Try to extract title from info.json
                        var infoJsonPath = Path.Combine(cacheDir, nameWithoutExt + ".info.json");
                        if (File.Exists(infoJsonPath))
                        {
                            var title = ExtractTitleFromInfoJson(infoJsonPath);
                            if (!string.IsNullOrEmpty(title))
                            {
                                db.UpdateTitle(item.Id, title);
                            }
                        }

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
    /// Gets all queue items as DTOs.
    /// </summary>
    public List<QueueItemDto> GetQueueItems()
    {
        return _dbProvider.Database.GetAllItems().Select(MapToDto).ToList();
    }

    /// <summary>
    /// Gets downloaded items with metadata populated from tags and info.json.
    /// </summary>
    public List<QueueItemDto> GetDownloadedItems()
    {
        var db = _dbProvider.Database;
        var items = db.GetDownloadedItems();
        var cacheDir = _dbProvider.GetAudioCacheDirectory();

        return items.Select(item =>
        {
            var dto = MapToDto(item);
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

            return dto;
        }).ToList();
    }

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
            _logger.LogWarning("TagLib failed to read {File}, attempting ffmpeg repair", item.FileName);
            if (TryRepairWithFfmpeg(filePath))
            {
                tagFile = TagLib.File.Create(filePath);
            }
            else
            {
                throw new InvalidOperationException($"File is corrupt and repair failed: {item.FileName}", ex);
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
        tagFile.Tag.Genres = string.IsNullOrWhiteSpace(request.Genre)
            ? Array.Empty<string>()
            : request.Genre.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Strip any embedded artwork — text metadata only
        tagFile.Tag.Pictures = Array.Empty<TagLib.IPicture>();

        tagFile.Save();

        _logger.LogInformation("Tags saved for queue item {Id} ({FileName})", request.Id, item.FileName);
    }

    // ===== Import Operations =====

    /// <summary>
    /// Imports downloaded files to the Jellyfin music library.
    /// </summary>
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "File paths use GUID-based names from database lookup; destination paths are sanitized.")]
    public ImportResult ImportToLibrary(List<string> ids)
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
                // Some Opus files have corrupt Vorbis Comment headers; repair with ffmpeg if needed.
                TagLib.File? tagFile = null;
                try
                {
                    tagFile = TagLib.File.Create(sourcePath);
                }
                catch (Exception ex) when (ex is ArgumentNullException or TagLib.CorruptFileException)
                {
                    _logger.LogWarning("TagLib failed to read {File}, attempting ffmpeg repair: {Msg}", item.FileName, ex.Message);
                    if (TryRepairWithFfmpeg(sourcePath))
                    {
                        tagFile = TagLib.File.Create(sourcePath);
                    }
                    else
                    {
                        result.Errors.Add($"Cannot read tags for {item.Title ?? item.Id}: file is corrupt and repair failed.");
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
                    // Clean the title: strip YouTube junk like "(Official Video)" etc.
                    var rawTitle = tagFile.Tag.Title ?? Path.GetFileNameWithoutExtension(item.FileName);
                    var artistName = tagFile.Tag.FirstAlbumArtist ?? tagFile.Tag.FirstPerformer;
                    var cleanedTitle = CleanYouTubeTitle(rawTitle, artistName);

                    // Write the cleaned title back to the file metadata
                    tagFile.Tag.Title = cleanedTitle;
                    tagFile.Save();

                    var artist = SanitizePathComponent(tagFile.Tag.FirstAlbumArtist ?? tagFile.Tag.FirstPerformer ?? "Unknown Artist");
                    var album = SanitizePathComponent(tagFile.Tag.Album ?? "Unknown Album");
                    var year = tagFile.Tag.Year > 0 ? tagFile.Tag.Year.ToString(CultureInfo.InvariantCulture) : string.Empty;
                    title = SanitizePathComponent(cleanedTitle);
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

                // Clean up info.json from cache
                var infoJsonPath = GetInfoJsonPath(sourcePath);
                if (File.Exists(infoJsonPath))
                {
                    File.Delete(infoJsonPath);
                }

                db.UpdateStatus(id, QueueStatus.Imported);
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
            var cacheDir = _dbProvider.GetAudioCacheDirectory();
            var filePath = Path.Combine(cacheDir, item.FileName);

            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            var infoJsonPath = GetInfoJsonPath(filePath);
            if (File.Exists(infoJsonPath))
            {
                File.Delete(infoJsonPath);
            }

            db.DeleteItem(id);
            _logger.LogInformation("Deleted cached file for queue item {Id}", id);
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

    private static QueueItem CreateSingleQueueItem(string url, string ext, string now)
    {
        return new QueueItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Url = url,
            FileName = Guid.NewGuid().ToString("N") + ext,
            Status = QueueStatus.Queued,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static QueueItemDto MapToDto(QueueItem item)
    {
        return new QueueItemDto
        {
            Id = item.Id,
            Url = item.Url,
            Title = item.Title,
            FileName = item.FileName,
            Status = item.Status.ToString(),
            StatusCode = (int)item.Status,
            ErrorMessage = item.ErrorMessage,
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt
        };
    }

    /// <summary>
    /// Ensures yt-dlp is available, downloading it to the plugin directory if needed.
    /// Must be called before CreateYoutubeDL(). Safe to call under _downloadLock.
    /// </summary>
    private async Task EnsureYtDlpAsync()
    {
        if (FindYtDlp() != null)
        {
            return;
        }

        var pluginDir = GetPluginDirectory();
        if (string.IsNullOrEmpty(pluginDir))
        {
            throw new InvalidOperationException(
                "Could not determine the plugin directory. Set the yt-dlp path manually in Settings.");
        }

        _logger.LogInformation("yt-dlp not found — downloading to plugin directory: {Dir}", pluginDir);

        try
        {
            await Utils.DownloadYtDlp(pluginDir).ConfigureAwait(false);

            var expectedPath = Path.Combine(pluginDir, Utils.YtDlpBinaryName);
            if (!File.Exists(expectedPath))
            {
                throw new FileNotFoundException($"Download completed but binary not found at {expectedPath}");
            }

            _logger.LogInformation("Successfully downloaded yt-dlp to: {Path}", expectedPath);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Failed to download yt-dlp automatically: {ex.Message}. " +
                "Check your internet connection, or install yt-dlp manually and set the path in Settings.",
                ex);
        }
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

    private string? FindYtDlp()
    {
        // 1. Check explicit config override first
        var configPath = Plugin.Instance?.Configuration.YtDlpPath;
        if (!string.IsNullOrWhiteSpace(configPath) && File.Exists(configPath))
        {
            return configPath;
        }

        // 2. Check plugin directory for auto-downloaded binary
        var pluginDir = GetPluginDirectory();
        if (!string.IsNullOrEmpty(pluginDir))
        {
            var bundledPath = Path.Combine(pluginDir, Utils.YtDlpBinaryName);
            if (File.Exists(bundledPath))
            {
                _logger.LogInformation("Found bundled yt-dlp at: {Path}", bundledPath);
                return bundledPath;
            }
        }

        // 3. Check system PATH via YoutubeDLSharp utility
        var pathResult = Utils.GetFullPath("yt-dlp");
        if (pathResult != null)
        {
            return pathResult;
        }

        // 4. Check common install locations (macOS GUI apps have limited PATH)
        string[] commonPaths =
        [
            "/opt/homebrew/bin/yt-dlp",     // macOS ARM Homebrew
            "/usr/local/bin/yt-dlp",        // macOS Intel Homebrew / manual installs
            "/usr/bin/yt-dlp",              // Linux package managers
            "/snap/bin/yt-dlp"              // Snap installs
        ];

        foreach (var path in commonPaths)
        {
            if (File.Exists(path))
            {
                _logger.LogInformation("Found yt-dlp at common location: {Path}", path);
                return path;
            }
        }

        return null;
    }

    private static string? GetPluginDirectory()
    {
        var assemblyLocation = typeof(Plugin).Assembly.Location;
        return string.IsNullOrEmpty(assemblyLocation) ? null : Path.GetDirectoryName(assemblyLocation);
    }

    private static string GetFileExtension(AudioFormat format)
    {
        return format switch
        {
            AudioFormat.Opus => ".opus",
            AudioFormat.Mp3 => ".mp3",
            AudioFormat.M4a => ".m4a",
            _ => ".opus"
        };
    }

    private static AudioConversionFormat MapToAudioConversionFormat(AudioFormat format)
    {
        return format switch
        {
            AudioFormat.Opus => AudioConversionFormat.Opus,
            AudioFormat.Mp3 => AudioConversionFormat.Mp3,
            AudioFormat.M4a => AudioConversionFormat.Aac,
            _ => AudioConversionFormat.Opus
        };
    }

    private static bool IsValidYouTubeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
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
            return GetJsonString(root, "track") ?? GetJsonString(root, "title");
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
                _logger.LogWarning("TagLib failed to read {File}, attempting ffmpeg repair", Path.GetFileName(filePath));
                if (TryRepairWithFfmpeg(filePath))
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

            // Clean up title: strip YouTube video patterns and artist prefix
            dto.MetadataTitle = CleanYouTubeTitle(tagFile.Tag.Title ?? string.Empty, dto.Artist);

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

            // Prefer track name (actual track name from YouTube Music metadata)
            var trackName = GetJsonString(root, "track");
            if (!string.IsNullOrEmpty(trackName))
            {
                dto.MetadataTitle = trackName;
            }
            else if (string.IsNullOrEmpty(dto.MetadataTitle))
            {
                // Fall back to video title only if we have nothing
                var videoTitle = GetJsonString(root, "title");
                var artist = GetJsonString(root, "artist") ?? dto.Artist;
                dto.MetadataTitle = CleanYouTubeTitle(videoTitle ?? string.Empty, artist);
            }

            if (string.IsNullOrEmpty(dto.Artist))
            {
                dto.Artist = GetJsonString(root, "artist") ?? GetJsonString(root, "uploader") ?? dto.Artist;
            }

            // Only fill fields from info.json when the file tags had no value.
            // This prevents overwriting user-edited metadata saved via SaveTags.
            if (string.IsNullOrEmpty(dto.Album))
            {
                dto.Album = GetJsonString(root, "album") ?? dto.Album;
            }

            if (!dto.TrackNumber.HasValue
                && root.TryGetProperty("playlist_index", out var trackNum)
                && trackNum.ValueKind == JsonValueKind.Number)
            {
                dto.TrackNumber = trackNum.GetInt32();
            }

            if (!dto.Year.HasValue)
            {
                var uploadDate = GetJsonString(root, "upload_date");
                if (uploadDate?.Length >= 4 && int.TryParse(uploadDate[..4], out var year))
                {
                    dto.Year = year;
                }
            }

            if (string.IsNullOrEmpty(dto.Genre))
            {
                dto.Genre = GetJsonString(root, "genre") ?? dto.Genre;
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
    /// Attempts to repair a corrupt audio file by re-muxing it with ffmpeg.
    /// This fixes corrupt Vorbis Comment headers that TagLib cannot parse.
    /// </summary>
    /// <returns>True if the file was repaired successfully.</returns>
    private bool TryRepairWithFfmpeg(string filePath)
    {
        try
        {
            var ffmpegPath = _mediaEncoder.EncoderPath;
            if (string.IsNullOrEmpty(ffmpegPath) || !File.Exists(ffmpegPath))
            {
                _logger.LogWarning("Cannot repair file: ffmpeg not found");
                return false;
            }

            var tempPath = filePath + ".repair" + Path.GetExtension(filePath);
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
                _logger.LogWarning("ffmpeg repair timed out for {File}, killing process", Path.GetFileName(filePath));
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
                _logger.LogInformation("Repaired corrupt audio file: {File}", Path.GetFileName(filePath));
                return true;
            }

            // Clean up temp file on failure
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            _logger.LogWarning("ffmpeg repair failed with exit code {Code}", process.ExitCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception during ffmpeg repair of {File}", Path.GetFileName(filePath));
            return false;
        }
    }

    /// <summary>
    /// Cleans up a YouTube video title to extract the actual track name.
    /// Strips common YouTube patterns like "(Official Video)" and "Artist - " prefix.
    /// </summary>
    private static string CleanYouTubeTitle(string title, string? artist)
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
    /// </summary>
    private static string SanitizePathComponent(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Unknown";
        }

        // Characters that are problematic across platforms (network shares, web servers, shells)
        var extraBadChars = new HashSet<char> { '#', '%', '{', '}', '`', '!', '$', '@', '~', '=' };

        var invalidChars = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);

        foreach (var c in name)
        {
            if (invalidChars.Contains(c) || extraBadChars.Contains(c))
            {
                continue; // Strip the character
            }

            sb.Append(c);
        }

        // Replace & with 'and' for readability
        var sanitized = sb.ToString().Replace("&", "and", StringComparison.Ordinal).Trim();

        // Remove leading/trailing dots and spaces which cause issues on some filesystems
        sanitized = sanitized.Trim('.', ' ');

        return string.IsNullOrEmpty(sanitized) ? "Unknown" : sanitized;
    }

    private static string SanitizeForLog(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return "[empty]";
        }

        var sanitized = new string(input.Where(c => !char.IsControl(c)).ToArray());
        return sanitized.Length > 200 ? string.Concat(sanitized.AsSpan(0, 200), "...") : sanitized;
    }
}
