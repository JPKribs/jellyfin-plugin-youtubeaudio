using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.YouTubeAudio.Models;
using Jellyfin.Plugin.YouTubeAudio.Services;
using Jellyfin.Plugin.YouTubeAudio.Services.Playlists;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeAudio.Controllers;

/// <summary>
/// API controller for YouTube Audio plugin operations.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("YouTubeAudio")]
[Produces(MediaTypeNames.Application.Json)]
public class YouTubeAudioController : ControllerBase
{
    private readonly DownloadService _downloadService;
    private readonly LibraryService _libraryService;
    private readonly PlaylistImportService _playlistImportService;
    private readonly PlaylistSyncService _playlistSyncService;
    private readonly ILogger<YouTubeAudioController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="YouTubeAudioController"/> class.
    /// </summary>
    public YouTubeAudioController(
        DownloadService downloadService,
        LibraryService libraryService,
        PlaylistImportService playlistImportService,
        PlaylistSyncService playlistSyncService,
        ILogger<YouTubeAudioController> logger)
    {
        _playlistSyncService = playlistSyncService;
        _downloadService = downloadService;
        _libraryService = libraryService;
        _playlistImportService = playlistImportService;
        _logger = logger;
    }

    // ===== Download Tab Endpoints =====

    /// <summary>
    /// Queues a YouTube URL for download.
    /// </summary>
    /// <param name="request">The request containing the YouTube URL.</param>
    /// <param name="cancellationToken">Cancellation token tied to the HTTP request.</param>
    /// <returns>List of created queue items.</returns>
    [HttpPost("Queue")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<List<QueueItemDto>>> QueueUrl([FromBody] QueueUrlRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var items = await _downloadService.QueueUrlAsync(request.Url, cancellationToken).ConfigureAwait(false);
            return Ok(items);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { Error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in QueueUrl endpoint");
            return StatusCode(500, new { Error = "An internal error occurred. Check server logs for details." });
        }
    }

    /// <summary>
    /// Lists the playlist export formats that can be imported.
    /// </summary>
    /// <returns>The supported playlist sources.</returns>
    [HttpGet("Playlists/Sources")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<List<PlaylistSourceInfo>> GetPlaylistSources()
        => Ok(_playlistImportService.GetSources());

    /// <summary>
    /// Queues every track in an uploaded playlist export. Each track's YouTube match is found
    /// when it is downloaded. For a file holding several playlists, the first call returns them
    /// for selection and the file is sent again with the chosen IDs.
    /// </summary>
    /// <param name="request">The uploaded export file.</param>
    /// <returns>The source used and the created queue items.</returns>
    [HttpPost("Queue/Playlist")]
    [RequestSizeLimit(PlaylistImportService.MaxRequestBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<PlaylistImportResult> QueuePlaylist([FromBody] PlaylistImportRequest request)
    {
        try
        {
            return Ok(_playlistImportService.Import(request));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { Error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in QueuePlaylist endpoint");
            return StatusCode(500, new { Error = "An internal error occurred. Check server logs for details." });
        }
    }

    /// <summary>
    /// Lists the Jellyfin playlists being filled by playlist imports, with progress.
    /// </summary>
    /// <returns>The playlist targets.</returns>
    [HttpGet("Playlists")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<List<PlaylistTargetDto>> GetPlaylistTargets()
        => Ok(_playlistSyncService.GetTargets());

    /// <summary>
    /// Adds every pending playlist song that is now in the library, without waiting for a scan.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token tied to the HTTP request.</param>
    /// <returns>The number of songs added.</returns>
    [HttpPost("Playlists/Sync")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> SyncPlaylists(CancellationToken cancellationToken)
    {
        try
        {
            var added = await _playlistSyncService.SyncAsync(cancellationToken).ConfigureAwait(false);
            return Ok(new { Added = added });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error in SyncPlaylists endpoint");
            return StatusCode(500, new { Error = "An internal error occurred. Check server logs for details." });
        }
    }

    /// <summary>
    /// Stops filling a playlist. The Jellyfin playlist and its songs are kept.
    /// </summary>
    /// <param name="id">The playlist target ID.</param>
    /// <returns>Success status.</returns>
    [HttpDelete("Playlists/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult DeletePlaylistTarget(string id)
    {
        _downloadService.DeletePlaylistTarget(id);
        return Ok(new { Success = true });
    }

    /// <summary>
    /// Points a queue item at a different YouTube link (to fix a wrong or failed match) and
    /// starts downloading it. Its metadata and playlist entries are kept.
    /// </summary>
    /// <param name="id">The queue item ID.</param>
    /// <param name="request">The new YouTube link.</param>
    /// <returns>The updated item.</returns>
    [HttpPost("Queue/{id}/Url")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<QueueItemDto> ChangeQueueUrl(string id, [FromBody] QueueUrlRequest request)
    {
        try
        {
            var item = _downloadService.ChangeUrl(id, request?.Url ?? string.Empty);
            _downloadService.StartProcessing([id]);
            return Ok(item);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { Error = ex.Message });
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(new { Error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { Error = ex.Message });
        }
    }

    /// <summary>
    /// Gets all queue items.
    /// </summary>
    /// <returns>List of queue items with total count.</returns>
    [HttpGet("Queue")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<QueueListResponse> GetQueue()
    {
        try
        {
            var items = _downloadService.GetQueueItems();
            return Ok(new QueueListResponse { Items = items, TotalCount = items.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GetQueue endpoint");
            return StatusCode(500, new { Error = "An internal error occurred. Check server logs for details." });
        }
    }

    /// <summary>
    /// Removes a queue item and its cached file.
    /// </summary>
    /// <param name="id">The queue item ID.</param>
    /// <returns>Success status.</returns>
    [HttpDelete("Queue/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult DeleteQueueItem(string id)
    {
        try
        {
            _downloadService.DeleteCachedFile(id);
            return Ok(new { Success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in DeleteQueueItem endpoint");
            return StatusCode(500, new { Error = "An internal error occurred. Check server logs for details." });
        }
    }

    /// <summary>
    /// Deletes multiple queue items at once.
    /// </summary>
    /// <param name="request">The request containing IDs to delete.</param>
    /// <returns>Deletion result with counts.</returns>
    [HttpPost("Queue/BatchDelete")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult BatchDeleteQueue([FromBody] ImportRequest request)
    {
        try
        {
            var deleted = 0;
            var errors = 0;
            foreach (var id in request.Ids)
            {
                try
                {
                    _downloadService.DeleteCachedFile(id);
                    deleted++;
                }
                catch (Exception ex)
                {
                    errors++;
                    _logger.LogWarning(ex, "Error deleting queue item {Id}", id);
                }
            }

            return Ok(new { Deleted = deleted, Errors = errors });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in BatchDeleteQueue endpoint");
            return StatusCode(500, new { Error = "An internal error occurred. Check server logs for details." });
        }
    }

    /// <summary>
    /// Starts downloading the selected queue items in the background and returns immediately.
    /// Progress is observable by polling the queue; run-level failures mark the items as Error.
    /// </summary>
    /// <param name="request">Request containing IDs to process.</param>
    /// <returns>Success status.</returns>
    [HttpPost("Queue/Process")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult ProcessQueue([FromBody] ImportRequest request)
    {
        try
        {
            _downloadService.StartProcessing(request.Ids);
            return Ok(new { Success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in ProcessQueue endpoint");
            return StatusCode(500, new { Error = "An internal error occurred. Check server logs for details." });
        }
    }

    // ===== Import Tab Endpoints =====

    /// <summary>
    /// Gets all downloaded items ready for import with metadata.
    /// </summary>
    /// <returns>List of downloaded items with total count.</returns>
    [HttpGet("Downloads")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<QueueListResponse> GetDownloads()
    {
        try
        {
            var items = _downloadService.GetDownloadedItems();
            return Ok(new QueueListResponse { Items = items, TotalCount = items.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GetDownloads endpoint");
            return StatusCode(500, new { Error = "An internal error occurred. Check server logs for details." });
        }
    }

    /// <summary>
    /// Streams a downloaded file so it can be previewed before it is imported. Supports range
    /// requests, so the player can seek.
    /// </summary>
    /// <param name="id">The queue item ID.</param>
    /// <returns>The audio file, or 404 when the item is not downloaded.</returns>
    [HttpGet("Downloads/{id}/Audio")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetDownloadAudio(string id)
    {
        var file = _downloadService.GetPreviewFile(id);
        if (file == null)
        {
            return NotFound();
        }

        Response.Headers["Cache-Control"] = "no-store";
        return PhysicalFile(file.Value.Path, file.Value.ContentType, enableRangeProcessing: true);
    }

    /// <summary>
    /// Gets the cover art embedded in a downloaded file.
    /// </summary>
    /// <param name="id">The queue item ID.</param>
    /// <returns>The image, or 404 when the file has none.</returns>
    [HttpGet("Downloads/{id}/Cover")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetDownloadCover(string id)
    {
        var cover = _downloadService.GetCoverArt(id);
        if (cover == null)
        {
            return NotFound();
        }

        Response.Headers["Cache-Control"] = "no-store";
        return File(cover.Value.Data, cover.Value.MimeType);
    }

    /// <summary>
    /// Embeds an uploaded cover image (a square JPEG or PNG) in a downloaded file.
    /// </summary>
    /// <param name="id">The queue item ID.</param>
    /// <param name="request">The base64-encoded image.</param>
    /// <returns>Success status.</returns>
    [HttpPost("Downloads/{id}/Cover")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult SetDownloadCover(string id, [FromBody] CoverArtRequest request)
    {
        byte[] data;
        try
        {
            var base64 = request?.Data ?? string.Empty;
            var comma = base64.IndexOf(',', StringComparison.Ordinal);
            data = Convert.FromBase64String(comma >= 0 ? base64[(comma + 1)..] : base64);
        }
        catch (FormatException)
        {
            return BadRequest(new { Error = "The image could not be read." });
        }

        try
        {
            _downloadService.SetCoverArt(id, data);
            return Ok();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { Error = ex.Message });
        }
        catch (FileNotFoundException)
        {
            return NotFound(new { Error = "Downloaded file not found." });
        }
    }

    /// <summary>
    /// Saves metadata tags to a downloaded file.
    /// </summary>
    /// <param name="request">The tag update request.</param>
    /// <returns>Success status.</returns>
    [HttpPost("Tags")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult SaveTags([FromBody] TagUpdateRequest request)
    {
        try
        {
            _downloadService.SaveTags(request);
            return Ok(new { Success = true });
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(new { Error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { Error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SaveTags endpoint");
            return StatusCode(500, new { Error = "An internal error occurred. Check server logs for details." });
        }
    }

    /// <summary>
    /// Imports downloaded files to the Jellyfin music library.
    /// </summary>
    /// <param name="request">The import request with file IDs.</param>
    /// <returns>Import result.</returns>
    [HttpPost("Import")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ImportResult>> Import([FromBody] ImportRequest request)
    {
        try
        {
            var result = await _downloadService.ImportToLibraryAsync(request.Ids, HttpContext.RequestAborted).ConfigureAwait(false);

            // Kick a scan so the imported files show up without waiting for the schedule.
            if (result.ImportedIds.Count > 0)
            {
                _libraryService.QueueLibraryScan();

                // The scan's ItemAdded events trigger the playlist sync; this is the fallback.
                _playlistSyncService.RequestSync(TimeSpan.FromMinutes(2));
            }

            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { Error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in Import endpoint");
            return StatusCode(500, new { Error = "An internal error occurred. Check server logs for details." });
        }
    }

    /// <summary>
    /// Deletes a downloaded file from the cache.
    /// </summary>
    /// <param name="id">The queue item ID.</param>
    /// <returns>Success status.</returns>
    [HttpDelete("Downloads/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult DeleteDownload(string id)
    {
        try
        {
            _downloadService.DeleteCachedFile(id);
            return Ok(new { Success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in DeleteDownload endpoint");
            return StatusCode(500, new { Error = "An internal error occurred. Check server logs for details." });
        }
    }

    /// <summary>
    /// Deletes multiple downloaded files at once.
    /// </summary>
    /// <param name="request">The request containing IDs to delete.</param>
    /// <returns>Deletion result with counts.</returns>
    [HttpPost("Downloads/BatchDelete")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult BatchDeleteDownloads([FromBody] ImportRequest request)
    {
        try
        {
            var deleted = 0;
            var errors = 0;
            foreach (var id in request.Ids)
            {
                try
                {
                    _downloadService.DeleteCachedFile(id);
                    deleted++;
                }
                catch (Exception ex)
                {
                    errors++;
                    _logger.LogWarning(ex, "Error deleting download {Id}", id);
                }
            }

            return Ok(new { Deleted = deleted, Errors = errors });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in BatchDeleteDownloads endpoint");
            return StatusCode(500, new { Error = "An internal error occurred. Check server logs for details." });
        }
    }

    // ===== Library Integration Endpoints =====

    /// <summary>
    /// Searches for artists in the Jellyfin library.
    /// </summary>
    /// <param name="query">Search query.</param>
    /// <returns>List of matching artist names.</returns>
    [HttpGet("Artists")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<List<string>> SearchArtists([FromQuery] string query)
    {
        try
        {
            var artists = _libraryService.SearchArtists(query);
            return Ok(artists);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SearchArtists endpoint");
            return StatusCode(500, new { Error = "An internal error occurred. Check server logs for details." });
        }
    }

    /// <summary>
    /// Searches for albums in the Jellyfin library.
    /// </summary>
    /// <param name="query">Search query.</param>
    /// <param name="artist">Optional artist name to filter albums by.</param>
    /// <returns>List of matching album names.</returns>
    [HttpGet("Albums")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<List<string>> SearchAlbums([FromQuery] string query, [FromQuery] string? artist = null)
    {
        try
        {
            var albums = _libraryService.SearchAlbums(query, artist);
            return Ok(albums);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SearchAlbums endpoint");
            return StatusCode(500, new { Error = "An internal error occurred. Check server logs for details." });
        }
    }

    /// <summary>
    /// Gets all music libraries configured in Jellyfin.
    /// </summary>
    /// <returns>List of music libraries.</returns>
    [HttpGet("Libraries")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<List<LibraryInfo>> GetLibraries()
    {
        try
        {
            var libraries = _libraryService.GetMusicLibraries();
            return Ok(libraries);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GetLibraries endpoint");
            return StatusCode(500, new { Error = "An internal error occurred. Check server logs for details." });
        }
    }

    // ===== Settings Endpoints =====

    /// <summary>
    /// Updates yt-dlp: re-downloads the standalone binary, or runs the configured binary's
    /// self-updater when a custom path is set.
    /// </summary>
    /// <returns>Success status with a result message.</returns>
    [HttpPost("UpdateYtDlp")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> UpdateYtDlp()
    {
        try
        {
            var message = await _downloadService.UpdateYtDlpAsync().ConfigureAwait(false);
            return Ok(new { Success = true, Message = message });
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(500, new { Error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in UpdateYtDlp endpoint");
            return StatusCode(500, new { Error = "An internal error occurred. Check server logs for details." });
        }
    }

    /// <summary>
    /// Resets the download queue (database only).
    /// </summary>
    /// <returns>Success status.</returns>
    [HttpPost("ResetQueue")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult ResetQueue()
    {
        try
        {
            _downloadService.ResetQueue();
            return Ok(new { Success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in ResetQueue endpoint");
            return StatusCode(500, new { Error = "An internal error occurred. Check server logs for details." });
        }
    }

    /// <summary>
    /// Resets the cache: deletes all cached files and resets the queue.
    /// </summary>
    /// <returns>Success status.</returns>
    [HttpPost("ResetCache")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult ResetCache()
    {
        try
        {
            _downloadService.ResetCache();
            return Ok(new { Success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in ResetCache endpoint");
            return StatusCode(500, new { Error = "An internal error occurred. Check server logs for details." });
        }
    }
}
