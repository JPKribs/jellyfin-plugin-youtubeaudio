# Server Sync

A Jellyfin plugin that enables one-way synchronization from a source Jellyfin server to a local Jellyfin server. Keep your media libraries in sync across multiple servers with configurable approval workflows, bandwidth controls, and intelligent change detection.

---

## Source Server

The source server is the Jellyfin server you want to sync content **from**. This plugin runs on your local (destination) server and pulls content from the source server.

### Setup

1. **Generate an API Key** on the source server:
   - Go to **Dashboard > API Keys** on the source server
   - Create a new API key for Server Sync

2. **Configure the plugin** on your local server:
   - **Server URL**: The full URL of the source server (e.g., `http://192.168.1.100:8096`)
   - **API Key**: The API key you generated on the source server

3. **Test the connection** using the "Test" button to verify connectivity

Once connected, you'll see the source server's name and ID displayed, confirming the connection is working.

---

## Content Syncing

Content syncing downloads media files from the source server to your local server. The plugin periodically scans the source for movies, episodes, audio, and video files, then downloads missing or updated content based on your configuration.

**Key Features:**
- Library-to-library mapping with automatic path translation
- ETag-based change detection for reliable sync
- Granular approval workflows (Enabled / Require Approval / Disabled) for new content, replacements, and deletions
- Bandwidth throttling with time-based scheduling
- Recycling bin for safe deletions
- Companion file support (subtitles, NFO, images)

For complete documentation including all settings, library mappings, approval workflows, and technical details, see **[Documentation/Content.md](Documentation/Content.md)**.

---

## History Syncing

*Work in Progress*

---

## User Syncing

*Work in Progress*
