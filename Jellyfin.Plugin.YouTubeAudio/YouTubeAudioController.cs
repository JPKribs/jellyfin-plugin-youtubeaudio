using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.YouTubeAudio.Models;
using Jellyfin.Plugin.YouTubeAudio.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeAudio;

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
    private readonly ILogger<YouTubeAudioController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="YouTubeAudioController"/> class.
    /// </summary>
    public YouTubeAudioController(
        DownloadService downloadService,
        LibraryService libraryService,
        ILogger<YouTubeAudioController> logger)
    {
        _downloadService = downloadService;
        _libraryService = libraryService;
        _logger = logger;
    }

    // ===== Download Tab Endpoints =====

    /// <summary>
    /// Queues a YouTube URL for download.
    /// </summary>
    /// <param name="request">The request containing the YouTube URL.</param>
    /// <returns>List of created queue items.</returns>
    [HttpPost("Queue")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<List<QueueItemDto>>> QueueUrl([FromBody] QueueUrlRequest request)
    {
        try
        {
            var items = await _downloadService.QueueUrlAsync(request.Url).ConfigureAwait(false);
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
    [ProducesResponseType(StatusCodes.Status404NotFound)]
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
    /// Processes the download queue.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success status.</returns>
    [HttpPost("Queue/Process")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> ProcessQueue(CancellationToken cancellationToken)
    {
        try
        {
            await _downloadService.ProcessQueueAsync(cancellationToken).ConfigureAwait(false);
            return Ok(new { Success = true });
        }
        catch (OperationCanceledException)
        {
            return Ok(new { Success = true, Message = "Processing was cancelled." });
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
    public ActionResult<ImportResult> Import([FromBody] ImportRequest request)
    {
        try
        {
            var result = _downloadService.ImportToLibrary(request.Ids);
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
