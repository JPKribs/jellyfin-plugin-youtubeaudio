# Screenshot Kit

Captures every screenshot in `docs/screenshots/` from the repository's own files and generic sample data, with no Jellyfin server.

| File | Does |
| :--- | :--- |
| `server.py` | Serves the harness, the plugin's pages from `Jellyfin.Plugin.YouTubeAudio/Web`, Base's assets from the NuGet package the csproj names, and jellyfin-web. It renders `/YouTube/Download` and `/YouTube/Queue` through Base's status template the way `StatusPage.Render` does, answers their API calls with generic songs, and serves the US English strings at `/YouTubeAudio/Translations` as `{ language, strings, casing }`, the way `BaseTranslations.Serve` answers |
| `harness.html` | Loads jellyfin-web's stylesheets in load order, then mounts one tab the way the dashboard does |
| `upgrade.js` | Adds the classes and markup Jellyfin's `emby-*` elements add on upgrade, so the real CSS applies |
| `stub.js` | A stand-in `ApiClient` with a generic queue, downloaded files, playlist imports, and users |
| `shoot.mjs` | Captures **Download**, **Import**, **Settings**, **Queue**, and **User-Download** into `docs/screenshots/` |

## Requirements

* Python 3 and Node.js.
* playwright-core and a Chromium build for it.
* The plugin restored once with `dotnet restore`, so NuGet caches the Base version the csproj names.
* jellyfin-web, which the macOS Jellyfin app includes.

| Variable | Default |
| :--- | :--- |
| `PLAYWRIGHT_CORE` | `playwright-core`, resolved from this folder |
| `CHROMIUM` | `~/Library/Caches/ms-playwright/chromium-1148/chrome-mac/Chromium.app/Contents/MacOS/Chromium` |
| `JELLYFIN_WEB` | `/Applications/Jellyfin.app/Contents/Resources/jellyfin-web` |
| `NUGET_PACKAGES` | `~/.nuget/packages` |

## Running

Run both from this folder. Start the server, then take every shot.

```bash
python3 -I server.py 8857 &
PLAYWRIGHT_CORE=/path/to/node_modules/playwright-core/index.mjs node shoot.mjs
```

Name files to retake only those.

```bash
node shoot.mjs Queue
```

Each run prints every shot's size and any page error. Open every PNG it writes and look at it before committing it. The **Queue** card ends with the time it was updated, so it differs from the committed file in that line on every run. Restore it with `git checkout docs/screenshots/Queue.png` when nothing else changed. Some runs draw the **Queue** card 1604 px tall with its last row wrapped, so retake it until it is 1588 px tall.
