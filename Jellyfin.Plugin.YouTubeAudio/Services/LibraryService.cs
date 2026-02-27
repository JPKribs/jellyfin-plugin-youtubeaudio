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
