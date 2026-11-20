# Glossary

The agreed name for every concept in this plugin. The UI, the docs, messages, and code use these words and no others.

* Use the **Term** in the UI, the docs, and messages.
* The **In Code** column names the type or member that holds it, when the code name differs.
* Never use a word from the **Not** column for the concept.
* Add a term here before it appears anywhere else. Rename it here first when it changes, then everywhere in the same commit.

## Sources

| Term | Meaning | In Code | Not |
| :--- | :--- | :--- | :--- |
| Source | Where songs come from, picked with a card on the **Download** tab | `IPlaylistSource` for an export, `QueueItem.Source` | Provider, origin |
| YouTube URL | A YouTube video or playlist link to queue | `QueueUrlRequest.Url` | Link field, address |
| Playlist export | A file the Music app or Exportify wrote, uploaded on the **Download** tab | `PlaylistImportRequest` | Playlist file, dump |
| Apple Music | Playlist and library exports from the Music app | `AppleMusicPlaylistSource`, `AppleMusicLibrarySource` | iTunes |
| Spotify | Playlist exports written by Exportify | `SpotifyPlaylistSource` | |
| Replace a song | Swaps a library song's audio for a different YouTube upload | `ReplaceSongRequest` | Redownload, overwrite |
| Replacement | A queue item that overwrites a library song's file when imported | Source `replace` | Fix, swap |

## The Queue

| Term | Meaning | In Code | Not |
| :--- | :--- | :--- | :--- |
| Queue | The songs waiting to download, downloading, downloaded, or failed, on the **Download** tab | `QueueDatabase` | List, backlog |
| Queue item | One song in the queue, drawn as a media card with its status and **Change link** | `QueueItem`, `renderQueueRow` | Job, task, entry |
| Status cards | The cards above the queue, one per status with its count, that each show only songs with that status | `QUEUE_STATES` | Count cards, tiles, filters |
| Download | Turns a queued song into an audio file in the download cache with yt-dlp | `DownloadService` | Fetch, rip |
| Match | The YouTube upload a playlist track downloads from, found when it downloads | `YouTubeMatcher` | Hit, result |
| Check match | The badge on a queue item whose match is worth a look, with its note | `MatchNote` | Warning, low confidence |
| Change link | Points a queue item at a different YouTube upload, keeping its details | `POST Queue/{id}/Url` | Edit URL, relink |
| Cancel downloads | Stops the running batch. Songs not finished stay queued | `POST Queue/Cancel` | Abort, stop all |
| Library single | A single already in the library that a queued album version replaces on import | `ReplacesFiles`, `ReplacesFileCount` | Duplicate, old file |

### Statuses

| Term | Meaning | In Code |
| :--- | :--- | :--- |
| Queued | Waiting to download | `QueueStatus.Queued` |
| Downloading | Being downloaded now | `QueueStatus.Downloading` |
| Downloaded | In the download cache, ready to import | `QueueStatus.Downloaded` |
| Imported | Filed in the library, and shown on neither tab | `QueueStatus.Imported` |
| Error | Failed, with the reason on the row | `QueueStatus.Error` |

The [public queue](../docs/PublicQueue.md) names each status for any signed in user. Its pills read **Waiting**, **Downloading**, **Downloaded**, and **Failed**.

| Status | Public queue badge | In Code |
| :--- | :--- | :--- |
| Queued | Waiting to download | `DownloadService.GetPublicQueue` |
| Downloading | Downloading | `DownloadService.GetPublicQueue` |
| Downloaded | Downloaded, not yet added | `DownloadService.GetPublicQueue` |
| Error | Could not be downloaded | `DownloadService.GetPublicQueue` |

## Importing

| Term | Meaning | In Code | Not |
| :--- | :--- | :--- | :--- |
| Import | Files downloaded songs into the music library | `LibraryService`, `POST Import` | Move, publish |
| Downloaded file | A downloaded song on the **Import** tab, before it is imported | `QueueItemDto` | Staged file, track |
| Tags | The title, artist, album, and other details written into a file | `TagUpdateRequest` | Metadata fields, ID3 |
| Bulk updates | Copies filled fields into every selected file | | Batch edit, mass edit |
| Featured artists | Artists credited with "feat." on a song | `FeaturedArtist` | Feat. Artists, guests |
| Cover | The picture embedded in a file | `HasCover`, `Downloads/{id}/Cover` | Artwork, thumbnail. **Album art** names the Settings section for generated covers |
| Generate cover | The wand that draws a cover from the album's text and colors | | Auto cover, placeholder art |
| Set cover | The button under a file's cover that picks a picture, reading **Replace** once the file has one | `createCoverPicker` | Upload art, change image |
| In library | The badge on a downloaded file whose song the library already has | `InLibrary` | Duplicate, exists |
| Duplicate | A file whose name an import would reuse in the library | `ReplaceDuplicates` | Clash, conflict |
| Catalog lookup | Fills a song's details from Apple's public catalog | `AppleCatalogClient`, `CatalogMatcher` | Metadata provider, scrape |

## Playlists

| Term | Meaning | In Code | Not |
| :--- | :--- | :--- | :--- |
| Playlist import | A Jellyfin playlist that fills as an export's songs reach the library | `PlaylistTarget` | Playlist sync, tracked playlist |
| Playlist owner | The user who owns the private playlist an import makes | `PlaylistImportRequest.UserId` | Playlist user |
| Playlist imports in progress | The list of playlist imports still filling | `GET Playlists` | Targets |

## Settings

| Term | Meaning | In Code | Not |
| :--- | :--- | :--- | :--- |
| Target library | The music library imports are filed into | `MusicLibraryId` | Destination library |
| Library path | The folder in that library imports are filed under | `MusicLibraryPath` | Root, output folder |
| Download format | Opus, MP3, or M4A | `AudioFormat` | Codec, file type |
| Album art | The Settings section that sets a generated cover's text lines and **Colors from** | `GeneratedCoverText`, `GeneratedCoverTextOrder`, `GeneratedCoverColors` | Cover settings |
| Download cache | The folder downloaded files wait in until they are imported | `QueueDatabaseProvider.GetAudioCacheDirectory` | Staging, temp |
| Cache directory override | A folder for the download cache instead of the Jellyfin data folder | `CacheDirectoryOverride` | Cache path |
| Approved users | Users who may queue downloads from the user page, set in the Approved users section of Settings | `ApprovedUserIds` | Allowed users, whitelist |
| User page | The page at `/YouTube/Download` where approved users queue links | `youtubeaudio_download_public.html` | Public download page, portal |
| Public queue | The read only queue at `/YouTube/Queue` for any signed in user | `youtubeaudio_queue_public.html`, `PublicQueueItem` | Status page, queue viewer |
| Playlist limit | The most videos an approved user can queue from one playlist link | `MaxPublicPlaylistItems` | Cap, max items |
| yt-dlp | The downloader the plugin fetches and keeps up to date | `ToolDownloads`, `YtDlpRunner` | youtube-dl, the tool |
| Deno | The JavaScript runtime yt-dlp needs for YouTube | `ToolDownloads` | Node |
| yt-dlp path override | The user's own yt-dlp, used instead of the plugin's copy | `YtDlpPath` | Custom binary |
| Update yt-dlp | Updates yt-dlp and Deno now instead of at the next check | `POST UpdateYtDlp` | Upgrade, refresh tools |

## Housekeeping

| Term | Meaning | Not |
| :--- | :--- | :--- |
| Reset queue | Clears the queue and deletes the cached files of every item not yet imported | Wipe, purge |
| Reset cache | Deletes every file in the download cache and clears the queue | Clear all, nuke |
| Server log | Jellyfin's log files | Debug log |
