using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.YouTubeAudio.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.YouTubeAudio.Services;

/// <summary>
/// Queries the local Jellyfin library for artists, albums, and music libraries.
/// </summary>
public class LibraryService
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LibraryService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryService"/> class.
    /// </summary>
    public LibraryService(
        ILibraryManager libraryManager,
        ILogger<LibraryService> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Gets all music libraries configured in Jellyfin.
    /// </summary>
    public List<LibraryInfo> GetMusicLibraries()
    {
        try
        {
            var folders = _libraryManager.GetVirtualFolders();
            return folders
                .Where(f => f.CollectionType == CollectionTypeOptions.music)
                .Select(f => new LibraryInfo
                {
                    Id = f.ItemId,
                    Name = f.Name,
                    Path = f.Locations.FirstOrDefault() ?? string.Empty
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get music libraries");
            return new List<LibraryInfo>();
        }
    }

    /// <summary>
    /// Queues a library scan so freshly imported files appear without waiting for the
    /// next scheduled scan. Failures are logged and swallowed — the import itself succeeded.
    /// </summary>
    public void QueueLibraryScan()
    {
        try
        {
            _libraryManager.QueueLibraryScan();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to queue library scan after import");
        }
    }

    /// <summary>
    /// Lists every audio track in the Jellyfin music libraries, for duplicate detection.
    /// Covers all music libraries, not just the import target, so a song kept anywhere counts.
    /// </summary>
    public List<LibraryTrack> GetLibraryTracks()
    {
        try
        {
            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Audio },
                Recursive = true,
                IsVirtualItem = false
            });

            return items
                .OfType<Audio>()
                .Select(a => new LibraryTrack
                {
                    ItemId = a.Id,
                    Title = a.Name ?? string.Empty,
                    Artist = a.Artists.Count > 0 ? a.Artists[0] : null,
                    AlbumArtist = a.AlbumArtists.Count > 0 ? a.AlbumArtists[0] : null,
                    Album = a.Album,
                    Year = a.ProductionYear,
                    Path = a.Path,
                    DurationSeconds = a.RunTimeTicks > 0 ? TimeSpan.FromTicks(a.RunTimeTicks.Value).TotalSeconds : null
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list library tracks for duplicate detection");
            return new List<LibraryTrack>();
        }
    }

    /// <summary>
    /// Maps each artist in the music libraries (compared case- and punctuation-insensitively) to
    /// the spelling the library uses most, so new imports reuse "Linkin Park" rather than
    /// adding "LINKIN PARK" as a second artist.
    /// </summary>
    public Dictionary<string, string> GetArtistSpellings()
    {
        try
        {
            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Audio },
                Recursive = true,
                IsVirtualItem = false
            });

            return BuildArtistSpellings(items.OfType<Audio>().SelectMany(a => a.Artists.Concat(a.AlbumArtists)));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list library artists");
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Picks the most used spelling of each artist name.
    /// </summary>
    internal static Dictionary<string, string> BuildArtistSpellings(IEnumerable<string> names)
        => names
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .GroupBy(Services.Playlists.TrackMetadataCleaner.Normalize, StringComparer.Ordinal)
            .Where(g => g.Key.Length > 0)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(n => n, StringComparer.Ordinal).OrderByDescending(v => v.Count()).ThenBy(v => v.Key, StringComparer.Ordinal).First().Key,
                StringComparer.Ordinal);

    /// <summary>
    /// Searches for artists in the Jellyfin library matching the query.
    /// </summary>
    public List<string> SearchArtists(string query, int limit = 20)
    {
        try
        {
            var result = _libraryManager.GetItemsResult(new InternalItemsQuery
            {
                SearchTerm = query,
                IncludeItemTypes = new[] { BaseItemKind.MusicArtist },
                Limit = limit,
                Recursive = true
            });

            return result.Items
                .Select(i => i.Name)
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to search artists for query: {Query}", query);
            return new List<string>();
        }
    }

    /// <summary>
    /// Searches for albums in the Jellyfin library matching the query.
    /// Optionally filters to albums by a specific artist.
    /// </summary>
    public List<string> SearchAlbums(string query, string? artist = null, int limit = 20)
    {
        try
        {
            var result = _libraryManager.GetItemsResult(new InternalItemsQuery
            {
                SearchTerm = query,
                IncludeItemTypes = new[] { BaseItemKind.MusicAlbum },
                Limit = string.IsNullOrWhiteSpace(artist) ? limit : limit * 5, // Fetch more when filtering by artist
                Recursive = true
            });

            var albums = result.Items.AsEnumerable();

            // Post-filter by artist if specified
            if (!string.IsNullOrWhiteSpace(artist))
            {
                albums = albums.Where(a =>
                    a is MusicAlbum album &&
                    album.AlbumArtists.Any(aa => aa.Equals(artist, StringComparison.OrdinalIgnoreCase)));
            }

            return albums
                .Select(i => i.Name)
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to search albums for query: {Query}", query);
            return new List<string>();
        }
    }
}
