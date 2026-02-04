# User Syncing

## Summary

User Syncing copies user settings from a Source Jellyfin Server and applies them to mapped users on your Local Server. The plugin syncs three categories: Policy (permissions and restrictions), Configuration (playback preferences and display settings), and Profile Images. Each category can be independently enabled, and library-specific permissions are automatically translated using your Library Mappings. This is useful when you manage user accounts centrally and want consistent settings across multiple servers.

---

## Statuses

| Status | Description |
|--------|-------------|
| **Pending** | Item is awaiting initial processing (rarely seen in normal operation). |
| **Queued** | User settings differ between servers and are waiting for the next sync task to apply changes. |
| **Synced** | User settings match between servers with no pending changes. |
| **Errored** | Settings failed to sync. Check the error message for details. |
| **Ignored** | User/category has been explicitly skipped and will not be processed in future syncs. |

---

## How It Works

### Refresh Sync Table

The Refresh task scans all User Mappings to build a tracking table of user settings. For each mapped user pair, it fetches the Source User's Policy, Configuration, and Profile Image data, then compares against the Local User's current settings.

The plugin creates sync records for each enabled category, tracking Source values, Local values, and (for Policy) translated values that account for library ID differences between servers.

**Source Server APIs Used:**

| API | Purpose |
|-----|---------|
| `GET /Users/{id}` | Fetches user details including Policy and Configuration objects |
| `GET /Users/{id}/Images/Primary` | Downloads the user's profile image |
| `HEAD /Users/{id}/Images/Primary` | Gets image size without downloading (for quick comparison) |
| `GET /Users` | Lists available users for mapping configuration |

### User Setting Categories

Each category contains different types of settings:

| Category | Settings Included | Comparison Method |
|----------|-------------------|-------------------|
| **Policy** | IsAdministrator, IsDisabled, IsHidden, EnableMediaPlayback, EnableAudioPlaybackTranscoding, EnableVideoPlaybackTranscoding, EnableAllFolders, EnabledFolders, BlockedMediaFolders, EnableLiveTvAccess, EnableContentDeletion, EnableRemoteAccess, MaxParentalRating, BlockedTags, MaxActiveSessions, RemoteClientBitrateLimit, and more | JSON comparison with library ID translation |
| **Configuration** | PlayDefaultAudioTrack, SubtitleLanguagePreference, AudioLanguagePreference, SubtitleMode, DisplayMissingEpisodes, RememberAudioSelections, RememberSubtitleSelections, EnableNextEpisodeAutoPlay, HidePlayedInLatest | JSON comparison |
| **Profile Image** | User avatar image | SHA256 hash comparison |

### Library ID Translation

Policy settings that reference library IDs (like `EnabledFolders` and `BlockedMediaFolders`) are automatically translated using your Library Mappings. If the Source User has access to library `abc123` on the Source Server, and your mapping connects that to library `xyz789` on the Local Server, the plugin translates the permission accordingly.

This ensures permissions work correctly even when library IDs differ between servers—which they always do, since Jellyfin generates unique IDs per installation.

### Sync Users

The Sync task processes all Queued items by applying Source settings to Local Users. For Policy and Configuration, it updates the Local User's data directly through Jellyfin's User Manager. For Profile Images, it downloads the image from the Source Server and saves it locally.

**Local Server Internal APIs Used:**

| Service | Purpose |
|---------|---------|
| `IUserManager.GetUserById()` | Retrieves the local user object |
| `IUserManager.UpdateUserAsync()` | Saves updated Policy and Configuration |
| `IUserManager.ClearProfileImageAsync()` | Removes existing profile image before replacement |
| File System | Profile images saved to user's config directory |

### Comparison Logic

User settings are compared by serializing Policy and Configuration objects to JSON and checking for differences. The plugin excludes certain properties that shouldn't sync: `EnabledDevices` (device IDs are server-specific), `EnabledChannels` (channel IDs differ between servers), `AuthenticationProviderId`, and `PasswordResetProviderId` (security settings should remain local).

Profile images use SHA256 hash comparison. The plugin downloads both Source and Local images, computes their hashes, and only syncs if they differ. This is more accurate than size-based comparison and correctly detects when images have changed even if file sizes happen to match.

After sync completes, the Local values are updated in the tracking database. On subsequent refreshes, only users with new differences are re-queued. Be cautious with Policy sync for administrator users—syncing could grant or revoke significant permissions.
