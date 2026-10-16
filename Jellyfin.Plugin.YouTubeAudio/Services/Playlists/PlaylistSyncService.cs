using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.YouTubeAudio.Models;
using JPKribs.Jellyfin.Base;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Playlists;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeAudio.Services.Playlists;

/// <summary>
/// Fills the Jellyfin playlists recorded by playlist imports. Each target is a one-time import
/// into a private playlist owned by the chosen user and named after the source playlist; songs
/// are added as they reach the library (immediately for songs already there, otherwise after
/// their download is imported and scanned). An existing playlist of the same name owned by that
/// user is appended to, skipping songs it already holds in any version, and is then re-ordered
/// to follow the source. A target is dropped once nothing is left to add: every song is in the
/// playlist, or its queue row was removed (a failed download counts until it is retried or removed).
/// </summary>
public sealed class PlaylistSyncService : IHostedService, IDisposable
{
    // Library scans add items in bursts; wait for a quiet moment before syncing.
    private static readonly TimeSpan _scanDebounce = TimeSpan.FromSeconds(15);

    // Catch up on scans that finished while the server was down.
    private static readonly TimeSpan _startupDelay = TimeSpan.FromMinutes(1);

    // An imported song not in the library after this long was never picked up by a scan.
    private static readonly TimeSpan _scanGrace = TimeSpan.FromHours(1);

    private readonly ILibraryManager _libraryManager;
    private readonly IPlaylistManager _playlistManager;
    private readonly IUserManager _userManager;
    private readonly LibraryService _libraryService;
    private readonly QueueDatabaseProvider _dbProvider;
    private readonly ILogger<PlaylistSyncService> _logger;
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "Disposed in Dispose() method")]
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly Timer _timer;
    private volatile bool _hasPending = true;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaylistSyncService"/> class.
    /// </summary>
    public PlaylistSyncService(
        ILibraryManager libraryManager,
        IPlaylistManager playlistManager,
        IUserManager userManager,
        LibraryService libraryService,
        QueueDatabaseProvider dbProvider,
        ILogger<PlaylistSyncService> logger)
    {
        _libraryManager = libraryManager;
        _playlistManager = playlistManager;
        _userManager = userManager;
        _libraryService = libraryService;
        _dbProvider = dbProvider;
        _logger = logger;
        _timer = new Timer(_ => _ = RunScheduledSyncAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        RequestSync(_startupDelay);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Schedules a sync, replacing any already scheduled one.
    /// </summary>
    /// <param name="delay">How long to wait; defaults to the scan debounce.</param>
    public void RequestSync(TimeSpan? delay = null)
    {
        _hasPending = true;
        _timer.Change(delay ?? _scanDebounce, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Adds every pending song that is now in the library to its Jellyfin playlist.
    /// </summary>
    /// <returns>The number of songs added to playlists.</returns>
    public async Task<int> SyncAsync(CancellationToken cancellationToken)
    {
        await _syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var db = _dbProvider.Database;
            var pending = db.GetPlaylistEntries(pendingOnly: true);
            var targets = db.GetPlaylistTargets();
            if (pending.Count == 0 && targets.Count == 0)
            {
                _hasPending = false;
                return 0;
            }

            var library = BuildLibraryIndex(_libraryService.GetLibraryTracks());
            // Failed downloads stay in the queue until they are retried or removed, so their songs
            // stay in the import: a retry that succeeds still lands in the playlist.
            var activeItems = db.GetAllItems()
                .Where(item => item.Status != QueueStatus.Imported && !string.IsNullOrWhiteSpace(item.Title))
                .ToList();
            var activeQueue = activeItems
                .Select(item => TrackMetadataCleaner.SongKey(item.Artist, item.Title))
                .ToHashSet(StringComparer.Ordinal);

            // Songs whose album version is on its way to replace a library single wait for it,
            // so playlists never pick up the single that is about to be deleted.
            var awaitingUpgrade = activeItems
                .Where(item => item.Status != QueueStatus.Error && item.SourceTrack?.ReplacesFiles is { Count: > 0 })
                .Select(item => TrackMetadataCleaner.SongKey(item.Artist, item.Title))
                .ToHashSet(StringComparer.Ordinal);

            // Songs that can no longer arrive (download failed or removed) leave the import.
            foreach (var group in pending.Where(e => IsAbandoned(e, library, activeQueue, DateTime.UtcNow)).GroupBy(e => e.TargetId))
            {
                db.DeletePlaylistEntries(group.Key, group.Select(e => e.SongKey));
            }

            var added = 0;
            var pendingByTarget = pending.GroupBy(e => e.TargetId).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var targetPending = (pendingByTarget.GetValueOrDefault(target.Id) ?? new List<PlaylistEntry>())
                    .Where(e => !awaitingUpgrade.Contains(e.SongKey))
                    .ToList();
                if (!targetPending.Any(e => library.ContainsKey(e.SongKey)))
                {
                    continue;
                }

                try
                {
                    added += await SyncTargetAsync(target, targetPending, library).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Failed to sync playlist {Name}", FormatUtilities.SanitizeForLog(target.Name));
                }
            }

            // One-time imports: drop targets with nothing left to add.
            var stillPending = db.GetPlaylistEntries(pendingOnly: true).Select(e => e.TargetId).ToHashSet(StringComparer.Ordinal);
            foreach (var target in targets.Where(t => !stillPending.Contains(t.Id)))
            {
                db.DeletePlaylistTarget(target.Id);
                _logger.LogInformation("Playlist import {Name} finished", FormatUtilities.SanitizeForLog(target.Name));
            }

            _hasPending = stillPending.Count > 0;
            return added;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>
    /// Lists playlist targets with their progress.
    /// </summary>
    public List<PlaylistTargetDto> GetTargets()
    {
        var db = _dbProvider.Database;
        var entries = db.GetPlaylistEntries().GroupBy(e => e.TargetId).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        return db.GetPlaylistTargets().Select(target =>
        {
            var targetEntries = entries.GetValueOrDefault(target.Id) ?? new List<PlaylistEntry>();
            var user = Guid.TryParse(target.UserId, out var userId) ? _userManager.GetUserById(userId) : null;
            return new PlaylistTargetDto
            {
                Id = target.Id,
                Name = target.Name,
                UserName = user?.Username ?? "Unknown user",
                Total = targetEntries.Count,
                Added = targetEntries.Count(e => e.AddedAt != null),
                JellyfinPlaylistId = target.JellyfinPlaylistId
            };
        }).ToList();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _timer.Dispose();
        _syncLock.Dispose();
    }

    /// <summary>
    /// Maps each song key to the library item to use for it. When the library holds several
    /// versions, the album version wins over a compilation, EP or single.
    /// </summary>
    internal static Dictionary<string, Guid> BuildLibraryIndex(IReadOnlyCollection<LibraryTrack> tracks)
    {
        var albumSizes = tracks
            .GroupBy(t => TrackMetadataCleaner.AlbumKey(t.AlbumArtist, t.Artist, t.Album))
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        return tracks
            .Where(t => t.ItemId != Guid.Empty && !string.IsNullOrWhiteSpace(t.Title))
            .GroupBy(t => TrackMetadataCleaner.SongKey(t.Artist, t.Title))
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(t => ImportPlanner.LibraryReleaseType(t, albumSizes)).First().ItemId,
                StringComparer.Ordinal);
    }

    /// <summary>
    /// Decides what to add to a playlist: pending songs now in the library, in playlist order,
    /// minus songs the playlist already holds. Both the added and the already-present songs
    /// count as done.
    /// </summary>
    internal static (List<Guid> ItemIds, List<string> DoneKeys) PlanAdditions(
        IEnumerable<PlaylistEntry> pending,
        IReadOnlyDictionary<string, Guid> library,
        IReadOnlySet<string> playlistKeys)
    {
        var itemIds = new List<Guid>();
        var doneKeys = new List<string>();

        foreach (var entry in pending.OrderBy(e => e.Position))
        {
            if (playlistKeys.Contains(entry.SongKey))
            {
                doneKeys.Add(entry.SongKey);
            }
            else if (library.TryGetValue(entry.SongKey, out var itemId) && !itemIds.Contains(itemId))
            {
                itemIds.Add(itemId);
                doneKeys.Add(entry.SongKey);
            }
        }

        return (itemIds, doneKeys);
    }

    /// <summary>
    /// Returns whether a pending song will never reach the library: it is not there, not in the
    /// download queue (a failed download still counts, since it can be retried), and was not just
    /// imported and awaiting a scan.
    /// </summary>
    internal static bool IsAbandoned(PlaylistEntry entry, IReadOnlyDictionary<string, Guid> library, IReadOnlySet<string> activeQueue, DateTime now)
    {
        if (library.ContainsKey(entry.SongKey) || activeQueue.Contains(entry.SongKey))
        {
            return false;
        }

        return !DateTime.TryParse(entry.ImportedAt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var importedAt)
            || now - importedAt > _scanGrace;
    }

    /// <summary>
    /// Computes the playlist order that follows the source: the slots holding source songs are
    /// refilled in source order, while songs the user added themselves keep their places.
    /// </summary>
    /// <param name="current">The playlist's items in order, with their song keys.</param>
    /// <param name="positions">Source position per song key.</param>
    /// <returns>The item IDs in the desired order.</returns>
    internal static List<Guid> PlanOrder(IReadOnlyList<(Guid Id, string? Key)> current, IReadOnlyDictionary<string, int> positions)
    {
        var sourceItems = current
            .Where(item => item.Key != null && positions.ContainsKey(item.Key))
            .OrderBy(item => positions[item.Key!])
            .Select(item => item.Id)
            .ToList();

        var desired = new List<Guid>(current.Count);
        var next = 0;
        foreach (var item in current)
        {
            desired.Add(item.Key != null && positions.ContainsKey(item.Key) ? sourceItems[next++] : item.Id);
        }

        return desired;
    }

    private async Task<int> SyncTargetAsync(PlaylistTarget target, List<PlaylistEntry> pending, Dictionary<string, Guid> library)
    {
        if (!Guid.TryParse(target.UserId, out var userId) || _userManager.GetUserById(userId) == null)
        {
            _logger.LogWarning("Skipping playlist {Name}: its user no longer exists", FormatUtilities.SanitizeForLog(target.Name));
            return 0;
        }

        var playlist = FindPlaylist(target, userId);
        // A playlist entry whose file was deleted (a replaced single not yet removed by the scan)
        // does not count as having the song.
        var playlistKeys = playlist == null
            ? new HashSet<string>(StringComparer.Ordinal)
            : playlist.GetManageableItems()
                .Select(pair => pair.Item2)
                .OfType<Audio>()
                .Where(audio => string.IsNullOrEmpty(audio.Path) || System.IO.File.Exists(audio.Path))
                .Select(audio => TrackMetadataCleaner.SongKey(audio.Artists.Count > 0 ? audio.Artists[0] : null, audio.Name))
                .ToHashSet(StringComparer.Ordinal);

        var (itemIds, doneKeys) = PlanAdditions(pending, library, playlistKeys);

        if (itemIds.Count > 0)
        {
            if (playlist == null)
            {
                var created = await _playlistManager.CreatePlaylist(new PlaylistCreationRequest
                {
                    Name = target.Name,
                    UserId = userId,
                    ItemIdList = itemIds,
                    MediaType = MediaType.Audio,
                    Users = Array.Empty<PlaylistUserPermissions>(),
                    Public = false
                }).ConfigureAwait(false);
                _dbProvider.Database.SetJellyfinPlaylistId(target.Id, created.Id);
                _logger.LogInformation("Created playlist {Name} with {Count} songs", FormatUtilities.SanitizeForLog(target.Name), itemIds.Count);
            }
            else
            {
                await _playlistManager.AddItemToPlaylistAsync(playlist.Id, itemIds, null, userId).ConfigureAwait(false);
                if (target.JellyfinPlaylistId == null)
                {
                    _dbProvider.Database.SetJellyfinPlaylistId(target.Id, playlist.Id.ToString("N"));
                }

                _logger.LogInformation("Added {Count} songs to playlist {Name}", itemIds.Count, FormatUtilities.SanitizeForLog(target.Name));
            }
        }

        if (doneKeys.Count > 0)
        {
            _dbProvider.Database.MarkPlaylistEntriesAdded(target.Id, doneKeys);
        }

        if (doneKeys.Count > 0)
        {
            await ReorderAsync(target, userId).ConfigureAwait(false);
        }

        return itemIds.Count;
    }

    /// <summary>
    /// Moves the playlist's source songs into source order (see <see cref="PlanOrder"/>).
    /// </summary>
    private async Task ReorderAsync(PlaylistTarget target, Guid userId)
    {
        var playlist = FindPlaylist(target, userId);
        if (playlist == null)
        {
            return;
        }

        var current = playlist.GetManageableItems()
            .Select(pair => (Id: pair.Item1.ItemId ?? pair.Item2.Id, Key: pair.Item2 is Audio audio
                ? TrackMetadataCleaner.SongKey(audio.Artists.Count > 0 ? audio.Artists[0] : null, audio.Name)
                : null))
            .ToList();

        // Moves address entries by item ID, which is ambiguous when an item appears twice.
        if (current.Select(item => item.Id).Distinct().Count() != current.Count)
        {
            return;
        }

        var positions = _dbProvider.Database.GetPlaylistEntries(target.Id)
            .GroupBy(e => e.SongKey)
            .ToDictionary(g => g.Key, g => g.Min(e => e.Position), StringComparer.Ordinal);
        var desired = PlanOrder(current, positions);
        var order = current.Select(item => item.Id).ToList();
        var moves = 0;

        for (var i = 0; i < desired.Count; i++)
        {
            if (order[i] == desired[i])
            {
                continue;
            }

            await _playlistManager.MoveItemAsync(playlist.Id.ToString("N"), desired[i].ToString("N"), i, userId).ConfigureAwait(false);
            order.Remove(desired[i]);
            order.Insert(i, desired[i]);
            moves++;
        }

        if (moves > 0)
        {
            _logger.LogInformation("Re-ordered {Count} songs in playlist {Name}", moves, FormatUtilities.SanitizeForLog(target.Name));
        }
    }

    /// <summary>
    /// Finds the user's playlist for a target: the one it created earlier if it still exists,
    /// otherwise one the user owns with the same name.
    /// </summary>
    private Playlist? FindPlaylist(PlaylistTarget target, Guid userId)
    {
        if (Guid.TryParse(target.JellyfinPlaylistId, out var playlistId)
            && _libraryManager.GetItemById(playlistId) is Playlist known
            && known.OwnerUserId == userId)
        {
            return known;
        }

        return _playlistManager.GetPlaylists(userId)
            .FirstOrDefault(p => p.OwnerUserId == userId && string.Equals(p.Name, target.Name, StringComparison.OrdinalIgnoreCase));
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (_hasPending && e.Item is Audio)
        {
            RequestSync();
        }
    }

    private async Task RunScheduledSyncAsync()
    {
        try
        {
            var added = await SyncAsync(CancellationToken.None).ConfigureAwait(false);
            if (added > 0)
            {
                _logger.LogInformation("Playlist sync added {Count} songs", added);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Playlist sync failed");
        }
    }
}
