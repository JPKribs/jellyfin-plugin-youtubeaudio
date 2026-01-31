# History Syncing

## Summary

History syncing enables bidirectional watch history synchronization between a source Jellyfin server and your local Jellyfin server. The plugin tracks playback data including played/unplayed status, play counts, playback positions (resume points), last played dates, and favorite status for each user's items.

Unlike content syncing which downloads media files, history syncing merges playback metadata between servers. When the same item exists on both servers with different watch history, the plugin uses intelligent two-way merge logic to combine the data: it takes the maximum play count, preserves the most recent playback position, and keeps items marked as played or favorited if either server has them that way.

History sync requires user mappings to know which source server user corresponds to which local user. It also uses library mappings to translate file paths between servers to match items. The plugin compares items by their file paths rather than relying on content sync data, making history sync fully independent from content sync.

```
Source Server                         Local Server
┌─────────────┐                       ┌─────────────────────────────┐
│             │                       │                             │
│  User A     │                       │  User B (mapped)            │
│  History    │ ──── API Scan ─────►  │  Tracking Database          │
│             │                       │  (compares source vs local) │
│             │                       │                             │
│ - Played    │                       │         ▼                   │
│ - Position  │                       │  ┌─────────────────────┐    │
│ - Count     │                       │  │ Two-Way Merge       │    │
│ - Favorite  │                       │  │ (best of both)      │    │
│             │                       │  └─────────────────────┘    │
│             │                       │         ▼                   │
│             │                       │  Apply to Local User        │
└─────────────┘                       └─────────────────────────────┘
```

---

## How it Works

When you configure history syncing, you map source server users to local users. For example, you might map "john" on the source server to "admin" on your local server. The plugin uses library mappings to translate file paths, finding the corresponding local item for each source item.

The **Refresh History Sync Table Task** runs periodically (default: every 6 hours) and performs a full scan of all mapped user/library combinations on the source server. For each item with playback data, it fetches the user's watch history and compares it against the local user's data for the same item.

For each item, the plugin creates a history sync record that tracks:

- **Source State**: The playback data on the source server (played status, play count, position, last played date, favorite)
- **Local State**: The playback data on the local server for the mapped user
- **Merged State**: The calculated "best of both" values to apply

The merge logic works as follows:

| Property | Merge Strategy |
|----------|----------------|
| Played | OR - true if either server has it played |
| Play Count | MAX - highest count between servers |
| Playback Position | Most Recent - from whichever has the later Last Played Date |
| Last Played Date | Most Recent - the later date between servers |
| Favorite | OR - true if either server has it favorited |

The **Sync Missing History Task** runs more frequently (default: every 6 hours) and processes all history items with a "Queued" status. For each item, it applies the merged playback data to the local user, updating their watch history to reflect the combined state.

After sync completes, the local user's playback data is updated, and the item's local state is refreshed to match the merged state. On subsequent scans, the plugin detects if new changes have occurred on either server and re-queues items as needed.

---

## Configuration

### General Settings

| Setting | Description | Default |
|---------|-------------|---------|
| Enable History Sync | Master toggle for all history syncing functionality | Off |

### Sync Options

Each property can be individually enabled or disabled:

| Setting | Description | Default |
|---------|-------------|---------|
| Played/Unplayed Status | Sync whether items are marked as played | On |
| Playback Position | Sync resume points for partially watched items | On |
| Play Count | Sync how many times an item has been played | On |
| Last Played Date | Sync when the item was last played | On |
| Favorites | Sync favorite/heart status | On |

### User Mappings

User mappings connect source server users to local users. Each mapping includes:

| Field | Description |
|-------|-------------|
| Source User | The user on the source server to sync from |
| Local User | The user on the local server to sync to |
| Enabled | Whether this mapping is active |

Both users must exist on their respective servers. The plugin syncs playback data from the source user's watch history to the local user.

### Library Mappings

History sync reuses the same library mappings as content sync. The mappings tell the plugin how to translate file paths between servers so it can match items. For example, a file at `/media/movies/Film.mkv` on the source might correspond to `/srv/jellyfin/movies/Film.mkv` locally.

If an item exists on the source but not locally (no matching file path), the history sync record is created but cannot be synced until the local item exists.

---

## Item Status and Review

### Status Cards

The History Sync page displays status cards showing counts for each status:

- **Synced**: Items successfully synced with no pending changes
- **Queued**: Items with changes ready to apply on next sync task
- **Errored**: Items that failed to sync (error message shown in details)
- **Ignored**: Items you've chosen to skip

### History Item Table

The main table shows all history sync items with columns for:

- **Checkbox**: For bulk selection
- **Status**: Current sync status with color indicator
- **Item Name**: The media item title
- **User**: Source user → Local user mapping

### Item Detail Modal

Click any row to open the detail modal showing:

**Status Section**
- Current status with color indicator
- Last sync time (when this item was last successfully synced)

**User Section**
- Source User: Username (ID) on the source server
- Local User: Username (ID) on the local server

**Comparison Table**
A side-by-side comparison showing Source, Local, and Merged values for:

| Property | Source | Local | Merged |
|----------|--------|-------|--------|
| Played | ✓/✗ | ✓/✗ | ✓/✗ |
| Playback Position | HH:MM:SS | HH:MM:SS | HH:MM:SS |
| Play Count | N | N | N |
| Last Played | Date/Time | Date/Time | Date/Time |
| Favorite | ✓/✗ | ✓/✗ | ✓/✗ |

The merged column shows what will be applied to the local user when synced.

**Path Section**
- Source Path: File location on source server
- Local Path: Translated file location on local server

### Managing Items

From the detail modal or using bulk actions:

- **Queue**: Mark items to sync on the next task run
- **Ignore**: Permanently skip syncing this item

### Bulk Actions

Select multiple items using checkboxes, then use the header buttons:

- **Ignore**: Mark all selected items as ignored
- **Queue**: Queue all selected items for sync

### Manual Sync

- **Sync**: Manually trigger the sync task to apply queued changes immediately
- **Refresh**: Reload the item list from the database

---

## Prerequisites

Before history sync can work:

1. **Source server connection** must be configured and tested
2. **Library mappings** must be set up (same as for content sync)
3. **User mappings** must be configured linking source users to local users
4. **Enable History Sync** must be checked

Items will only sync if:

- The source user has playback data for the item
- The file path can be translated using library mappings
- The local item exists at the translated path
- The local user exists

---

## Independence from Content Sync

History sync operates independently from content sync. You can:

- Use only history sync without content sync (useful if you already have the same media on both servers)
- Use only content sync without history sync
- Use both together

The two features share library mappings for path translation but don't depend on each other's tracking data. History sync finds local items by file path, not by referencing the content sync database.
