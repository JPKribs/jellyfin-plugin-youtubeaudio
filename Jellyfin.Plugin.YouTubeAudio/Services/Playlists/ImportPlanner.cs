using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.YouTubeAudio.Models;

namespace Jellyfin.Plugin.YouTubeAudio.Services.Playlists;

/// <summary>
/// What a playlist import will do: the tracks to queue and why the rest were left out.
/// </summary>
public sealed class ImportPlan
{
    /// <summary>Gets the cleaned tracks to queue, in playlist order.</summary>
    public List<PlaylistTrack> Tracks { get; } = new();

    /// <summary>Gets the queue items to remove because a better version of the song replaces them.</summary>
    public List<string> QueueIdsToRemove { get; } = new();

    /// <summary>Gets or sets the number of songs skipped because the library already has them.</summary>
    public int AlreadyInLibrary { get; set; }

    /// <summary>Gets or sets the number of songs skipped because they are already queued.</summary>
    public int AlreadyQueued { get; set; }

    /// <summary>Gets or sets the number of entries dropped as repeats or lesser versions of another selected track.</summary>
    public int Duplicates { get; set; }

    /// <summary>Gets or sets the number of queued tracks that will replace a lesser version in the library.</summary>
    public int LibraryUpgrades { get; set; }
}

/// <summary>
/// Decides what a playlist import queues. Pure logic over the parsed tracks, the library and
/// the queue, so the duplicate rules can be tested directly:
/// <list type="bullet">
/// <item>Each song (primary artist + title) is queued once, choosing its best release: album,
/// then compilation, then EP, then single.</item>
/// <item>A song the library already has is skipped, except that a library single is upgraded:
/// the studio album version is queued and replaces the single's file on import.</item>
/// <item>A song already queued is skipped, unless the queued copy is a lesser release that
/// has not downloaded yet, which is swapped for the better one, or a failed download, which is
/// replaced by a fresh attempt.</item>
/// <item>Editions of one album merge: tracks share one album name and year, and adopt the
/// library's name and year when the album is already there, so they land in its folder.</item>
/// </list>
/// </summary>
public static class ImportPlanner
{
    /// <summary>
    /// Builds the import plan.
    /// </summary>
    /// <param name="tracks">The tracks from the selected playlists, raw from the source, in order.</param>
    /// <param name="library">Tracks already in the library.</param>
    /// <param name="queued">Tracks already in the download queue.</param>
    /// <returns>The plan.</returns>
    public static ImportPlan Plan(IEnumerable<PlaylistTrack> tracks, IReadOnlyCollection<LibraryTrack> library, IReadOnlyCollection<QueuedTrack> queued)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(queued);

        var plan = new ImportPlan();

        // 1. One entry per song, keeping the best release at the song's first position.
        var chosen = new List<PlaylistTrack>();
        var chosenBySong = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var raw in tracks)
        {
            var track = TrackMetadataCleaner.Clean(raw);
            var key = TrackMetadataCleaner.SongKey(track.Artist, track.Title);
            if (chosenBySong.TryGetValue(key, out var index))
            {
                plan.Duplicates++;
                if (track.ReleaseType > chosen[index].ReleaseType)
                {
                    chosen[index] = track;
                }

                continue;
            }

            chosenBySong[key] = chosen.Count;
            chosen.Add(track);
        }

        // 2. Merge album editions within the import: one name and the earliest year.
        foreach (var album in chosen.GroupBy(AlbumKey))
        {
            var name = album.First().Album;
            var year = album.Min(t => t.Year);
            foreach (var track in album)
            {
                track.Album = name;
                track.Year = year;
            }
        }

        var librarySongs = library
            .Where(t => !string.IsNullOrWhiteSpace(t.Title))
            .GroupBy(t => TrackMetadataCleaner.SongKey(t.Artist, t.Title))
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var libraryAlbumSizes = library
            .GroupBy(t => TrackMetadataCleaner.AlbumKey(t.AlbumArtist, t.Artist, t.Album))
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var libraryAlbums = library
            .Where(t => !string.IsNullOrWhiteSpace(t.Album))
            .GroupBy(t => TrackMetadataCleaner.AlbumKey(t.AlbumArtist, t.Artist, t.Album))
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        // Imported rows (kept by older plugin versions) describe songs already moved into the
        // library; the library check covers them, so they never block a song from being queued.
        var queuedSongs = queued
            .Where(q => q.Status != QueueStatus.Imported && !string.IsNullOrWhiteSpace(q.Title))
            .GroupBy(q => TrackMetadataCleaner.SongKey(q.Artist, q.Title))
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        foreach (var track in chosen)
        {
            var key = TrackMetadataCleaner.SongKey(track.Artist, track.Title);
            var rank = track.ReleaseType ?? ReleaseType.Album;

            // 3. The library: skip what it has. The one upgrade is single → studio album, and
            //    only when the library copy is on a different album: a title track alone on its
            //    album ("Boléro" on Boléro) looks like a single but is the same release.
            if (librarySongs.TryGetValue(key, out var existing))
            {
                var trackAlbum = AlbumKey(track);
                var upgrade = rank == ReleaseType.Album
                    && existing.All(t => LibraryReleaseType(t, libraryAlbumSizes) == ReleaseType.Single
                        && TrackMetadataCleaner.AlbumKey(t.AlbumArtist, t.Artist, t.Album) != trackAlbum);
                if (!upgrade)
                {
                    plan.AlreadyInLibrary++;
                    continue;
                }

                track.ReplacesFiles = existing.Select(t => t.Path).OfType<string>().ToList();
                plan.LibraryUpgrades++;
            }

            // 4. The queue: skip what is queued, unless it is a lesser release not yet downloaded
            //    or a failed download, which is replaced by a fresh attempt.
            if (queuedSongs.TryGetValue(key, out var inQueue))
            {
                var replaceable = inQueue.All(q => q.Status == QueueStatus.Error
                    || (q.Status == QueueStatus.Queued && (q.ReleaseType ?? ReleaseType.Album) < rank));
                if (!replaceable)
                {
                    plan.AlreadyQueued++;
                    if (track.ReplacesFiles != null)
                    {
                        plan.LibraryUpgrades--;
                    }

                    continue;
                }

                plan.QueueIdsToRemove.AddRange(inQueue.Select(q => q.Id));
            }

            // 5. File into the library's existing album folder when there is one.
            if (libraryAlbums.TryGetValue(AlbumKey(track), out var libraryAlbum))
            {
                track.Album = libraryAlbum.Album;
                track.Year = libraryAlbum.Year ?? track.Year;
            }

            plan.Tracks.Add(track);
        }

        return plan;
    }

    /// <summary>
    /// Infers the release type of a library track. Tags no longer carry Apple's " - Single"
    /// suffix, so a track alone on an album named after itself counts as a single.
    /// </summary>
    internal static ReleaseType LibraryReleaseType(LibraryTrack track, IReadOnlyDictionary<string, int> albumSizes)
    {
        if (string.Equals(track.AlbumArtist?.Trim(), "Various Artists", StringComparison.OrdinalIgnoreCase)
            || TrackMetadataCleaner.IsCompilationName(track.Album))
        {
            return ReleaseType.Compilation;
        }

        var albumKey = TrackMetadataCleaner.AlbumKey(track.AlbumArtist, track.Artist, track.Album);
        var (title, _) = TrackMetadataCleaner.ExtractFeatured(track.Title);
        var namedAfterTrack = string.IsNullOrWhiteSpace(track.Album)
            || TrackMetadataCleaner.Normalize(TrackMetadataCleaner.CleanAlbum(track.Album)) == TrackMetadataCleaner.Normalize(title);
        var alone = !albumSizes.TryGetValue(albumKey, out var size) || size <= 1;

        return namedAfterTrack && alone ? ReleaseType.Single : ReleaseType.Album;
    }

    private static string AlbumKey(PlaylistTrack track)
        => TrackMetadataCleaner.AlbumKey(track.AlbumArtist, track.Artist, track.Album);
}
