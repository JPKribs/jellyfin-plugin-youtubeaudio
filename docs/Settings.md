# Settings

Configure the plugin before your first download.

![Settings](screenshots/Settings.png)

## Music Library

Select your target Jellyfin music library and confirm the library path. This is the directory where imported files will be placed. Files are organized as:

```
<library path>/Artist/Album (Year)/Song.ext
```

!!! important
    Jellyfin must have write access to this directory for the audio to be copied successfully.

## Audio Format

Choose your preferred download format:

| Format | Notes |
| :--- | :--- |
| Opus (.opus) | Best quality at smaller file sizes |
| MP3 (.mp3) | Widest compatibility |
| M4A (.m4a) | Well suited for Apple devices |

## Import Behavior

Choose whether importing a song that is already in the library should overwrite the existing file or skip it, leaving the duplicate in the download cache. A file with the same name counts as a duplicate unless both songs carry track numbers and they differ: a different song that shares its title with one on the same album, like a second "Interlude" with its own track number, is imported beside it as "Title (2).ext" and never overwrites it. A re-download of the same song from another upload is still a duplicate. Two more options are on by default:

* **Look up song details on Apple Music.** Each download is looked up in Apple's public music catalog (no account needed) to fill in its title, album, album artist, release year, track and disc numbers and genre. Only a confident match is used: the artist must match and the length must agree. Anything you entered when queueing the song is kept, and playlist imports keep their export's details.
* **Embed cover art.** The album cover from Apple's catalog (as a 1200 × 1200 square) is added to each downloaded file, so Jellyfin shows it.

## Cache Directory

By default, downloaded files are cached in the Jellyfin data directory. Override this if you need a specific staging location.

## Approved Users

By default, only users with access to the Admin Dashboard can download and import tracks. Enabling a user here allows select users to download tracks into your queue, which you can then approve and import into your library. See [Approved User Downloads](ApprovedUserDownloads.md).

**Playlist Limit** caps how many videos an approved user can queue from one playlist link (200 by default). A larger playlist is refused with a message asking them to have an administrator add it. Set it to 0 for no limit. Administrators are never limited.

## yt-dlp

The plugin downloads [yt-dlp](https://github.com/yt-dlp/yt-dlp) and the [Deno](https://deno.com) JavaScript runtime that yt-dlp needs for YouTube into the plugin folder the first time they are needed, from their official GitHub releases. Both are kept up to date: every 12 hours, before a download starts, the plugin checks for newer releases and replaces its own copies. Every file is checked against the SHA-256 checksum published with its release (yt-dlp's `SHA2-256SUMS`, Deno's `.sha256sum`) before it is made executable; a file that does not match is deleted and never run, and the error says so.

* **yt-dlp Path Override.** Set an absolute path (for example `/opt/homebrew/bin/yt-dlp`) to use your own yt-dlp instead. The plugin then never downloads or replaces yt-dlp, and **Update yt-dlp** runs that binary's own updater (`yt-dlp -U`). A system yt-dlp is otherwise only used when the plugin's own copy cannot be downloaded. A Deno installed on the system is used when the plugin has no copy of its own, and is never updated by the plugin.
* **Update yt-dlp.** Updates yt-dlp and the plugin's Deno right away instead of waiting for the next check. It waits for running downloads; cancel them on the [Download](Download.md) tab to update now.

## Reset

Both actions stop any running downloads first and cannot be undone.

* **Reset Queue** clears the download queue and deletes the cached files of every queued, failed and downloaded item that has not been imported.
* **Reset Cache** deletes every file in the download cache and clears the download queue.

Files left in the cache by an interrupted download (for example when the server stopped mid-download) are removed automatically the next time the server starts, and songs that were downloading go back to the queue.

Next: [Download](Download.md).
