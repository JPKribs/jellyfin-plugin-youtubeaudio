# Content Syncing

## Summary

Content syncing enables one-way media synchronization from a source Jellyfin server to your local Jellyfin server. The plugin runs on your local (destination) server and pulls content from a remote source server using API key authentication.

The sync process works in two phases. First, a refresh task scans the source server's libraries and compares each item against your local tracking database. New items, changed items, and items that no longer exist on the source are identified and categorized. Second, a download task processes any items that have been queued for download, streaming the files from the source server to your local storage with optional bandwidth throttling.

Each item is tracked individually with its own status. Items can be automatically queued for download, require manual approval, or be disabled entirely depending on your configuration. The plugin supports three operation types: downloading new content, replacing existing content when the source version changes, and deleting local content when it's removed from the source. Each operation type has its own approval setting, giving you fine-grained control over what happens automatically versus what requires your review.

The plugin also handles companion files (subtitles, NFO metadata files, images) alongside the main media files. When enabled, these are downloaded together with the primary media file. A recycling bin feature allows replaced or deleted files to be soft-deleted first, giving you a recovery window before permanent deletion.

```
Source Server                         Local Server
┌─────────────┐                       ┌─────────────────────────────┐
│             │                       │                             │
│  Libraries  │ ──── API Scan ─────►  │  Tracking Database          │
│             │                       │  (compares source vs local) │
│             │                       │                             │
│             │                       │         ▼                   │
│             │                       │  ┌─────────────────────┐    │
│             │                       │  │ Queued / Pending    │    │
│             │                       │  └─────────────────────┘    │
│             │                       │         ▼                   │
│  Files      │ ◄── Download ───────  │  Download Task              │
│             │    (throttled)        │  (processes queue)          │
│             │                       │         ▼                   │
│             │                       │  Local Storage              │
└─────────────┘                       └─────────────────────────────┘
```

---

## How it Works

When you configure content syncing, you map source server libraries to local paths on your server. For example, you might map the source server's "Movies" library (located at `/media/movies`) to your local path `/srv/jellyfin/movies`. The plugin uses this mapping to translate file paths between servers.

The **Refresh Task** runs periodically (default: every 6 hours) and performs a full scan of all mapped libraries on the source server. For each item found, it fetches metadata including the file path, size, and ETag (a change indicator based on the file's modification date). The plugin then compares this against its tracking database to determine what action is needed.

For new items not in the database, the plugin checks if a local file already exists at the expected path with a matching file size. If so, it marks the item as already synced. Otherwise, it either queues the item for download or marks it as pending approval, depending on your "Download New Content" setting.

For existing tracked items, the plugin checks whether the source file has changed by comparing the ETag, file size, and path. If changes are detected and you have "Replace Existing Content" enabled or set to require approval, the item is queued or marked pending accordingly. If "Detect Updated Files" is enabled, the plugin also verifies that previously synced local files still exist and match the expected size.

For items in the database that no longer exist on the source, the plugin can mark them for deletion if you have "Delete Missing Content" enabled. These go through the same approval workflow as other operations.

The **Download Task** runs more frequently (default: every hour) and processes all items with a "Queued" status. For each item, it validates that sufficient disk space exists, streams the file from the source server to a temporary location, then moves it to the final destination. If bandwidth throttling is configured, downloads respect the speed limit. Failed downloads are retried up to 3 times before being marked as errored.

After downloads complete, the plugin triggers a Jellyfin library refresh so new content appears in your server immediately.

---

## Configuration

### General Settings

| Setting | Description | Default |
|---------|-------------|---------|
| Enable Content Sync | Master toggle for all content syncing functionality | Off |
| Include Companion Files | Download subtitles, NFO files, and images alongside media | On |
| Detect Updated Files | Re-queue files if local copy is missing or size mismatches | On |

### Download Settings

| Setting | Description | Default |
|---------|-------------|---------|
| Max Concurrent Downloads | Number of simultaneous downloads (1-10) | 2 |
| Max Download Speed | Bandwidth limit (0 = unlimited) | 0 (unlimited) |
| Download Speed Unit | Unit for speed limit (KB/s, MB/s, GB/s) | MB/s |
| Minimum Free Disk Space | Stop downloads when disk space falls below this (GB) | 10 GB |
| Max Retry Count | Times to retry failed downloads before giving up | 3 |

### Bandwidth Scheduling

| Setting | Description | Default |
|---------|-------------|---------|
| Enable Bandwidth Scheduling | Use different speed during scheduled hours | Off |
| Scheduled Start Hour | Hour (0-23) when scheduled speed begins | 0 (midnight) |
| Scheduled End Hour | Hour (0-24) when scheduled speed ends | 6 (6 AM) |
| Scheduled Download Speed | Speed during scheduled hours (0 = unlimited) | 0 (unlimited) |
| Scheduled Speed Unit | Unit for scheduled speed | MB/s |

### Approval Modes

Each operation type can be set to one of three modes:

| Mode | Behavior |
|------|----------|
| Enabled | Operations happen automatically without approval |
| Require Approval | Operations are queued as "Pending" and require manual approval |
| Disabled | Operations are not performed |

| Setting | Description | Default |
|---------|-------------|---------|
| Download New Content | How to handle items on source that don't exist locally | Enabled |
| Replace Existing Content | How to handle items that have changed on source | Enabled |
| Delete Missing Content | How to handle items removed from source | Disabled |

### Recycling Bin

| Setting | Description | Default |
|---------|-------------|---------|
| Enable Recycling Bin | Soft-delete files instead of permanent deletion | Off |
| Recycling Bin Path | Directory where deleted files are moved | (none) |
| Retention Days | Days to keep files before permanent deletion | 7 |

### Library Mappings

Library mappings connect source server libraries to local storage paths. Each mapping includes:

| Field | Description |
|-------|-------------|
| Source Library | The library on the source server to sync from |
| Local Root Path | The local directory where files should be saved |
| Enabled | Whether this mapping is active |

The plugin automatically translates paths between servers. For example, if the source has a file at `/media/movies/Film (2024)/Film.mkv` and your mapping specifies source root `/media/movies` with local root `/srv/jellyfin/movies`, the file will be saved to `/srv/jellyfin/movies/Film (2024)/Film.mkv`.

---

## Approval and Pending Items

When an operation mode is set to "Require Approval", items enter a pending state instead of being processed automatically. This gives you the opportunity to review what will happen before any files are downloaded, replaced, or deleted.

### Status Cards

The Sync Items page displays status cards at the top showing counts for each item status:

- **Synced**: Items that have been successfully downloaded and verified
- **Queued**: Items approved and waiting for the next download task
- **Errored**: Items that failed to download (will retry automatically)
- **Ignored**: Items you've chosen to never sync
- **Pending Download**: New items awaiting approval to download
- **Pending Replace**: Changed items awaiting approval to replace
- **Pending Delete**: Removed items awaiting approval to delete

The pending status cards only appear when you have items in those states.

### Filtering Items

Use the status dropdown filter to view items by status. Click on any status card to quickly filter to that status. The "Select All" checkbox and bulk action buttons let you process multiple items at once.

### Item Detail Modal

Click on any item row to open the detail modal. The modal displays:

- **Status**: Current status with color indicator
- **Error**: If errored, the error message (useful for troubleshooting)
- **Retry Count**: Number of download attempts if errored
- **Size**: File size
- **Source Path**: Location on the source server
- **Local Path**: Where the file will be saved locally
- **Last Sync**: When the item was last successfully synced (if applicable)
- **Companion Files**: List of associated files (subtitles, etc.)

### Approving Items

From the detail modal or using bulk actions, you can:

- **Queue**: Approve the item for download/replacement/deletion. The item moves to "Queued" status and will be processed on the next download task run.
- **Ignore**: Mark the item to never sync. The item will be skipped on future scans.
- **Delete**: Remove the local file (for synced items only). This deletes from your local server, not the source.

For pending deletions specifically, clicking "Queue" approves the deletion. If the recycling bin is enabled, the file will be moved there instead of permanently deleted.

### Bulk Actions

Select multiple items using the checkboxes, then use the header buttons:

- **Ignore**: Mark all selected items as ignored
- **Queue**: Approve all selected items
- **Delete**: Delete all selected local files (synced items only)

### Manual Sync

Use the "Sync" button to manually trigger a download task immediately instead of waiting for the scheduled run. Use "Refresh" to reload the item list from the database. Use "Retry Errors" to reset all errored items back to queued status for another attempt.
