# ![YouTube Audio](Jellyfin.Plugin.YouTubeAudio/Assets/Logo.png)

A Jellyfin plugin to download audio tracks from YouTube URLs or playlists. Once downloaded, files are tagged using internal metadata and imported into a local Jellyfin music library.

## How It Works
YouTube Audio lets you paste a YouTube video or playlist URL, download the audio using [YT-DLP](https://github.com/yt-dlp/yt-dlp), tag each track with metadata, and import the files directly into your Jellyfin library. YT-DLP is auto-downloaded on first use or can be manually installed and referenced. Downloaded files are organized into Jellyfin's expected directory structure automatically.

## Getting Started

### 1. Settings

#### Configure the plugin before your first download.

| Settings |
|----------|
| ![Settings](Jellyfin.Plugin.YouTubeAudio/Assets/Settings.png) |

**Music Library** — Select your target Jellyfin music library and confirm the library path. This is the directory where imported files will be placed. Files are organized as:

```
<library path>/Artist/Album (Year)/Song.ext
```

> **Important:** Jellyfin must have write access to this directory for the audio to be copied successfully.

**Audio Format** — Choose your preferred download format:

| Format | Notes |
|--------|-------|
| Opus (.opus) | Best quality at smaller file sizes |
| MP3 (.mp3) | Widest compatibility |
| M4A (.m4a) | Well-suited for Apple devices |

**Import Behavior** — Choose whether importing a file that already exists in the library should overwrite the existing file or skip it (leaving the duplicate in the download cache).

**Cache Directory** — By default, downloaded files are cached in the Jellyfin data directory. Override this if you need a specific staging location.

**Approved Users** — By default, only users with access to the Admin Dashboard can download & import tracks. Enabling a user here allows select users to download tracks into your queue which you then can approve and import into your library.

### 2. Download

#### Queue up audio for download.

| Downloads |
|-----------|
| ![Download](Jellyfin.Plugin.YouTubeAudio/Assets/Download.png) |

1. Paste a YouTube video or playlist URL into the input field
2. Click **Queue** to add it to the download queue
3. From the queue, select the items you want to download and click **Download** — or remove any you don't want with **Delete**

### 3. Import

#### Tag your downloaded files and bring them into your library.

| Import |
|--------|
| ![Import](Jellyfin.Plugin.YouTubeAudio/Assets/Import.png) |

1. Review and edit metadata for each downloaded file (title, artist, album artist, featured artists, album, year, genre, track number)
2. To apply the same metadata to multiple files at once, fill in the bulk fields at the top and click **Apply to Selected**
3. Remember to save any unsaved changes on individual tracks before importing (look for the save icon)
4. Select the files you want to import and click **Import**

Metadata is written directly to the audio files. On import, files are copied into your music library directory following Jellyfin's expected structure:

```
<library path>/Artist/Album (Year)/Song.ext
```

Successfully imported files are removed from the queue.

### 4. Approved User Downloads

#### Let trusted users queue downloads without giving them admin access.

You can allow specific Jellyfin users to submit download links from a simple page, without access to the plugin's admin tabs or the import step.

1. In **Settings**, open the **Approved Users** section, select the users you want to allow, and click **Save**.
2. Approved users sign in to Jellyfin and visit `/YouTube/Download` on your server (for example `https://jellyfin.example.com/YouTube/Download`).
3. They paste a YouTube link, fill in artist, album, year, and title, and submit it. The download then runs on the server and lands in your queue.

Approved users can only submit links. They cannot import into the library, and they never see the admin tabs. The page is reachable by its URL, but the submit endpoint checks approval against the signed-in Jellyfin account on the server, so an unapproved or signed-out visitor cannot queue anything.

---

# Versioning

Releases use a four-part version, `JJ.JJ.F.B`, that matches the supported Jellyfin version with the plugin's own feature/bug count:

```
10.11.1.2
└───┘ └┬┘
  │    └── 1 = Plugin feature release
  │        2 = Plugin bug/patch release within that feature
  │
  └─── 10.11 = Jellyfin version this build was tested/released for
```

# Installation

## Step 1: Add Plugin Repository

* Open Jellyfin and navigate to Dashboard → Plugins → Repositories
* Click Add Repository
* Enter the following repository URL: `https://raw.githubusercontent.com/JPKribs/jellyfin-plugin-youtubeaudio/master/manifest.json`
* Click Save

## Step 2: Install Plugin

* Go to the Catalog tab in the Plugins section
* Find YouTube Audio in the catalog
* Click Install
* Wait for installation to complete

## Step 3: Restart Jellyfin

* Restart your Jellyfin server completely
* Wait for Jellyfin to fully start up

## Verification Check

* After restart, navigate to Dashboard → Plugins → Server Sync to confirm the plugin configuration page loads properly.

---

## AI Disclaimer

Claude Code was utilized in the initial structure of this project and first drafts of documentation. All code has been manually reviewed, tested, and revised after its generation. This disclaimer exists in the interest of transparency.

**All code was written, or code reviewed and tested, by humans.**
