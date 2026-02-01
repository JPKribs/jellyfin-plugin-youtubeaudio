# Metadata Syncing

## Summary

Metadata syncing enables one-way synchronization of media metadata from a source Jellyfin server to your local Jellyfin server. The plugin syncs item metadata (titles, descriptions, ratings, genres, tags), images (posters, backdrops, logos), and people/artists associations from matched items on the source server to their corresponding local items.

Unlike content syncing which downloads media files, metadata syncing copies the descriptive data about your media. This is useful when you curate metadata on a primary server and want secondary servers to reflect those changes. For example, if you fix a movie's description or add custom artwork on your main server, metadata sync replicates those changes to other servers.

Metadata sync operates at the property category level, creating separate sync records for Metadata, Images, and People. This allows you to selectively sync only what you need. Items are matched by file path using your existing library mappings—the same matching logic used by History Sync.

```
Source Server                         Local Server
┌─────────────┐                       ┌─────────────────────────────┐
│             │                       │                             │
│  Item A     │                       │  Item A (matched by path)   │
│  Metadata   │ ──── API Scan ─────►  │  Tracking Database          │
│             │                       │  (compares source vs local) │
│             │                       │                             │
│ - Title     │                       │         ▼                   │
│ - Overview  │                       │  ┌─────────────────────┐    │
│ - Images    │                       │  │ Per-Category Sync   │    │
│ - People    │                       │  │ (Metadata, Images,  │    │
│             │                       │  │  People)            │    │
│             │                       │  └─────────────────────┘    │
│             │                       │         ▼                   │
│             │                       │  Apply to Local Item        │
└─────────────┘                       └─────────────────────────────┘
```

---

## How it Works

Metadata sync uses your existing library mappings to translate file paths between servers. When you configure library mappings for content or history sync, metadata sync can use those same mappings to find corresponding items.

The **Refresh Metadata Sync Table Task** runs periodically (default: every 6 hours) and performs a scan of all enabled library mappings. For each item on the source server, it translates the file path and attempts to find a matching local item. If found, it compares metadata between the two items and creates sync records for any differences.

For each matched item, the plugin creates up to three sync records (one per enabled category):

- **Metadata**: Core item properties like Name, Overview, Genres, Tags, Ratings, etc.
- **Images**: All image types (Primary, Backdrop, Logo, Thumb, Banner, Art, Disc)
- **People**: Associated people (Actors, Directors, Writers) and Artists for music

The comparison logic varies by category:

| Property Category | Comparison Method | Sync Strategy |
|-------------------|-------------------|---------------|
| Metadata | Semantic JSON comparison | Source-wins (overwrite local) |
| Images | SHA256 hash per image type | Source-wins (download missing/changed) |
| People | Name-based comparison | Source-wins (replace associations) |

The **Sync Missing Metadata Task** runs more frequently (default: every 6 hours) and processes all metadata sync items with a "Queued" status. For each item, it applies the source metadata to the local item using Jellyfin's provider manager APIs.

---

## Configuration

### General Settings

| Setting | Description | Default |
|---------|-------------|---------|
| Enable Metadata Sync | Master toggle for all metadata syncing functionality | Off |

### Sync Options

Each property category can be individually enabled or disabled:

| Setting | Description | Default |
|---------|-------------|---------|
| Sync Metadata | Sync titles, descriptions, ratings, genres, tags, etc. | On |
| Sync Images | Sync all image types (posters, backdrops, etc.) | On |
| Sync People | Sync actor, director, artist associations | Off |

### Library Mappings

Metadata sync reuses the same library mappings as content sync and history sync. Items are matched by translating the source file path to a local path. If an item exists at that local path, it's considered a match.

For example, if the source has `/media/movies/Film.mkv` and your mapping translates that to `/srv/jellyfin/movies/Film.mkv`, and that file exists locally, the items are matched and eligible for metadata sync.

---

## Property Categories

### Metadata

Core item properties that describe the media:

| Property | Description |
|----------|-------------|
| Name | Display title |
| OriginalTitle | Original language title |
| SortName | Title used for sorting |
| ForcedSortName | User-specified sort name |
| Overview | Description/synopsis |
| Taglines | Short promotional phrases |
| Genres | Genre classifications |
| Tags | Custom tags |
| Studios | Production studios |
| OfficialRating | Content rating (PG, R, etc.) |
| CustomRating | User-defined rating |
| CommunityRating | User ratings (stars) |
| CriticRating | Critical score |
| PremiereDate | Release/air date |
| ProductionYear | Year of production |
| EndDate | Series end date |
| ProviderIds | External IDs (IMDB, TMDB, TVDB, etc.) |

### Images

All standard Jellyfin image types:

| Image Type | Description |
|------------|-------------|
| Primary | Main poster/cover art |
| Backdrop | Background images (can have multiple) |
| Logo | Transparent logo artwork |
| Thumb | Thumbnail image |
| Banner | Wide banner image |
| Art | Additional artwork |
| Disc | Disc/media image |

For each image type, the plugin:
1. Computes SHA256 hashes for source and local images
2. Compares hashes to detect changes
3. Downloads and saves changed/missing images

### People

Associated people and their roles:

| Type | Description |
|------|-------------|
| Actor | Cast members with roles |
| Director | Directors |
| Writer | Screenwriters |
| Producer | Producers |
| Composer | Music composers |
| GuestStar | Guest appearances (TV) |
| Artist | Music artists |
| AlbumArtist | Album-level artists |

People sync replaces the local item's people associations with those from the source. Note: This creates people entries on the local server if they don't exist.

---

## Item Status and Review

### Status Cards

The Metadata Sync page displays status cards showing counts for each status:

- **Synced**: Items successfully synced with no pending changes
- **Queued**: Items with changes ready to apply on next sync task
- **Errored**: Items that failed to sync (error message shown in details)
- **Ignored**: Items you've chosen to skip

### Metadata Sync Item Table

The main table shows all metadata sync items with columns for:

- **Checkbox**: For bulk selection
- **Status**: Current sync status with color indicator
- **Item Name**: The media item title
- **Property Category**: Metadata, Images, or People
- **Library**: Source library name
- **Changes**: Summary of differences

### Item Detail Modal

Click any row to open the detail modal showing:

**Status Section**
- Current status with color indicator
- Last sync time (when this item was last successfully synced)
- Error message (if status is Errored)

**Item Section**
- Item Name: Media title
- Source Path: File location on source server
- Local Path: File location on local server

**Value Comparison**
For Metadata items, shows JSON diff of changed properties.
For Images items, shows which image types differ.
For People items, shows people list comparison.

### Managing Items

From the detail modal or using bulk actions:

- **Queue**: Mark items to sync on the next task run
- **Ignore**: Permanently skip syncing this item (will not be updated on refresh)

### Bulk Actions

Select multiple items using checkboxes, then use the header buttons:

- **Ignore**: Mark all selected items as ignored
- **Queue**: Queue all selected items for sync

### Manual Sync

- **Sync**: Manually trigger the sync task to apply queued changes immediately
- **Refresh**: Reload the item list from the database

---

## Prerequisites

Before metadata sync can work:

1. **Source server connection** must be configured and tested
2. **Library mappings** must be configured (same as content/history sync)
3. **Enable Metadata Sync** must be checked
4. At least one property category (Metadata, Images, or People) must be enabled
5. Items must exist on both servers with matching file paths

Items will only sync if:

- The source item exists and can be retrieved via API
- The local item exists at the translated file path
- The property category is enabled in settings
- The item is in "Queued" status (not Ignored)

---

## Independence from Other Sync Features

Metadata sync operates independently from content sync, history sync, and user sync. You can:

- Use only metadata sync (if you already have matching content on both servers)
- Use metadata sync with content sync (sync both files and their metadata)
- Use metadata sync with history sync (sync metadata and watch progress)
- Use all features together for complete server replication

Metadata sync shares library mappings with other sync features but maintains its own tracking database for metadata state.

---

## Technical Notes

### Item Matching

Items are matched by file path, not by Jellyfin item IDs. This means:
- Items must have the same relative path structure on both servers
- Renamed or moved files won't match until library mappings are updated
- Items without file paths (virtual items, playlists) are not supported

### Image Handling

Images are downloaded via Jellyfin's image API and saved using the provider manager:
- Original image quality is preserved
- Multiple backdrop images are all synced
- Chapter images are not currently synced

### People Handling

People sync is optional because:
- It creates new person entries on your local server
- Person images are not synced (only associations)
- Large libraries may have thousands of people entries

### Performance Considerations

For large libraries:
- Initial scan may take time to compare all items
- Consider enabling one category at a time initially
- Ignored items are skipped during refresh, improving performance
