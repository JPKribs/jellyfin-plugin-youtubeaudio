# User Syncing

## Summary

User syncing enables one-way synchronization of user settings from a source Jellyfin server to your local Jellyfin server. The plugin syncs user policies (permissions and restrictions), user configuration (preferences), and profile images from mapped source users to their corresponding local users.

Unlike content syncing which downloads media files, user syncing copies user account settings. This is useful when you want users on your local server to have the same permissions, preferences, and profile appearance as they do on the source server. For example, if you manage user policies centrally on one server, user sync can replicate those settings to secondary servers.

User sync operates at the property category level, creating separate sync records for Policy, Configuration, and ProfileImage. This allows you to selectively sync only what you need and review changes before applying them. Each property category can be individually enabled or disabled in the plugin settings.

```
Source Server                         Local Server
┌─────────────┐                       ┌─────────────────────────────┐
│             │                       │                             │
│  User A     │                       │  User B (mapped)            │
│  Settings   │ ──── API Scan ─────►  │  Tracking Database          │
│             │                       │  (compares source vs local) │
│             │                       │                             │
│ - Policy    │                       │         ▼                   │
│ - Config    │                       │  ┌─────────────────────┐    │
│ - Image     │                       │  │ Per-Property Sync   │    │
│             │                       │  │ (Policy, Config,    │    │
│             │                       │  │  ProfileImage)      │    │
│             │                       │  └─────────────────────┘    │
│             │                       │         ▼                   │
│             │                       │  Apply to Local User        │
└─────────────┘                       └─────────────────────────────┘
```

---

## How it Works

When you configure user syncing, you map source server users to local users. For example, you might map "john" on the source server to "john" on your local server. The plugin then tracks and syncs the settings for each mapped user pair.

The **Refresh User Sync Table Task** runs periodically (default: every 6 hours) and performs a scan of all enabled user mappings. For each mapping, it fetches the source user's Policy, Configuration, and ProfileImage data from the source server and compares them against the local user's current settings.

For each property category, the plugin creates a sync record that tracks:

- **Source Value**: The setting on the source server
- **Local Value**: The current setting on the local server
- **Merged Value**: The value that will be applied (source-wins for most properties, with special handling for library-specific policies)

The comparison logic varies by property category:

| Property Category | Comparison Method | Merge Strategy |
|-------------------|-------------------|----------------|
| Policy | Semantic JSON comparison | Source-wins with library ID translation |
| Configuration | Semantic JSON comparison | Source-wins (direct copy) |
| ProfileImage | SHA256 hash comparison | Source-wins (download and replace) |

For **Policy** settings, the plugin performs intelligent merging. Library-specific properties like `EnabledFolders` and `BlockedMediaFolders` are translated using your library mappings, converting source library IDs to their corresponding local library IDs. This ensures permissions work correctly even when library IDs differ between servers.

For **ProfileImage**, the plugin computes SHA256 hashes of both the source and local images to accurately detect changes. This is more reliable than size-based comparison and handles cases where images have the same size but different content.

The **Sync Missing Users Task** runs more frequently (default: every 6 hours) and processes all user sync items with a "Queued" status. For each item, it applies the merged value to the local user, updating their policy, configuration, or profile image as appropriate.

---

## Configuration

### General Settings

| Setting | Description | Default |
|---------|-------------|---------|
| Enable User Sync | Master toggle for all user syncing functionality | Off |

### Sync Options

Each property category can be individually enabled or disabled:

| Setting | Description | Default |
|---------|-------------|---------|
| Sync Policy | Sync user permissions and restrictions | On |
| Sync Configuration | Sync user preferences | On |
| Sync Profile Image | Sync user profile pictures | On |

### User Mappings

User mappings connect source server users to local users. Each mapping includes:

| Field | Description |
|-------|-------------|
| Source User | The user on the source server to sync from |
| Local User | The user on the local server to sync to |
| Enabled | Whether this mapping is active |

Both users must exist on their respective servers. The plugin syncs settings from the source user to the local user.

### Library Mappings (for Policy Translation)

User sync reuses the same library mappings as content sync. For Policy settings, the mappings translate library-specific permissions. If the source user has access to library "Movies" with ID `abc123` on the source, and your library mapping connects that to your local "Movies" library with ID `xyz789`, the plugin translates the permission accordingly.

---

## Property Categories

### Policy

User policies control permissions and restrictions. Synced properties include:

- **Access Controls**: `IsAdministrator`, `IsDisabled`, `IsHidden`
- **Playback Permissions**: `EnableMediaPlayback`, `EnableAudioPlaybackTranscoding`, `EnableVideoPlaybackTranscoding`, `EnablePlaybackRemuxing`
- **Library Access**: `EnableAllFolders`, `EnabledFolders`, `BlockedMediaFolders`, `BlockedChannels`
- **Feature Access**: `EnableLiveTvAccess`, `EnableLiveTvManagement`, `EnableContentDeletion`, `EnableContentDownloading`
- **Remote Access**: `EnableRemoteAccess`, `EnableRemoteControlOfOtherUsers`, `EnableSharedDeviceControl`
- **Sync Controls**: `EnableSyncTranscoding`, `SyncPlayAccess`, `EnablePublicSharing`
- **Restrictions**: `MaxParentalRating`, `BlockedTags`, `BlockUnratedItems`, `InvalidLoginAttemptCount`, `LoginAttemptsBeforeLockout`
- **Session Limits**: `MaxActiveSessions`, `RemoteClientBitrateLimit`
- **Device Access**: `EnableAllDevices`, `EnabledDevices`

Library-specific properties (`EnabledFolders`, `BlockedMediaFolders`) are automatically translated using your library mappings.

### Configuration

User configuration stores preferences. Synced properties include:

- **Playback Settings**: `PlayDefaultAudioTrack`, `SubtitleLanguagePreference`, `AudioLanguagePreference`, `SubtitleMode`
- **Display Settings**: `DisplayMissingEpisodes`, `GroupedFolders`, `RememberAudioSelections`, `RememberSubtitleSelections`
- **Experience Settings**: `EnableNextEpisodeAutoPlay`, `EnableLocalPassword`, `HidePlayedInLatest`
- **Ordering Preferences**: `OrderedViews`, `LatestItemsExcludes`, `MyMediaExcludes`

### Profile Image

Profile images are the user's avatar picture. The plugin:

1. Computes SHA256 hashes for both source and local images
2. Compares hashes to detect actual content differences
3. Downloads and applies the source image if different
4. Clears the local image if the source user has no image

Hash-based comparison is more accurate than size comparison and correctly detects when images have changed even if their file sizes happen to match.

---

## Item Status and Review

### Status Cards

The Users page displays status cards showing counts for each status:

- **Synced**: Items successfully synced with no pending changes
- **Queued**: Items with changes ready to apply on next sync task
- **Errored**: Items that failed to sync (error message shown in details)
- **Ignored**: Items you've chosen to skip

### User Sync Item Table

The main table shows all user sync items with columns for:

- **Checkbox**: For bulk selection
- **Status**: Current sync status with color indicator
- **Property Category**: Policy, Configuration, or ProfileImage
- **User**: Source user → Local user mapping
- **Changes**: Summary of differences (e.g., "3 differences" or image size)

### Item Detail Modal

Click any row to open the detail modal showing:

**Status Section**
- Current status with color indicator
- Last sync time (when this item was last successfully synced)
- Error message (if status is Errored)

**User Section**
- Source User: Username (ID) on the source server
- Local User: Username (ID) on the local server

**Value Comparison**
For Policy and Configuration items, shows the full JSON values:
- Source Value: Settings on the source server
- Local Value: Current settings on the local server
- Merged Value: What will be applied when synced

For ProfileImage items, shows:
- Source: Image size and hash preview
- Local: Image size and hash preview

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

Before user sync can work:

1. **Source server connection** must be configured and tested
2. **User mappings** must be configured linking source users to local users
3. **Library mappings** should be configured for proper Policy translation
4. **Enable User Sync** must be checked
5. At least one property category (Policy, Configuration, or ProfileImage) must be enabled

Items will only sync if:

- The source user exists and can be retrieved via API
- The local user exists
- The property category is enabled in settings
- The item is in "Queued" status

---

## Independence from Other Sync Features

User sync operates independently from content sync and history sync. You can:

- Use only user sync without content or history sync
- Use user sync alongside content sync (useful for replicating a complete user experience)
- Use user sync with history sync (sync both user settings and watch progress)
- Use all three together for complete server replication

User sync shares library mappings with other sync features but maintains its own tracking database for user settings. Changes to user settings on the source server are detected independently of media changes.

---

## Security Considerations

User sync copies sensitive user data:

- **Policies** control what users can access and do on your server
- **Administrator status** can grant full server control
- **Profile images** may contain personal photos

Ensure you trust the source server and review policy changes before applying them, especially for administrative users. Consider using the approval workflow (checking queued items before syncing) for sensitive users.
