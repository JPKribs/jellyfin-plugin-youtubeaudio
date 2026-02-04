# History Syncing

## Summary

History Syncing copies watch history from a Source Jellyfin Server and applies it to mapped users on your Local Server. The plugin tracks played status, play counts, playback positions (resume points), last played dates, and favorite status. Unlike a simple one-way copy, History Sync uses intelligent merge logic—play counts take the maximum value, while played status, position, and last played date come from whichever server has the most recent activity. Favorite status always comes from the Source Server. Items are matched by file path, so History Sync works independently of Content Sync.

---

## Statuses

| Status | Description |
|--------|-------------|
| **Pending** | Item is awaiting initial processing (rarely seen in normal operation). |
| **Queued** | Item has history differences and is waiting for the next sync task to apply changes. |
| **Synced** | Item history matches between servers with no pending changes. |
| **Errored** | Item failed to sync. Check the error message for details. |
| **Ignored** | Item has been explicitly skipped and will not be processed in future syncs. |

---

## How It Works

### Refresh Sync Table

The Refresh task scans all mapped User and Library combinations to build a history tracking table. For each Source User, it fetches their playback data for items in each mapped library. The plugin then translates file paths using Library Mappings to find the corresponding Local item and retrieves the Local User's playback data for comparison.

For each item, the plugin creates a sync record containing Source values, Local values, and Merged values. The merge logic determines what will be applied to the Local User during sync.

**Source Server APIs Used:**

| API | Purpose |
|-----|---------|
| `GET /Items` | Fetches library items with UserId parameter to include UserData (playback info) |
| `GET /Users` | Lists available users for mapping configuration |

### Merge Logic

History Sync doesn't simply overwrite Local data with Source data. Instead, it intelligently merges values to preserve the best information from both servers:

| Field | Merge Strategy |
|-------|----------------|
| **Played Status** | Uses the value from whichever server has the more recent Last Played Date |
| **Play Count** | Takes the maximum value between Source and Local |
| **Playback Position** | Uses the position from whichever server has the more recent Last Played Date |
| **Last Played Date** | Takes the more recent date between Source and Local |
| **Favorite Status** | Always uses the Source Server value (source-wins) |

This approach ensures you don't lose watch progress from either server. If you watched something on your Local Server more recently, that position is preserved. If the Source has a higher play count, that count is used.

### Sync History

The Sync task processes all Queued items by applying the merged playback data to Local Users. For each item, it updates the Local User's data through Jellyfin's internal UserDataManager. This directly modifies the database rather than going through the API, ensuring immediate effect.

**Local Server Internal APIs Used:**

| Service | Purpose |
|---------|---------|
| `IUserDataManager.GetUserData()` | Retrieves current playback data for a user/item |
| `IUserDataManager.SaveUserData()` | Saves updated playback data with UserDataSaveReason.UpdateUserData |
| `ILibraryManager.FindByPath()` | Locates local items by translated file path |

### Comparison Logic

Items are matched between servers using **file path translation**, the same approach used by Content Sync and Metadata Sync. The plugin takes the Source item's file path, applies your Library Mapping's root path substitution, and looks for a matching item locally.

After applying merged values, the sync record's Local values are updated to reflect the new state. On subsequent refreshes, the plugin detects if new changes have occurred on either server and re-queues items as needed. Items already in sync (where Source, Local, and Merged values all match) remain in Synced status.

History Sync requires both Library Mappings (for path translation) and User Mappings (to know which Source User corresponds to which Local User). Items will only sync if the Local item exists—if you're using Content Sync, wait for files to download before expecting history to sync.
