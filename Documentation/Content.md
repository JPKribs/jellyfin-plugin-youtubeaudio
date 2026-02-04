# Content Syncing

## Summary

Content Syncing downloads media files from a Source Jellyfin Server and mirrors them on your Local Server. The plugin scans Source Libraries, compares files by path against your Local Server, and queues missing or updated content for download. Companion files like external subtitles, NFO metadata, and images are included automatically. Files removed from the Source can optionally be deleted locally. An approval workflow lets you review pending operations before they execute, and a recycling bin provides a safety net for deletions.

---

## Statuses

| Status | Description |
|--------|-------------|
| **Pending** | Item requires manual approval before processing. The `PendingType` indicates the operation: Download (new file), Replacement (updated file), or Deletion (file removed from source). |
| **Queued** | Item is approved and waiting for the next sync task to process it. |
| **Synced** | Item has been successfully downloaded and verified on the Local Server. |
| **Errored** | Item failed to download after multiple retry attempts. Check the error message for details. |
| **Ignored** | Item has been explicitly skipped and will not be processed in future syncs. |
| **Deleting** | Item is queued for deletion from the Local Server (content sync specific). |

---

## How It Works

### Refresh Sync Table

The Refresh task scans all mapped Source Libraries and builds a tracking table of content. For each item on the Source Server, it fetches the file path, size, and ETag (a change indicator). The plugin translates the Source path to a Local path using your Library Mappings and checks if the file exists locally.

New items not found locally are either Queued for download or set to Pending if approval is required. Existing items are compared by ETag and file size to detect changes—if the Source file has been updated, the item can be queued for replacement. Items in the tracking table that no longer exist on the Source Server can be marked for deletion if that setting is enabled.

**Source Server APIs Used:**

| API | Purpose |
|-----|---------|
| `GET /Items` | Fetches library items with Path, ETag, MediaSources, and DateCreated fields |
| `GET /Library/VirtualFolders` | Lists available libraries for mapping configuration |

### Sync Content

The Sync task processes all Queued items by downloading files from the Source Server. Before each download, it verifies sufficient disk space exists. Files are streamed to a temporary directory first, then moved to their final location once complete. This atomic approach prevents partial files from appearing in your library.

Companion files (subtitles, NFO files, images) are downloaded alongside the main media file when enabled. The plugin discovers companion files through the item's MediaSources data and downloads each one to the appropriate location.

**Source Server APIs Used:**

| API | Purpose |
|-----|---------|
| `GET /Items/{id}/Download` | Downloads the main media file |
| `GET /Videos/{id}/Subtitles/Stream` | Downloads external subtitle files |
| `GET /Items/{id}/File` | Downloads companion files by path |

### Local Storage

Downloaded files are saved to the Local path determined by your Library Mappings. The plugin creates any necessary subdirectories automatically. For example, if the Source has `/media/movies/Film (2024)/Film.mkv` and your mapping translates `/media/movies` to `/srv/jellyfin/movies`, the file saves to `/srv/jellyfin/movies/Film (2024)/Film.mkv`.

When deletion is enabled, files removed from the Source are deleted locally. If the Recycling Bin is configured, deleted files move there instead of being permanently removed, giving you a recovery window.

### Comparison Logic

Items are matched between servers using **file path translation**. The plugin takes the Source file path, applies your Library Mapping's root path substitution, and looks for a matching file locally. This means files must maintain the same relative path structure on both servers.

Change detection uses the ETag (based on file modification time) and file size. If either differs between Source and Local, the item is flagged for replacement. The `Detect Updated Files` setting also checks if previously-synced local files still exist and match their expected size.

After sync completes, the plugin triggers a Jellyfin library scan so new content appears immediately in your Local Server's interface.
