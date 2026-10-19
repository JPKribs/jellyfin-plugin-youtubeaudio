using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.YouTubeAudio.Models;
using Jellyfin.Plugin.YouTubeAudio.Services;
using Jellyfin.Plugin.YouTubeAudio.Services.Playlists;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeAudio.Controllers;

/// <summary>
/// Public-facing surface that lets approved non-admin users submit download links.
/// The page is served anonymously and gates itself client-side against the signed-in user;
/// the JSON endpoints enforce approval server-side. Import into the library remains admin-only.
/// </summary>
[ApiController]
[Route("YouTube")]
public class YouTubePublicController : ControllerBase
{
    private const string UserIdClaim = "Jellyfin-UserId";

    private static string? _pageHtml;
    private static string? _queuePageHtml;

    private readonly DownloadService _downloadService;
    private readonly LibraryService _libraryService;
    private readonly PlaylistImportService _playlistImportService;
    private readonly IUserManager _userManager;
    private readonly IServerApplicationPaths _paths;
    private readonly ILogger<YouTubePublicController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="YouTubePublicController"/> class.
    /// </summary>
    /// <param name="downloadService">The download service.</param>
    /// <param name="libraryService">The library service, used for artist and album lookups.</param>
    /// <param name="playlistImportService">The playlist import service, used for source names on the queue page.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="paths">The server application paths, used to locate the web client favicon.</param>
    /// <param name="logger">The logger.</param>
    public YouTubePublicController(
        DownloadService downloadService,
        LibraryService libraryService,
        PlaylistImportService playlistImportService,
        IUserManager userManager,
        IServerApplicationPaths paths,
        ILogger<YouTubePublicController> logger)
    {
        _downloadService = downloadService;
        _libraryService = libraryService;
        _playlistImportService = playlistImportService;
        _userManager = userManager;
        _paths = paths;
        _logger = logger;
    }

    /// <summary>Serves the public submit page (static HTML; gates itself client-side against the signed-in user).</summary>
    /// <returns>The HTML page.</returns>
    [HttpGet("Download")]
    [AllowAnonymous]
    [Produces("text/html")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ContentResult Page()
    {
        PublicPage.ApplyHardeningHeaders(Response);
        var html = StatusPage.Render(
            "Loading…",
            string.Empty,
            title: "Download",
            buttonText: null,
            showSpinner: true,
            content: GetContentFragment());
        return Content(html, "text/html");
    }

    /// <summary>Serves the public, read-only queue page (static HTML; requires a signed-in user client-side).</summary>
    /// <returns>The HTML page.</returns>
    [HttpGet("Queue")]
    [AllowAnonymous]
    [Produces("text/html")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ContentResult QueuePage()
    {
        PublicPage.ApplyHardeningHeaders(Response);
        var html = StatusPage.Render(
            "Loading…",
            string.Empty,
            title: "Queue",
            buttonText: null,
            showSpinner: true,
            content: _queuePageHtml ??= ReadResource("youtubeaudio_queue_public.html", "Queue page unavailable."));
        return Content(html, "text/html");
    }

    /// <summary>
    /// Lists the download queue, read-only, for any signed-in user. Titles, artists, albums and
    /// status only; no URLs, file names or error details.
    /// </summary>
    /// <returns>The queue, oldest first.</returns>
    [HttpGet("Queue/Items")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<List<PublicQueueItem>> QueueItems()
    {
        PublicPage.ApplyHardeningHeaders(Response);
        var sourceNames = _playlistImportService.GetSources().ToDictionary(s => s.Id, s => s.Name, StringComparer.Ordinal);
        return Ok(_downloadService.GetPublicQueue(sourceNames));
    }

    /// <summary>Serves the web client's favicon so the page can reuse the server's real icon at a stable path.</summary>
    /// <returns>The favicon bytes, or 404 when none could be located.</returns>
    [HttpGet("favicon.ico")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Favicon() => PublicPage.Favicon(_paths, Response);

    /// <summary>Reports whether the signed-in user may submit downloads.</summary>
    /// <returns>An object with an Approved flag.</returns>
    [HttpGet("Download/Info")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult Info()
    {
        PublicPage.ApplyHardeningHeaders(Response);
        return Ok(new { Approved = IsAllowed() });
    }

    /// <summary>Queues a submitted URL with metadata and starts the download in the background.</summary>
    /// <param name="request">The submitted URL and metadata.</param>
    /// <param name="cancellationToken">Cancellation token tied to the HTTP request.</param>
    /// <returns>The number of queued items, or an error.</returns>
    [HttpPost("Download/Submit")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> Submit([FromBody] SubmitRequest request, CancellationToken cancellationToken)
    {
        PublicPage.ApplyHardeningHeaders(Response);

        var access = CheckAccess();
        if (!access.Allowed)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Artist)
            || string.IsNullOrWhiteSpace(request.Album)
            || !request.Year.HasValue)
        {
            return BadRequest(new { Error = "Artist, album, and year are required." });
        }

        try
        {
            // Approved users may queue only so much from one playlist link; admins are not limited.
            var maxPlaylistItems = access.IsAdministrator ? 0 : Math.Max(0, Plugin.Instance?.Configuration.MaxPublicPlaylistItems ?? 0);
            var items = await _downloadService.QueueUrlAsync(
                request.Url,
                request.Artist,
                request.Album,
                request.Year,
                request.Title,
                maxPlaylistItems,
                cancellationToken).ConfigureAwait(false);

            var ids = items.Select(i => i.Id).ToList();
            _downloadService.StartProcessing(ids);

            return Ok(new { Queued = ids.Count });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { Error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in approved-user submit");
            return StatusCode(500, new { Error = "An internal error occurred. Check server logs for details." });
        }
    }

    /// <summary>Searches existing library artists, for the submit form's artist lookup.</summary>
    /// <param name="query">The search query.</param>
    /// <returns>Matching artist names.</returns>
    [HttpGet("Download/Artists")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<List<string>> Artists([FromQuery] string query)
    {
        PublicPage.ApplyHardeningHeaders(Response);
        if (!IsAllowed())
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        return Ok(_libraryService.SearchArtists(query ?? string.Empty));
    }

    /// <summary>Searches existing library albums, optionally filtered by artist, for the submit form's album lookup.</summary>
    /// <param name="query">The search query.</param>
    /// <param name="artist">An optional artist name to filter by.</param>
    /// <returns>Matching album names.</returns>
    [HttpGet("Download/Albums")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<List<string>> Albums([FromQuery] string query, [FromQuery] string? artist = null)
    {
        PublicPage.ApplyHardeningHeaders(Response);
        if (!IsAllowed())
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        return Ok(_libraryService.SearchAlbums(query ?? string.Empty, artist));
    }

    internal bool IsAllowed() => CheckAccess().Allowed;

    /// <summary>
    /// Works out whether the signed-in user may submit downloads: administrators always, other
    /// users only when approved in Settings.
    /// </summary>
    internal (bool Allowed, bool IsAdministrator) CheckAccess()
    {
        var raw = User.FindFirst(UserIdClaim)?.Value;
        if (string.IsNullOrEmpty(raw) || !Guid.TryParse(raw, out var userId))
        {
            return (false, false);
        }

        var user = _userManager.GetUserById(userId);
        if (user == null)
        {
            return (false, false);
        }

        if (user.HasPermission(PermissionKind.IsAdministrator))
        {
            return (true, true);
        }

        var approved = Plugin.Instance?.Configuration.ApprovedUserIds ?? new List<string>();
        var normalized = userId.ToString("N");
        return (approved.Any(id => string.Equals(NormalizeId(id), normalized, StringComparison.OrdinalIgnoreCase)), false);
    }

    private static string NormalizeId(string id)
        => Guid.TryParse(id, out var g) ? g.ToString("N") : id;

    private static string GetContentFragment()
        => _pageHtml ??= ReadResource("youtubeaudio_download_public.html", "Download page unavailable.");

    private static string ReadResource(string fileName, string fallback)
    {
        var assembly = typeof(Plugin).Assembly;
        var resourceName = typeof(Plugin).Namespace + ".Configuration." + fileName;
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return "<!DOCTYPE html><html><body>" + fallback + "</body></html>";
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
