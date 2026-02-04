# Server Sync

A Jellyfin plugin that enables one-way synchronization from a Source Jellyfin Server to a Local Jellyfin Server. This enables you to not only keep **Content Files** syncronized on multiple servers, but also share **Watch History**, **Metadata**, and **User Settings** as well. The plugin is only required on the Local (destination) Server as all sync tasks are performed using standard Jellyfin APIs.

---

# WORK IN PROGRESS

**THIS IS NOT CURRENTLY READY FOR PRODUCTION USAGE! FEEL FREE TO TEST THIS OUT BUT DO NOT USE THIS FOR ANYTHING CRITICAL!**

**AT THIS TIME THE FOLLOWING IS COMPLETE:**

- **Content Syncing**
- **Histoy Syncing**
- **User Syncing**

**THE FOLLOWING IS INCOMPLETE:**

- **Metadata Syncing**
   - *Images and Metadata sync but array data (Genres, People, Studios, & Tags) does not sync at this time.*

---

# Settings

![Plugin Settings](Documentation/Screenshots/Settings/Main.png)

## Source Server

The Source Server is the Jellyfin Server you want to sync content **from**. This plugin runs on your Local Server and pulls content from the Source Server.

![Server Configuration](Documentation/Screenshots/Settings/Server%20Configuration.png)

1. **Generate an API Key** on the source server:
   - Go to **Dashboard > API Keys** on the source server
   - Create a new API key for Server Sync

2. **Configure the plugin** on your local server:
   - **Server URL**: The full URL of the source server (e.g., `http://192.168.1.100:8096`)
   - **API Key**: The API key you generated on the source server

3. **Test the connection** using the "Test" button to verify connectivity

Once connected, you'll see the source server's name and ID displayed, confirming the connection is working.

## Library Mapping

![Library Configuration](Documentation/Screenshots/Settings/Library%20Mapping.png)

1. **Create a new Library Mapping**

2. **Map your Source Library** to a library on your Local Server:

   - **Library**: Select the Source and Local Libraries that should map to each other
      - *Multiple Source Libraries can be mapped to the same Local Library if desired*

   - **Root Path**: This is the base folder path that the library uses for content
      - *This will take the Source Library file `/media/Track Testing/My Movie (2025)/movie.mp4` and save it to the Local Library at `/media/My Movie (2025)/movie.mp4`*
         - **Only single folder libraries are supported by this plugin.**

Once all of the Libraries that you want to map are mapped, save your settings.

## User Mapping [Optional]

![User Configuration](Documentation/Screenshots/Settings/User%20Mapping.png)

1. **Create a new User Mapping**

2. **Map your Source User** to a user on your Local Server
   - *This is only required if you want History or User Syncing*
   - *This NOT required for Content and Metadata Syncing*

Once all of the Users that you want to map are mapped, save your settings.

---

# Syncing Types

## Content Syncing

Content syncing enables one-way media file synchronization from the source server to your local server. The plugin periodically scans the source for movies, episodes, audio, and video files, compares them against what exists locally using ETag-based change detection, and queues missing or updated content for download. Downloads respect your configured bandwidth limits and can require manual approval before proceeding. For complete documentation, see **[Documentation/Content.md](Documentation/Content.md)**.

---

## History Syncing

History syncing enables bidirectional watch history synchronization between servers. The plugin tracks played/unplayed status, play counts, playback positions (resume points), and favorites for each user's items. Using intelligent two-way merge logic, it combines data from both servers: maximum play count, most recent playback position, and preserves played/favorite status if either server has them. History sync requires user mappings and library mappings but operates independently from content sync. For complete documentation, see **[Documentation/History.md](Documentation/History.md)**.

---

## Metadata Syncing

Metadata syncing enables one-way synchronization of media metadata from the source server to your local server. The plugin syncs three property categories independently: item metadata (titles, descriptions, ratings, genres, tags, provider IDs), images (posters, backdrops, logos), and people/artist associations. Each category uses intelligent comparison—semantic JSON diffing for metadata properties and SHA256 hash comparison for images. Items are matched by file path using your existing library mappings, the same matching logic used by History Sync. This allows you to curate metadata on a primary server and have secondary servers automatically reflect those changes. For complete documentation, see **[Documentation/Metadata.md](Documentation/Metadata.md)**.

---

## User Syncing

User syncing enables one-way synchronization of user settings from the source server to your local server. The plugin syncs three property categories independently: user policies (permissions and restrictions), user configuration (preferences), and profile images. Each category can be individually enabled and uses intelligent comparison—semantic JSON diffing for policies/configuration and SHA256 hash comparison for profile images. Library-specific permissions are automatically translated using your library mappings, ensuring access controls work correctly even when library IDs differ between servers. For complete documentation, see **[Documentation/Users.md](Documentation/Users.md)**.

---

## Installation

### Step 1: Add Plugin Repository

* Open Jellyfin and navigate to Dashboard → Plugins → Repositories
* Click Add Repository
* Enter the following repository URL: `https://raw.githubusercontent.com/JPKribs/jellyfin-plugin-serversync/master/manifest.json`
* Click Save

### Step 2: Install Plugin

* Go to the Catalog tab in the Plugins section
* Find Server Sync in the catalog
* Click Install
* Wait for installation to complete

### Step 3: Restart Jellyfin

* Restart your Jellyfin server completely
* Wait for Jellyfin to fully start up

### Verification Check

* After restart, navigate to Dashboard → Plugins → Server Sync to confirm the plugin configuration page loads properly.