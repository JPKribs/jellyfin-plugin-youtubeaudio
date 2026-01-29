# Content Syncing - Technical Documentation

This document provides a detailed technical breakdown of how content syncing works in the Server Sync plugin.

## Overview

Content syncing is a two-phase process:

1. **Refresh Phase** (`UpdateSyncTablesTask`): Scans the source server and updates the local tracking database
2. **Download Phase** (`DownloadMissingContentTask`): Downloads files for items in the queue

Both phases run as scheduled Jellyfin tasks and can also be triggered manually from the plugin UI.

## Architecture

### Components

| Component | Purpose |
|-----------|---------|
| `SourceServerClient` | Communicates with the source Jellyfin server via API |
| `SyncDatabase` | SQLite database tracking all sync items and their states |
| `UpdateSyncTablesTask` | Scheduled task that scans source and updates tracking |
| `DownloadMissingContentTask` | Scheduled task that downloads queued items |
| `RecyclingBinService` | Handles soft-delete operations for replaced/deleted files |
| `ConfigurationController` | API endpoints for the plugin UI |

### Data Flow

```
Source Server                    Local Server
     │                                │
     │  ── API Request ──────────►    │
     │     (GetItems)                 │
     │                                │
     │  ◄── Item List ────────────    │
     │                                │
     │                          ┌─────┴─────┐
     │                          │  Compare  │
     │                          │  with DB  │
     │                          └─────┬─────┘
     │                                │
     │                          ┌─────┴─────┐
     │                          │  Update   │
     │                          │  Statuses │
     │                          └─────┬─────┘
     │                                │
     │  ── Download Request ─────►    │
     │     (for Queued items)         │
     │                                │
     │  ◄── File Stream ──────────    │
     │                                │
     │                          ┌─────┴─────┐
     │                          │   Save    │
     │                          │   File    │
     │                          └───────────┘
```

## API Communication

### Source Server APIs Used

The plugin uses the Jellyfin SDK to communicate with the source server:

| Endpoint | Purpose |
|----------|---------|
| `GET /System/Info/Public` | Test connection, get server name/ID |
| `GET /Library/VirtualFolders` | List available libraries |
| `GET /Items` | Fetch items from a library (paginated) |
| `GET /Items/{id}/File` | Download the actual media file |

### Authentication

All API requests use an API key passed in the `Authorization` header:
```
Authorization: MediaBrowser Token="{api_key}"
```

### Pagination

Items are fetched in batches of 100 to avoid memory issues with large libraries:

```csharp
var result = await client.GetItemsAsync(
    parentId: libraryId,
    fields: [ItemFields.Path, ItemFields.DateCreated, ItemFields.MediaSources, ItemFields.Etag],
    includeItemTypes: [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Audio, BaseItemKind.Video],
    startIndex: startIndex,
    limit: 100
);
```

## Sync Item States

Each tracked item has a status that determines how it's processed:

| Status | Value | Description |
|--------|-------|-------------|
| `Pending` | 0 | New item awaiting approval to download |
| `Queued` | 1 | Approved and waiting to be downloaded |
| `Synced` | 2 | Successfully downloaded and verified |
| `Errored` | 3 | Download failed (will retry up to 3 times) |
| `Ignored` | 4 | User chose to never sync this item |
| `PendingDeletion` | 5 | Item removed from source, awaiting deletion approval |
| `PendingReplacement` | 6 | Source changed, awaiting approval to replace |

## Refresh Phase (UpdateSyncTablesTask)

The refresh task runs every 6 hours by default and performs the following:

### 1. Fetch Existing Items

Load all currently tracked items from the database for the library being processed.

### 2. Scan Source Library

Iterate through all items on the source server in batches.

### 3. Process Each Item

For each item found on the source:

```
ITEM FOUND ON SOURCE
│
├─► Item exists in tracking DB?
│   │
│   ├─► YES → ProcessExistingItem()
│   │
│   └─► NO → ProcessNewItem()
```

#### ProcessNewItem Logic

```
NEW ITEM
│
├─► Is DownloadNewContentMode = DISABLED?
│   └─► YES → Don't track (return)
│
├─► Does local file exist with matching size?
│   └─► YES → Status = SYNCED
│
├─► Is DownloadNewContentMode = REQUIRE_APPROVAL?
│   │
│   ├─► YES → Status = PENDING
│   │
│   └─► NO → Status = QUEUED
```

#### ProcessExistingItem Logic

```
EXISTING ITEM
│
├─► Status = IGNORED?
│   └─► YES → No action
│
├─► Status = PENDING_DELETION?
│   └─► YES → Restore (Status = QUEUED)
│
├─► Status = PENDING or PENDING_REPLACEMENT?
│   └─► YES → Update metadata only
│
├─► Source changed? (size, path, or ETag)
│   │
│   ├─► YES:
│   │   │
│   │   ├─► ReplaceExistingContentMode = DISABLED → Update metadata only
│   │   │
│   │   ├─► ReplaceExistingContentMode = REQUIRE_APPROVAL → Status = PENDING_REPLACEMENT
│   │   │
│   │   └─► ReplaceExistingContentMode = ENABLED → Status = QUEUED
│   │
│   └─► NO → Check local file integrity (if DetectUpdatedFiles enabled)
```

### 4. Process Missing Items

After scanning all source items, check for items in the database that weren't seen:

```
ITEM NOT FOUND ON SOURCE
│
├─► Status = IGNORED or PENDING_DELETION?
│   └─► YES → No action
│
├─► Status != SYNCED? (Pending, Queued, Errored, etc.)
│   └─► YES → Delete from tracking DB only
│
├─► DeleteMissingContentMode = DISABLED?
│   └─► YES → No action
│
└─► Status = PENDING_DELETION
    (awaits approval or auto-deletion based on mode)
```

## Download Phase (DownloadMissingContentTask)

The download task runs every hour by default and processes items with `Queued` status.

### Download Process

```
FOR EACH QUEUED ITEM:
│
├─► Pre-download validation:
│   │
│   ├─► Local file exists with matching size?
│   │   └─► YES → Status = SYNCED, skip download
│   │
│   ├─► Sufficient disk space?
│   │   └─► NO → Skip, log warning
│   │
│   └─► Library mapping still valid?
│       └─► NO → Skip, log warning
│
├─► If replacing existing file and recycling bin enabled:
│   └─► Move existing file to recycling bin
│
├─► Download to temp file:
│   │
│   ├─► Create temp directory if needed
│   │
│   ├─► Stream file from source with bandwidth throttling
│   │
│   └─► Verify downloaded size matches expected
│
├─► Move temp file to final location:
│   │
│   ├─► Create target directory if needed
│   │
│   └─► Atomic move from temp to final path
│
├─► Download companion files (if enabled):
│   │
│   └─► Subtitles, NFO, images
│
└─► Update status:
    │
    ├─► Success → Status = SYNCED
    │
    └─► Failure → Status = ERRORED, increment retry count
```

### Bandwidth Throttling

Downloads respect the configured bandwidth limits:

```csharp
var maxBytesPerSecond = config.GetEffectiveDownloadSpeedBytes();

if (maxBytesPerSecond > 0)
{
    // Throttled read
    var bytesToRead = Math.Min(buffer.Length, maxBytesPerSecond);
    var bytesRead = await stream.ReadAsync(buffer, 0, bytesToRead);
    await Task.Delay(1000); // Wait 1 second between reads
}
```

The `GetEffectiveDownloadSpeedBytes()` method checks if bandwidth scheduling is enabled and returns the appropriate speed based on current time.

### Retry Logic

Failed downloads are retried up to 3 times:

```csharp
if (item.RetryCount < MaxRetries)
{
    item.RetryCount++;
    item.Status = SyncStatus.Errored;
    // Will be retried on next task run
}
else
{
    item.Status = SyncStatus.Errored;
    item.ErrorMessage = "Max retries exceeded: " + error;
    // Requires manual intervention
}
```

## Change Detection

The plugin uses multiple methods to detect when content has changed:

### ETag (Primary)

The ETag is derived from the source file's `DateModified` timestamp. This is the most reliable indicator of actual file changes.

```csharp
var sourceChanged = sourceETag != null && existingItem.SourceETag != sourceETag;
```

### File Size

Compared between source and local. Detects corruption or incomplete downloads.

```csharp
var sizeChanged = existingItem.SourceSize != sourceSize;
```

### File Path

Detects renamed or moved files on the source server.

```csharp
var pathChanged = existingItem.SourcePath != sourcePath;
```

## Path Translation

Paths are translated between source and local servers using the library mapping configuration:

```csharp
private static string TranslatePath(string sourcePath, string sourceRoot, string localRoot)
{
    // Example:
    // sourcePath: /media/movies/Movie (2024)/Movie.mkv
    // sourceRoot: /media/movies
    // localRoot:  /srv/jellyfin/movies
    // Result:     /srv/jellyfin/movies/Movie (2024)/Movie.mkv

    if (sourcePath.StartsWith(sourceRoot))
    {
        var relativePath = sourcePath.Substring(sourceRoot.Length);
        return localRoot + relativePath;
    }

    return sourcePath;
}
```

## Deletion Process

When items are approved for deletion (or auto-deletion is enabled):

### With Recycling Bin

```
DELETE ITEM (Recycling Bin Enabled)
│
├─► Generate recycled filename:
│   │   Format: path.to.file_2024-01-15_14-30-45.mkv
│   │
│   └─► Encodes original path and timestamp
│
├─► Move file to recycling bin
│
├─► Move companion files to recycling bin
│
└─► Remove from Jellyfin library
    (DeleteFileLocation = false)
```

### Without Recycling Bin

```
DELETE ITEM (Permanent)
│
├─► Delete via Jellyfin Library Manager
│   (DeleteFileLocation = true)
│
└─► Library manager handles:
    ├─► File deletion
    ├─► Companion file cleanup
    └─► Database cleanup
```

### Recycling Bin Cleanup

The `EmptyRecyclingBinTask` runs daily and permanently deletes files older than the retention period:

```csharp
var cutoffTime = DateTime.UtcNow.AddDays(-retentionDays);

foreach (var file in Directory.GetFiles(recyclingBinPath))
{
    var fileTime = ExtractTimestampFromFileName(file) ?? fileInfo.LastWriteTimeUtc;

    if (fileTime < cutoffTime)
    {
        File.Delete(file);
    }
}
```

## Database Schema

The sync database uses SQLite with the following schema:

```sql
CREATE TABLE SyncItems (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    SourceLibraryId TEXT NOT NULL,
    LocalLibraryId TEXT NOT NULL,
    SourceItemId TEXT NOT NULL UNIQUE,
    SourcePath TEXT NOT NULL,
    LocalPath TEXT,
    LocalItemId TEXT,
    SourceSize INTEGER NOT NULL,
    SourceCreateDate TEXT NOT NULL,
    SourceModifyDate TEXT NOT NULL,
    SourceETag TEXT,
    Status INTEGER NOT NULL,
    StatusDate TEXT NOT NULL,
    LastSyncTime TEXT,
    ErrorMessage TEXT,
    RetryCount INTEGER DEFAULT 0
);

CREATE INDEX idx_status ON SyncItems(Status);
CREATE INDEX idx_source_library ON SyncItems(SourceLibraryId);
```

## Scheduled Tasks

| Task | Key | Default Interval | Purpose |
|------|-----|------------------|---------|
| Update Sync Tables | `ServerSyncUpdateTables` | 6 hours | Scan source, update tracking |
| Download Content | `ServerSyncDownloadContent` | 1 hour | Download queued items |
| Cleanup Temp Files | `ServerSyncCleanupTempFiles` | 24 hours | Remove orphaned temp files |
| Empty Recycling Bin | `ServerSyncEmptyRecyclingBin` | 24 hours | Permanently delete expired files |

## Error Handling

### Network Errors

Transient network errors increment the retry count. After 3 failures, the item remains in `Errored` status for manual review.

### Disk Space Errors

Downloads are skipped (not failed) when disk space is below the minimum threshold. The item remains `Queued` for the next attempt.

### File Permission Errors

Logged and treated as errors. May require manual intervention to fix permissions.

### API Errors

Connection failures during the refresh phase abort the current library scan but don't affect already-tracked items.
