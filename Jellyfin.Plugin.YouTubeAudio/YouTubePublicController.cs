using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.YouTubeAudio.Models;
using Jellyfin.Plugin.YouTubeAudio.Services;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeAudio;

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

    private static readonly object _faviconLock = new();
    private static bool _faviconResolved;
    private static byte[]? _faviconBytes;
    private static string _faviconContentType = "image/x-icon";

    private readonly DownloadService _downloadService;
    private readonly LibraryService _libraryService;
    private readonly IUserManager _userManager;
    private readonly IServerApplicationPaths _paths;
    private readonly ILogger<YouTubePublicController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="YouTubePublicController"/> class.
    /// </summary>
    /// <param name="downloadService">The download service.</param>
    /// <param name="libraryService">The library service, used for artist and album lookups.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="paths">The server application paths, used to locate the web client favicon.</param>
    /// <param name="logger">The logger.</param>
    public YouTubePublicController(
        DownloadService downloadService,
        LibraryService libraryService,
        IUserManager userManager,
        IServerApplicationPaths paths,
        ILogger<YouTubePublicController> logger)
    {
        _downloadService = downloadService;
        _libraryService = libraryService;
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
        ApplyHardeningHeaders();
        var html = TemplateLoader.Fill("status", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TITLE"] = "Download",
            ["HEADING"] = "Loading…",
            ["MESSAGE"] = string.Empty,
            ["SPINNER"] = "<div class=\"jpk-spinner\" role=\"status\" aria-label=\"Loading\"></div>",
            ["BUTTON"] = string.Empty,
            ["CONTENT"] = GetContentFragment()
        });
        return Content(html, "text/html");
    }

    /// <summary>Serves the web client's favicon so the page can reuse the server's real icon at a stable path.</summary>
    /// <returns>The favicon bytes, or 404 when none could be located.</returns>
    [HttpGet("favicon.ico")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult Favicon()
    {
        var favicon = GetFavicon();
        if (favicon is null)
        {
            return NotFound();
        }

        Response.Headers["Cache-Control"] = "public, max-age=86400";
        return File(favicon.Value.Bytes, favicon.Value.ContentType);
    }

    /// <summary>Reports whether the signed-in user may submit downloads.</summary>
    /// <returns>An object with an Approved flag.</returns>
    [HttpGet("Download/Info")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult Info()
    {
        ApplyHardeningHeaders();
        return Ok(new { Approved = IsAllowed() });
    }

    /// <summary>Queues a submitted URL with metadata and starts the download in the background.</summary>
    /// <param name="request">The submitted URL and metadata.</param>
    /// <returns>The number of queued items, or an error.</returns>
    [HttpPost("Download/Submit")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> Submit([FromBody] SubmitRequest request)
    {
        ApplyHardeningHeaders();

        if (!IsAllowed())
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
            var items = await _downloadService.QueueUrlAsync(
                request.Url,
                request.Artist,
                request.Album,
                request.Year,
                request.Title).ConfigureAwait(false);

            var ids = items.Select(i => i.Id).ToList();
            _ = Task.Run(() => _downloadService.ProcessQueueAsync(CancellationToken.None, ids));

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
        ApplyHardeningHeaders();
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
        ApplyHardeningHeaders();
        if (!IsAllowed())
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        return Ok(_libraryService.SearchAlbums(query ?? string.Empty, artist));
    }

    private bool IsAllowed()
    {
        var raw = User.FindFirst(UserIdClaim)?.Value;
        if (string.IsNullOrEmpty(raw) || !Guid.TryParse(raw, out var userId))
        {
            return false;
        }

        var user = _userManager.GetUserById(userId);
        if (user == null)
        {
            return false;
        }

        if (user.HasPermission(PermissionKind.IsAdministrator))
        {
            return true;
        }

        var approved = Plugin.Instance?.Configuration.ApprovedUserIds ?? new List<string>();
        var normalized = userId.ToString("N");
        return approved.Any(id => string.Equals(NormalizeId(id), normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeId(string id)
        => Guid.TryParse(id, out var g) ? g.ToString("N") : id;

    private void ApplyHardeningHeaders()
    {
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["Cache-Control"] = "no-store";
        Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
    }

    private static string GetContentFragment()
    {
        if (_pageHtml is not null)
        {
            return _pageHtml;
        }

        var assembly = typeof(Plugin).Assembly;
        var resourceName = typeof(Plugin).Namespace + ".Configuration.youtubeaudio_download_public.html";
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return "<!DOCTYPE html><html><body>Download page unavailable.</body></html>";
        }

        using var reader = new StreamReader(stream);
        _pageHtml = reader.ReadToEnd();
        return _pageHtml;
    }

    private (byte[] Bytes, string ContentType)? GetFavicon()
    {
        lock (_faviconLock)
        {
            if (!_faviconResolved)
            {
                ResolveFavicon();
                _faviconResolved = true;
            }
        }

        return _faviconBytes is null ? null : (_faviconBytes, _faviconContentType);
    }

    private void ResolveFavicon()
    {
        try
        {
            var web = _paths.WebPath;
            if (string.IsNullOrEmpty(web) || !Directory.Exists(web))
            {
                return;
            }

            var root = Path.GetFullPath(web);
            var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            string? file = null;

            var indexPath = Path.Combine(root, "index.html");
            if (System.IO.File.Exists(indexPath))
            {
                var match = Regex.Match(
                    System.IO.File.ReadAllText(indexPath),
                    "rel=\"(?:shortcut )?icon\"[^>]*href=\"([^\"]+)\"",
                    RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    var href = match.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
                    var candidate = Path.GetFullPath(Path.Combine(root, href));
                    if (candidate.StartsWith(rootPrefix, StringComparison.Ordinal) && System.IO.File.Exists(candidate))
                    {
                        file = candidate;
                    }
                }
            }

            file ??= Directory.EnumerateFiles(root, "favicon*.ico").FirstOrDefault();
            if (file is null)
            {
                return;
            }

            _faviconBytes = System.IO.File.ReadAllBytes(file);
            _faviconContentType = file.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/x-icon";
        }
        catch (IOException)
        {
            // Leave the favicon unresolved; the endpoint returns 404 and the browser falls back.
        }
        catch (UnauthorizedAccessException)
        {
            // Leave the favicon unresolved; the endpoint returns 404 and the browser falls back.
        }
    }
}
