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

Content syncing enables one-way media file synchronization from the source server to your local server. The plugin periodically scans the source for movies, episodes, audio, and video files, compares them against what exists locally using ETag-based change detection, and queues missing or updated content for download. Downloads respect your configured bandwidth limits and can require manual approval before proceeding. For complete documentation, see **[Documentation/Content.md](Documentation/Content.md)**.

---

## History Syncing

*Work in Progress*

---

## User Syncing

*Work in Progress*

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