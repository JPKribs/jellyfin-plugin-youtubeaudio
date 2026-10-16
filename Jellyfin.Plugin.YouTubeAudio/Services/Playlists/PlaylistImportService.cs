using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.YouTubeAudio.Models;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeAudio.Services.Playlists;

/// <summary>
/// Turns an uploaded playlist export into queue items. Picks the registered
/// <see cref="IPlaylistSource"/> that recognizes the file, then lets <see cref="ImportPlanner"/>
/// clean the metadata and drop duplicates against the library and queue. The YouTube match for
/// each track is found later, at download time.
/// </summary>
public sealed class PlaylistImportService
{
    /// <summary>
    /// Upper bound on an uploaded export. A library export runs about 2 KB per track, so this
    /// covers a library of roughly 30,000 tracks.
    /// </summary>
    internal const int MaxFileBytes = 64 * 1024 * 1024;

    /// <summary>Most files a playlist ZIP may hold.</summary>
    internal const int MaxArchiveEntries = 1000;

    /// <summary>Request body limit for the upload endpoint: the file base64 encoded, plus slack.</summary>
    internal const long MaxRequestBytes = (MaxFileBytes / 3 * 4) + (1024 * 1024);

    private readonly IReadOnlyList<IPlaylistSource> _sources;
    private readonly DownloadService _downloadService;
    private readonly LibraryService _libraryService;
    private readonly PlaylistSyncService _playlistSync;
    private readonly QueueDatabaseProvider _dbProvider;
    private readonly IUserManager _userManager;
    private readonly ILogger<PlaylistImportService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaylistImportService"/> class.
    /// </summary>
    public PlaylistImportService(
        IEnumerable<IPlaylistSource> sources,
        DownloadService downloadService,
        LibraryService libraryService,
        PlaylistSyncService playlistSync,
        QueueDatabaseProvider dbProvider,
        IUserManager userManager,
        ILogger<PlaylistImportService> logger)
    {
        _playlistSync = playlistSync;
        _dbProvider = dbProvider;
        _userManager = userManager;
        _sources = sources.ToList();
        _downloadService = downloadService;
        _libraryService = libraryService;
        _logger = logger;
    }

    /// <summary>
    /// Lists the supported playlist sources.
    /// </summary>
    public List<PlaylistSourceInfo> GetSources()
        => _sources.Select(source => new PlaylistSourceInfo
        {
            Id = source.Id,
            Name = source.Name,
            Instructions = source.Instructions,
            Extensions = source.Extensions
        }).ToList();

    /// <summary>
    /// Parses an uploaded export and queues one item per track.
    /// </summary>
    /// <param name="request">The uploaded file.</param>
    /// <returns>The source used and the created queue items.</returns>
    /// <exception cref="ArgumentException">The file is missing, too large, or not a supported export.</exception>
    public PlaylistImportResult Import(PlaylistImportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrEmpty(request.Content))
        {
            throw new ArgumentException("No file was uploaded.");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(request.Content);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("The uploaded file could not be read.", ex);
        }

        if (bytes.Length > MaxFileBytes)
        {
            throw new ArgumentException("The file is too large to be a playlist export.");
        }

        var fileName = Path.GetFileName(request.FileName ?? string.Empty);
        var (source, playlists) = IsZip(bytes) ? ParseArchive(_sources, bytes) : Parse(_sources, fileName, DecodeText(bytes));

        var choices = playlists.Select(p => new PlaylistChoice
        {
            Id = p.Id ?? string.Empty,
            Name = p.Name ?? "Untitled Playlist",
            IsLibrary = p.IsLibrary,
            TrackCount = p.Tracks.Count
        }).ToList();

        var selected = SelectPlaylists(playlists, request.PlaylistIds);
        if (selected == null)
        {
            return new PlaylistImportResult { Source = source.Name, NeedsSelection = true, Playlists = choices };
        }

        if (selected.All(p => p.Tracks.Count == 0))
        {
            throw new ArgumentException($"No tracks were found in the {source.Name} export.");
        }

        var playlistUserId = ResolvePlaylistUser(request.UserId);

        var plan = ImportPlanner.Plan(
            selected.SelectMany(p => p.Tracks),
            _libraryService.GetLibraryTracks(),
            _downloadService.GetQueuedTracks());

        foreach (var id in plan.QueueIdsToRemove.Concat(plan.StaleQueueIds))
        {
            _downloadService.DeleteCachedFile(id);
        }

        if (plan.StaleQueueIds.Count > 0)
        {
            _logger.LogInformation("Removed {Count} queued or failed songs the library now has", plan.StaleQueueIds.Count);
        }

        var playlistName = string.Join(", ", selected.Select(p => p.Name).Where(n => !string.IsNullOrEmpty(n)));
        var skipped = selected.Sum(p => p.SkippedRows);
        var items = plan.Tracks.Count > 0
            ? _downloadService.QueuePlaylistTracks(source.Id, plan.Tracks)
            : new List<QueueItemDto>();

        // Record a Jellyfin playlist per imported playlist for the chosen user. Every song is
        // listed, including ones skipped as already in the library, so the playlist is complete.
        var playlistsTracked = 0;
        if (playlistUserId != null)
        {
            foreach (var playlist in selected.Where(p => !p.IsLibrary && !string.IsNullOrWhiteSpace(p.Name)))
            {
                var target = _dbProvider.Database.GetOrCreatePlaylistTarget(playlistUserId, playlist.Name!.Trim(), source.Id);
                _dbProvider.Database.AddPlaylistEntries(target.Id, BuildPlaylistEntries(playlist, plan.LibraryAliases));
                playlistsTracked++;
            }

            if (playlistsTracked > 0)
            {
                // Songs already in the library go into the playlist right away.
                _playlistSync.RequestSync(TimeSpan.FromSeconds(2));
            }
        }

        _logger.LogInformation(
            "Queued {Count} tracks from {Source} playlist(s) {Name}: {InLibrary} already in library, {Queued} already queued, {Duplicates} duplicates, {Upgrades} replacing library singles, {Skipped} unreadable",
            items.Count,
            source.Name,
            FormatUtilities.SanitizeForLog(playlistName),
            plan.AlreadyInLibrary,
            plan.AlreadyQueued,
            plan.Duplicates,
            plan.LibraryUpgrades,
            skipped);

        return new PlaylistImportResult
        {
            Source = source.Name,
            PlaylistName = string.IsNullOrEmpty(playlistName) ? null : playlistName,
            Playlists = choices,
            Items = items,
            SkippedRows = skipped,
            DuplicateTracks = plan.Duplicates,
            AlreadyInLibrary = plan.AlreadyInLibrary,
            AlreadyQueued = plan.AlreadyQueued,
            LibraryUpgrades = plan.LibraryUpgrades,
            PlaylistsTracked = playlistsTracked
        };
    }

    /// <summary>
    /// Lists a playlist's songs as playlist entries, in order, once per song. Songs the library
    /// has under another title (see <see cref="ImportPlan.LibraryAliases"/>) use the library's key.
    /// </summary>
    internal static List<PlaylistEntry> BuildPlaylistEntries(ParsedPlaylist playlist, IReadOnlyDictionary<string, string>? libraryAliases = null)
    {
        var entries = new List<PlaylistEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in playlist.Tracks)
        {
            var track = TrackMetadataCleaner.Clean(raw);
            var key = TrackMetadataCleaner.SongKey(track.Artist, track.Title);
            if (libraryAliases != null && libraryAliases.TryGetValue(key, out var libraryKey))
            {
                key = libraryKey;
            }

            if (seen.Add(key))
            {
                entries.Add(new PlaylistEntry { Position = entries.Count, SongKey = key, Artist = track.Artist, Title = track.Title });
            }
        }

        return entries;
    }

    private string? ResolvePlaylistUser(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        if (!Guid.TryParse(userId, out var id) || _userManager.GetUserById(id) == null)
        {
            throw new ArgumentException("The selected playlist owner is not a Jellyfin user.");
        }

        return id.ToString("N");
    }

    /// <summary>
    /// Resolves which playlists to queue. A single-playlist file needs no choice; a file with
    /// several returns null until the user has picked some.
    /// </summary>
    /// <exception cref="ArgumentException">A requested ID is not in the file.</exception>
    internal static List<ParsedPlaylist>? SelectPlaylists(List<ParsedPlaylist> playlists, List<string>? requestedIds)
    {
        var requested = requestedIds?.Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList() ?? new List<string>();
        if (requested.Count == 0)
        {
            return playlists.Count == 1 ? playlists : null;
        }

        return requested
            .Select(id => playlists.FirstOrDefault(p => p.Id == id)
                ?? throw new ArgumentException("A selected playlist is not in the uploaded file."))
            .ToList();
    }

    /// <summary>
    /// Finds the first source that recognizes the file and parses it.
    /// </summary>
    internal static (IPlaylistSource Source, List<ParsedPlaylist> Playlists) Parse(IReadOnlyList<IPlaylistSource> sources, string fileName, string content)
    {
        var source = sources.FirstOrDefault(s => s.CanParse(fileName, content))
            ?? throw new ArgumentException(
                "This file isn't a supported playlist export. Supported: "
                + string.Join(", ", sources.Select(s => s.Name)) + ".");

        return (source, source.Parse(fileName, content));
    }

    /// <summary>
    /// Returns whether the upload is a ZIP archive (a "PK" local file header).
    /// </summary>
    internal static bool IsZip(byte[] bytes) => bytes.Length > 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] == 0x03 && bytes[3] == 0x04;

    /// <summary>
    /// Reads a ZIP of playlist exports (Exportify's "export all"): every CSV inside becomes one
    /// playlist to choose from, identified by its file name. Whole-library playlists (Liked
    /// Songs) are listed last. Only CSV files are read, and the archive's entry count and
    /// unpacked size are capped so a malformed or malicious ZIP cannot exhaust memory.
    /// </summary>
    internal static (IPlaylistSource Source, List<ParsedPlaylist> Playlists) ParseArchive(IReadOnlyList<IPlaylistSource> sources, byte[] bytes)
    {
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var entries = archive.Entries
            .Where(e => e.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) && !e.Name.StartsWith('.') && !e.FullName.StartsWith("__MACOSX", StringComparison.Ordinal))
            .ToList();
        if (entries.Count == 0 || entries.Count > MaxArchiveEntries)
        {
            throw new ArgumentException(entries.Count == 0 ? "The ZIP holds no playlist exports." : "The ZIP holds too many files to be a playlist export.");
        }

        IPlaylistSource? source = null;
        var playlists = new List<ParsedPlaylist>();
        long unpacked = 0;
        foreach (var entry in entries)
        {
            unpacked += entry.Length;
            if (unpacked > MaxFileBytes)
            {
                throw new ArgumentException("The ZIP is too large to be a playlist export.");
            }

            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var (entrySource, entryPlaylists) = Parse(sources, entry.Name, DecodeText(buffer.ToArray()));
            if (source != null && source.Id != entrySource.Id)
            {
                throw new ArgumentException("The ZIP mixes exports from different services.");
            }

            source = entrySource;
            foreach (var playlist in entryPlaylists)
            {
                playlist.Id ??= entry.FullName;
                playlists.Add(playlist);
            }
        }

        return (source!, playlists.OrderBy(p => p.IsLibrary).ToList());
    }

    /// <summary>
    /// Decodes file bytes using the byte-order mark when present (the Music app writes UTF-16
    /// LE with one), falling back to UTF-8. Sources therefore always receive plain text.
    /// </summary>
    internal static string DecodeText(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
