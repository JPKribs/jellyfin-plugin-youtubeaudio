# YouTube Audio

![YouTube Audio](assets/logo.png){ width="160" }

YouTube Audio is a Jellyfin plugin to download audio tracks from YouTube URLs or playlists. Once downloaded, files are tagged using internal metadata and imported into a local Jellyfin music library.

## How It Works

YouTube Audio lets you paste a YouTube video or playlist URL, download the audio using [YT-DLP](https://github.com/yt-dlp/yt-dlp), tag each track with metadata, and import the files directly into your Jellyfin library.

YT-DLP and the [Deno](https://deno.com) JavaScript runtime it needs for YouTube are downloaded into the plugin folder automatically, including in the official Jellyfin Docker image, and yt-dlp is kept up to date. You can point to your own yt-dlp instead in [Settings](Settings.md).

Downloads that YouTube temporarily refuses are retried automatically, and a failed item can be retried by selecting it on the Download tab and clicking **Download**. Downloaded files are organized into Jellyfin's expected directory structure automatically.

Next: [Installation](Installation.md).
