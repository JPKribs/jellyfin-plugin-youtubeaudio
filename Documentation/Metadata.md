# Metadata Syncing

## Summary

Metadata Syncing copies media metadata from a Source Jellyfin Server and applies it to matched items on your Local Server. The plugin syncs core metadata fields (titles, descriptions, ratings, dates), array data (genres, tags, studios), people associations (actors, directors, writers), and images (posters, backdrops, logos). Each category can be independently enabled, and items are matched by file path using your existing Library Mappings. This is useful when you curate metadata on a primary server and want secondary servers to reflect those changes.

---

## Statuses

| Status | Description |
|--------|-------------|
| **Pending** | Item is awaiting initial processing (rarely seen in normal operation). |
| **Queued** | Item has metadata differences and is waiting for the next sync task to apply changes. |
| **Synced** | Item metadata matches between servers with no pending changes. |
| **Errored** | Item failed to sync. Check the error message for details. |
| **Ignored** | Item has been explicitly skipped and will not be processed in future syncs. |

---

## How It Works

### Refresh Sync Table

The Refresh task scans all mapped Libraries to build a metadata tracking table. For each item on the Source Server, it fetches extended metadata fields and translates the file path to find the corresponding Local item. If a match is found, the plugin compares metadata values between servers and creates sync records for any differences.

The plugin tracks all enabled categories in a single sync record per item, with separate flags indicating which categories have changes. This allows you to see at a glance whether an item needs Metadata, Images, People, or any combination synced.

**Source Server APIs Used:**

| API | Purpose |
|-----|---------|
| `GET /Items` | Fetches items with extended fields: Overview, Genres, Tags, Studios, People, ProviderIds, OriginalTitle, SortName, Taglines, Settings, CustomRating |
| `GET /Items/{id}/Images` | Retrieves image info including size, dimensions, and type for each image |
| `GET /Library/VirtualFolders` | Lists available libraries for mapping configuration |

### Metadata Categories

Each category is compared and synced independently:

| Category | Fields Included | Comparison Method |
|----------|-----------------|-------------------|
| **Metadata** | Name, OriginalTitle, SortName, ForcedSortName, Overview, Tagline, OfficialRating, CustomRating, CommunityRating, CriticRating, PremiereDate, EndDate, ProductionYear, ProviderIds, IndexNumber, ParentIndexNumber, PreferredMetadataLanguage, PreferredMetadataCountryCode, AspectRatio, Video3DFormat, LockedFields, LockData | JSON comparison of field values |
| **Genres** | Genre array | Sorted array comparison |
| **Tags** | Tag array | Sorted array comparison |
| **Studios** | Studio names | Sorted array comparison (names only, not IDs) |
| **People** | Name, Role, Type for each person | Array comparison by name (not by GUID, allowing cross-server sync) |
| **Images** | Primary, Backdrop, Logo, Thumb, Banner, Art, Disc | Hash-based comparison using image size and dimensions |

### Sync Metadata

The Sync task processes all Queued items by applying Source metadata to Local items. For each category with changes, it updates the Local item through Jellyfin's provider manager and library manager APIs. Images are downloaded from the Source Server and saved locally.

**Local Server Internal APIs Used:**

| Service | Purpose |
|---------|---------|
| `ILibraryManager.FindByPath()` | Locates local items by translated file path |
| `ILibraryManager.UpdateItemAsync()` | Saves updated metadata fields to the item |
| `ILibraryManager.UpdatePeopleAsync()` | Updates people associations for an item |
| `IProviderManager.SaveImage()` | Downloads and saves images from Source Server |

### Comparison Logic

Items are matched between servers using **file path translation**. The plugin takes the Source item's file path, applies your Library Mapping's root path substitution, and looks for a matching item locally using `ILibraryManager.FindByPath()`.

For metadata fields, the plugin serializes both Source and Local values to JSON and compares them semantically. Array fields (Genres, Tags, Studios) are sorted before comparison to ignore ordering differences. People are compared by Name and Type rather than GUID, since person IDs differ between servers.

For images, the plugin fetches image info from the Source Server including size and dimensions. It computes a hash based on these values to detect changes without downloading the actual image data during refresh. Only during sync are changed images downloaded and applied.

After sync completes, the plugin updates the item's Local values in the tracking database. On subsequent refreshes, only items with new differences are re-queued. Items can also be manually Ignored if you want to preserve Local metadata for specific items.
