#!/usr/bin/env python3
"""
End-to-end test of the YouTube Audio plugin on a disposable Jellyfin Docker container.

Covers the paths that move or delete library data: an Apple Music library-export upload with
playlist selection, duplicate rules against a pre-populated library, real YouTube search and
download inside the container (yt-dlp auto-provisioned), album-folder alignment, the single ->
album upgrade (deleting only the single), import into the library, scan-driven private
playlists for a non-admin owner, ordering, persistence across a container restart, failed-match
pruning, the public read-only queue, and a re-import that must change nothing.

Every file in the test library is fingerprinted before and after each stage; any change other
than the expected ones fails the run.

Needs Docker and network access (YouTube, GitHub for yt-dlp). Uses only the Python standard
library; test audio is generated and tagged with the ffmpeg inside the Jellyfin image.

    dotnet build Jellyfin.Plugin.YouTubeAudio/Jellyfin.Plugin.YouTubeAudio.csproj -c Release
    python3 e2e/docker_e2e.py            # or: e2e/run.sh

Exit code 0 when every check passes.
"""
import argparse, base64, hashlib, json, os, plistlib, secrets, shutil, subprocess, sys, time, unicodedata, urllib.error, urllib.parse, urllib.request

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_IMAGE = "jellyfin/jellyfin:12.2.20261005-225228"  # stable 12.2 (no plain "12.2.0" tag exists)

parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0], formatter_class=argparse.RawDescriptionHelpFormatter)
parser.add_argument("--image", default=DEFAULT_IMAGE, help="Jellyfin image to test against (default: %(default)s)")
parser.add_argument("--expect-version", default="12.2.0", help="server version the image must report; empty to skip (default: %(default)s)")
parser.add_argument("--port", type=int, default=8097, help="host port for the test server (default: %(default)s)")
parser.add_argument("--name", default="yta-e2e", help="container name (default: %(default)s)")
parser.add_argument("--workdir", default=os.path.join(REPO, "e2e", ".work"), help="scratch folder, wiped on start (default: e2e/.work)")
parser.add_argument("--plugin-dir", default=os.path.join(REPO, "Jellyfin.Plugin.YouTubeAudio", "bin", "Release", "net10.0"),
                    help="folder holding the built plugin DLLs (default: Release build output)")
parser.add_argument("--keep", action="store_true", help="leave the container running afterwards for manual inspection")
ARGS = parser.parse_args()

WORK = os.path.abspath(ARGS.workdir)
PLUGIN_DIR = os.path.abspath(ARGS.plugin_dir)
IMAGE = ARGS.image
NAME = ARGS.name
BASE = f"http://localhost:{ARGS.port}"
PLUGIN_ID = "7323ea64-a200-4265-ab8f-e7ae27d06c38"
AUTH = 'MediaBrowser Client="e2e", Device="e2e", DeviceId="yta-docker-e2e", Version="1.0"'
MUSIC = os.path.join(WORK, "music")
# Run containers as the invoking user so files in the work folder stay removable on Linux hosts.
USER = ["--user", f"{os.getuid()}:{os.getgid()}"] if hasattr(os, "getuid") else []
TOKEN = None
FAILED = []


# ---------------------------------------------------------------- helpers
def sh(*args, check=True):
    return subprocess.run(args, check=check, capture_output=True, text=True).stdout


def call(method, path, body=None, token=None):
    headers = {"Content-Type": "application/json", "Authorization": AUTH + (f', Token="{token or TOKEN}"' if (token or TOKEN) else "")}
    data = json.dumps(body).encode() if body is not None else (b"" if method in ("POST", "DELETE") else None)
    req = urllib.request.Request(BASE + path, data=data, method=method, headers=headers)
    with urllib.request.urlopen(req, timeout=120) as resp:
        text = resp.read().decode()
        return json.loads(text) if text else None


def wait(pred, what, timeout=120, interval=2):
    end = time.time() + timeout
    last = None
    while time.time() < end:
        try:
            last = pred()
            if last:
                return last
        except Exception as ex:  # server busy / not ready
            last = ex
        time.sleep(interval)
    raise SystemExit(f"TIMEOUT waiting for {what} (last: {last})")


def check(label, ok, detail=""):
    print(("  PASS " if ok else "  FAIL ") + label + (f"  [{detail}]" if detail else ""), flush=True)
    if not ok:
        FAILED.append(label)


def snapshot():
    out = {}
    for root, _, files in os.walk(MUSIC):
        for f in files:
            p = os.path.join(root, f)
            # NFC so accented names compare equal however the host filesystem reports them.
            out[unicodedata.normalize("NFC", os.path.relpath(p, MUSIC))] = hashlib.sha256(open(p, "rb").read()).hexdigest()
    return out


def tag(rel, title, artist, album, year, track):
    """Creates a short tagged Opus file in the test library with the image's own ffmpeg."""
    os.makedirs(os.path.dirname(os.path.join(MUSIC, rel)), exist_ok=True)
    meta = {"title": title, "artist": artist, "album_artist": artist, "album": album, "date": str(year), "track": str(track)}
    cmd = ["docker", "run", "--rm", *USER, "-v", f"{MUSIC}:/m", "--entrypoint", "/usr/lib/jellyfin-ffmpeg/ffmpeg", IMAGE,
           "-v", "error", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=mono", "-t", "3", "-c:a", "libopus"]
    for key, value in meta.items():
        cmd += ["-metadata", f"{key}={value}"]
    subprocess.run(cmd + ["/m/" + rel], check=True, capture_output=True)


def ffprobe(rel):
    out = sh("docker", "exec", NAME, "/usr/lib/jellyfin-ffmpeg/ffprobe", "-v", "error", "-show_entries",
             "format=duration:format_tags:stream=codec_name:stream_tags", "-of", "json", "/media/music/" + rel)
    data = json.loads(out)
    tags = {k.lower(): v for k, v in (data["format"].get("tags") or {}).items()}
    for s in data.get("streams", []):
        tags.update({k.lower(): v for k, v in (s.get("tags") or {}).items()})
    return float(data["format"]["duration"]), data["streams"][0]["codec_name"], tags


def start_container():
    sh("docker", "run", "-d", "--name", NAME, *USER, "-p", f"{ARGS.port}:8096",
       "-v", f"{WORK}/config:/config", "-v", f"{WORK}/cache:/cache", "-v", f"{MUSIC}:/media/music", IMAGE)
    wait(lambda: (call("GET", "/System/Info/Public") or {}).get("Version"), "server up", 180)


# ---------------------------------------------------------------- fixture
# Classical test data. NEW is downloaded fresh; UPGRADE exists in the library only as a single and
# must be replaced by its album version; OWNED and TITLE_TRACK are already in the library (TITLE_TRACK
# is alone on an album named after itself, so it looks like a single but must never be "upgraded");
# UNMATCHABLE has no YouTube upload.
NEW, NEW_TRACK, NEW_SECONDS = "Gymnopédie No. 1", 9, 300
UPGRADE, UPGRADE_TRACK, UPGRADE_SECONDS = "Clair de lune", 3, 408
OWNED = "Für Elise"
TITLE_TRACK = "Rhapsody in Blue"
UNMATCHABLE = "Qwxzv Unmatchable Sonata Zzyx"
ARTIST, ALBUM, LIBRARY_ALBUM, YEAR, GENRE = "Lang Lang", "Piano Book", "Piano Book (Deluxe Edition)", 2019, "Classical"
ALBUM_DIR = f"{ARTIST}/{LIBRARY_ALBUM} ({YEAR})/"
SINGLE_FILE = f"{ARTIST}/{UPGRADE} (2018)/{UPGRADE}.opus"
TITLE_FILE = f"Leonard Bernstein/{TITLE_TRACK} (1959)/{TITLE_TRACK}.opus"


def apple_library_export():
    t = lambda tid, name, artist, album, ms, num, count, year, **kw: {
        "Track ID": tid, "Name": name, "Artist": artist, "Album Artist": kw.get("aa", artist), "Album": album,
        "Genre": kw.get("genre", GENRE), "Kind": "Apple Music AAC audio file", "Total Time": ms,
        "Disc Number": 1, "Disc Count": 1, "Track Number": num, "Track Count": count, "Year": year,
        "Track Type": "Remote", "Apple Music": True, **({"Composer": kw["composer"]} if "composer" in kw else {})}
    tracks = {
        "101": t(101, OWNED, ARTIST, ALBUM, 204000, 2, 25, YEAR, composer="Ludwig van Beethoven"),
        "102": t(102, NEW, ARTIST, ALBUM, NEW_SECONDS * 1000, NEW_TRACK, 25, YEAR, composer="Erik Satie"),
        "103": t(103, UPGRADE, ARTIST, ALBUM, UPGRADE_SECONDS * 1000, UPGRADE_TRACK, 25, YEAR, composer="Claude Debussy"),
        "104": t(104, UPGRADE, ARTIST, f"{UPGRADE} - Single", UPGRADE_SECONDS * 1000, 1, 1, 2018, composer="Claude Debussy"),
        "105": t(105, TITLE_TRACK, "Leonard Bernstein", TITLE_TRACK, 1012000, 1, 8, 1959, composer="George Gershwin"),
        "106": t(106, UNMATCHABLE, "Nonexistent Ensemble Qqqq", "Nothing Real", 200000, 1, 1, 2026),
    }
    pl = lambda pid, name, ids, **kw: {"Name": name, "Description": "", "Playlist ID": pid, "Playlist Persistent ID": "%016X" % pid,
                                        "All Items": True, **kw, "Playlist Items": [{"Track ID": i} for i in ids]}
    root = {"Major Version": 1, "Minor Version": 1, "Application Version": "1.7.0.163", "Features": 5,
            "Show Content Ratings": True, "Tracks": tracks, "Playlists": [
                pl(1, "Library", [101, 102, 103, 105, 106], Master=True, Visible=False),
                pl(2, "Music", [101, 102, 103, 105, 106], **{"Distinguished Kind": 4, "Music": True}),
                pl(3, "Piano Favourites", [102, 101, 103, 105]),
                pl(4, "Singles", [104]),
                pl(5, "Ghost", [106, 101])]}
    return base64.b64encode(plistlib.dumps(root, sort_keys=False)).decode()


# ---------------------------------------------------------------- run
def main():
    global TOKEN
    sh("docker", "rm", "-f", NAME, check=False)
    for f in ("Jellyfin.Plugin.YouTubeAudio.dll", "YoutubeDLSharp.dll", "TagLibSharp.dll"):
        if not os.path.isfile(os.path.join(PLUGIN_DIR, f)):
            raise SystemExit(f"{f} not found in {PLUGIN_DIR}; build the plugin first (see --help)")
    print(f"image {IMAGE}, plugin {PLUGIN_DIR}")
    subprocess.run(["docker", "pull", "-q", IMAGE], check=True, capture_output=True)
    shutil.rmtree(WORK, ignore_errors=True)
    for d in ("config/plugins/YouTubeAudio", "cache", "music"):
        os.makedirs(os.path.join(WORK, d), exist_ok=True)
    for f in ("Jellyfin.Plugin.YouTubeAudio.dll", "YoutubeDLSharp.dll", "TagLibSharp.dll"):
        shutil.copy(os.path.join(PLUGIN_DIR, f), os.path.join(WORK, "config/plugins/YouTubeAudio", f))

    print("== Pre-existing library")
    tag(ALBUM_DIR + OWNED + ".opus", OWNED, ARTIST, LIBRARY_ALBUM, YEAR, 2)
    tag(SINGLE_FILE, UPGRADE, ARTIST, UPGRADE, 2018, 1)
    open(os.path.join(MUSIC, os.path.dirname(SINGLE_FILE), "folder.jpg"), "wb").write(b"\xff\xd8\xff\xe0 not really a jpeg")
    tag(TITLE_FILE, TITLE_TRACK, "Leonard Bernstein", TITLE_TRACK, 1959, 1)
    tag("Control Ensemble/Control Album (2000)/Control Piece.opus", "Control Piece", "Control Ensemble", "Control Album", 2000, 1)
    before = snapshot()
    print(f"  {len(before)} files fingerprinted")

    print("== Server")
    start_container()
    info = call("GET", "/System/Info/Public")
    if ARGS.expect_version:
        check(f"server is Jellyfin {ARGS.expect_version}", info["Version"] == ARGS.expect_version, info["Version"])
    # Throwaway logins generated per run; written only to the git-ignored work folder for --keep.
    creds = {"admin": secrets.token_urlsafe(16), "listener": secrets.token_urlsafe(16)}
    json.dump(creds, open(os.path.join(WORK, "creds.json"), "w"))
    wait(lambda: call("GET", "/Startup/Configuration"), "startup wizard", 120)
    call("POST", "/Startup/Configuration", {"UICulture": "en-US", "MetadataCountryCode": "US", "PreferredMetadataLanguage": "en"})
    call("GET", "/Startup/User")
    call("POST", "/Startup/User", {"Name": "admin", "Password": creds["admin"]})
    call("POST", "/Startup/Complete")
    auth = call("POST", "/Users/AuthenticateByName", {"Username": "admin", "Pw": creds["admin"]})
    TOKEN = auth["AccessToken"]
    admin_id = auth["User"]["Id"]
    listener_id = call("POST", "/Users/New", {"Name": "listener", "Password": creds["listener"]})["Id"]
    listener_token = call("POST", "/Users/AuthenticateByName", {"Username": "listener", "Pw": creds["listener"]})["AccessToken"]
    plugins = call("GET", "/Plugins")
    yta = next((p for p in plugins if p["Id"].replace("-", "") == PLUGIN_ID.replace("-", "")), None)
    check("plugin loaded and active", yta is not None and yta["Status"] == "Active", json.dumps(yta and {k: yta[k] for k in ("Name", "Version", "Status")}))

    call("POST", "/Library/VirtualFolders?" + urllib.parse.urlencode({"name": "Music", "collectionType": "music", "refreshLibrary": "true"}),
         {"LibraryOptions": {"PathInfos": [{"Path": "/media/music"}]}})
    scan_state = {"next": time.time() + 15}

    def scanned():
        n = sum(1 for i in call("GET", "/Items?includeItemTypes=Audio&recursive=true&fields=Artists,Path")["Items"] if i.get("Artists"))
        if n < 4 and time.time() > scan_state["next"]:
            call("POST", "/Library/Refresh")  # the first scan can start before the folder is linked
            scan_state["next"] = time.time() + 20
        return n == 4

    wait(scanned, "initial scan", 240)
    library_id = next(f["ItemId"] for f in call("GET", "/Library/VirtualFolders") if f["Name"] == "Music")
    single_item = next(i["Id"] for i in call("GET", "/Items?includeItemTypes=Audio&recursive=true&fields=Path")["Items"] if "(2018)" in i["Path"])
    cfg = call("GET", f"/Plugins/{PLUGIN_ID}/Configuration")
    cfg.update({"MusicLibraryId": library_id, "MusicLibraryPath": "/media/music", "AudioFormat": 0, "ReplaceDuplicates": False})
    call("POST", f"/Plugins/{PLUGIN_ID}/Configuration", cfg)
    print("  library scanned (4 tracks), plugin configured")

    def lplaylists():
        return {i["Name"]: i["Id"] for i in call("GET", f"/Items?userId={listener_id}&includeItemTypes=Playlist&recursive=true", token=listener_token)["Items"]}

    def ltitles(pid):
        items = call("GET", f"/Playlists/{pid}/Items?userId={listener_id}&fields=Path", token=listener_token)["Items"]
        return [(i["Name"], i.get("Path", "")) for i in items]

    def queue():
        return call("GET", "/YouTubeAudio/Queue")["Items"]

    print("== 1. Upload library export (selection step)")
    export = apple_library_export()
    sel = call("POST", "/YouTubeAudio/Queue/Playlist", {"FileName": "Library.xml", "Content": export, "UserId": listener_id})
    check("library upload asks for a selection first", sel["NeedsSelection"] and not sel["Items"])
    names = [p["Name"] for p in sel["Playlists"]]
    check("choices: user playlists then Entire Library; built-ins hidden", names == ["Piano Favourites", "Singles", "Ghost", "Entire Library"], str(names))
    check("nothing queued yet", len(queue()) == 0)

    print("== 2. Queue everything")
    res = call("POST", "/YouTubeAudio/Queue/Playlist", {"FileName": "Library.xml", "Content": export, "UserId": listener_id,
                                                         "PlaylistIds": [p["Id"] for p in sel["Playlists"]]})
    queued = {i["Title"]: i for i in res["Items"]}
    check("queued exactly the new songs + the album upgrade", sorted(queued) == sorted([NEW, UNMATCHABLE, UPGRADE]), str(sorted(queued)))
    check(f"{OWNED} and {TITLE_TRACK} skipped as already in library", res["AlreadyInLibrary"] == 2, str(res["AlreadyInLibrary"]))
    check("one library single will be upgraded", res["LibraryUpgrades"] == 1 and queued[UPGRADE]["ReplacesFileCount"] == 1)
    check("three playlists tracked for listener", res["PlaylistsTracked"] == 3, str(res["PlaylistsTracked"]))
    check("library untouched by queueing", snapshot() == before)

    favourites = wait(lambda: lplaylists().get("Piano Favourites"), "Piano Favourites playlist", 30)
    time.sleep(4)
    check("Piano Favourites starts with the songs already owned, in source order (no soon-deleted single)", [t for t, _ in ltitles(favourites)] == [OWNED, TITLE_TRACK], str(ltitles(favourites)))
    check("Singles not created yet: it waits for the album version", "Singles" not in lplaylists(), str(list(lplaylists())))
    pinfo = call("GET", f"/Playlists/{favourites}", token=listener_token)
    check("playlist private to listener", pinfo["OpenAccess"] is False and pinfo["Shares"] == [], json.dumps({k: pinfo[k] for k in ("OpenAccess", "Shares")}))
    check("admin cannot see listener's playlists",
          not any(i["Name"] in ("Piano Favourites", "Singles", "Ghost") for i in call("GET", f"/Items?userId={admin_id}&includeItemTypes=Playlist&recursive=true")["Items"]))

    print("== 3. Download (real YouTube, yt-dlp auto-provisioned in container)")
    call("POST", "/YouTubeAudio/Queue/Process", {"Ids": [i["Id"] for i in res["Items"]]})
    wait(lambda: queue() and all(i["StatusCode"] in (2, 4) for i in queue()), "downloads to finish", 900, 5)
    q = {i["Title"]: i for i in queue()}
    for title in (NEW, UPGRADE):
        check(f"downloaded: {title}", q[title]["StatusCode"] == 2, q[title].get("ErrorMessage") or q[title]["Url"])
    check("unmatchable piece failed cleanly", q[UNMATCHABLE]["StatusCode"] == 4 and "No matching YouTube upload" in (q[UNMATCHABLE]["ErrorMessage"] or ""),
          q[UNMATCHABLE].get("ErrorMessage"))
    print("  matched:", {t: (q[t]["Url"], q[t].get("MatchNote")) for t in (NEW, UPGRADE)})
    plugin_files = os.listdir(os.path.join(WORK, "config/plugins/YouTubeAudio"))
    check("yt-dlp and the Deno runtime were provisioned in the plugin folder",
          "deno" in plugin_files and any(f.startswith("yt-dlp_linux") for f in plugin_files), str(sorted(plugin_files)))
    check("library untouched by downloading", snapshot() == before)

    downloads = {d["MetadataTitle"]: d for d in call("GET", "/YouTubeAudio/Downloads")["Items"]}
    for title, track, dur in ((NEW, NEW_TRACK, NEW_SECONDS), (UPGRADE, UPGRADE_TRACK, UPGRADE_SECONDS)):
        d = downloads.get(title, {})
        check(f"tags staged for {title}",
              d.get("Artist") == ARTIST and d.get("AlbumArtist") == ARTIST and d.get("Album") == LIBRARY_ALBUM
              and d.get("Year") == YEAR and d.get("TrackNumber") == track and d.get("Genre") == GENRE and abs((d.get("Duration") or 0) - dur) <= 8,
              json.dumps({k: d.get(k) for k in ("Artist", "AlbumArtist", "Album", "Year", "TrackNumber", "Genre", "Duration")}))

    print("== 3b. Public read-only queue")
    page = urllib.request.urlopen(BASE + "/YouTube/Queue", timeout=30).read().decode()
    check("queue page served without sign-in", "Download Queue" in page and "/Items" in page)
    try:
        urllib.request.urlopen(BASE + "/YouTube/Queue/Items", timeout=30)
        anon = 200
    except urllib.error.HTTPError as ex:
        anon = ex.code
    check("queue data refused when signed out", anon == 401, str(anon))
    pub = call("GET", "/YouTube/Queue/Items", token=listener_token)
    by_title = {i.get("Title"): i for i in pub}
    check("regular (non-approved) user can read the queue", len(pub) == 3, str([i.get("Title") for i in pub]))
    check("public queue shows statuses and sources",
          by_title[NEW]["StatusCode"] == 2 and by_title[NEW]["Source"] == "Apple Music Library"
          and by_title[UNMATCHABLE]["Status"] == "Could not be downloaded", json.dumps(by_title[NEW]))
    leaked = {k for i in pub for k in i if k in ("Url", "ErrorMessage", "FileName", "Id", "MatchNote")}
    check("public queue leaks no URLs, file names, ids or errors", not leaked, str(leaked))

    print("== 4. Failed songs can be retried; removing them finishes the import; restart persists state")
    call("POST", "/YouTubeAudio/Playlists/Sync")
    targets = {t["Name"]: t for t in call("GET", "/YouTubeAudio/Playlists")}
    check("Ghost import stays open while its failed song can still be retried", "Ghost" in targets, str(list(targets)))
    failed = q[UNMATCHABLE]
    call("POST", "/YouTubeAudio/Queue/Process", {"Ids": [failed["Id"]]})
    retried = wait(lambda: (lambda i: i if i and i["UpdatedAt"] != failed["UpdatedAt"] and i["StatusCode"] == 4 else None)(
        next((i for i in queue() if i["Id"] == failed["Id"]), None)), "failed item retried", 180, 3)
    retry_logs = sh("docker", "logs", NAME, check=False) + subprocess.run(["docker", "logs", NAME], capture_output=True, text=True).stderr
    check("selecting a failed item and downloading retries it", retry_logs.count("No YouTube match for Nonexistent Ensemble Qqqq") >= 2 and retried["StatusCode"] == 4,
          retried.get("ErrorMessage"))
    call("DELETE", f"/YouTubeAudio/Queue/{failed['Id']}")
    call("POST", "/YouTubeAudio/Playlists/Sync")
    targets = {t["Name"]: t for t in call("GET", "/YouTubeAudio/Playlists")}
    check("Ghost import finished once its failed song was removed", "Ghost" not in targets, str(list(targets)))
    check("Piano Favourites and Singles still in progress", {"Piano Favourites", "Singles"} <= set(targets), str({k: (v["Added"], v["Total"]) for k, v in targets.items()}))
    sh("docker", "restart", NAME)
    time.sleep(5)
    wait(lambda: (call("GET", "/System/Info/Public") or {}).get("Version") and call("GET", "/YouTubeAudio/Queue"), "server after restart", 180)
    check("queue survived restart", sum(1 for i in queue() if i["StatusCode"] == 2) == 2)
    check("playlist imports survived restart", {"Piano Favourites", "Singles"} <= {t["Name"] for t in call("GET", "/YouTubeAudio/Playlists")})

    print("== 5. Import into the library")
    ids = [i["Id"] for i in queue() if i["StatusCode"] == 2]
    imp = call("POST", "/YouTubeAudio/Import", {"Ids": ids})
    check("both imported, no errors", len(imp["ImportedIds"]) == 2 and not imp["Errors"], json.dumps(imp))
    check("exactly one library file removed (the single)", imp["RemovedFiles"] == 1, str(imp["RemovedFiles"]))
    after = snapshot()
    album_dir = ALBUM_DIR
    expected = dict(before)
    del expected[SINGLE_FILE]
    added = {k for k in after if k not in before}
    check("new pieces filed into the existing album folder", added == {album_dir + NEW + ".opus", album_dir + UPGRADE + ".opus"}, str(sorted(added)))
    check("every pre-existing file except the single is byte-identical", all(after.get(k) == v for k, v in expected.items()),
          str([k for k, v in expected.items() if after.get(k) != v]))
    check("the single is gone", SINGLE_FILE not in after)
    check("non-audio file beside the single kept (folder not deleted)", os.path.dirname(SINGLE_FILE) + "/folder.jpg" in after)
    check(f"{TITLE_TRACK} title track not 'upgraded'", after.get(TITLE_FILE) == before[TITLE_FILE])
    check("cache emptied of imported files", not any(f.endswith(".opus") for f in os.listdir(os.path.join(WORK, "config/data/youtubeaudio/cache"))),
          str(os.listdir(os.path.join(WORK, "config/data/youtubeaudio/cache"))))
    for title, track, dur in ((NEW, NEW_TRACK, NEW_SECONDS), (UPGRADE, UPGRADE_TRACK, UPGRADE_SECONDS)):
        d, codec, tags = ffprobe(album_dir + title + ".opus")
        check(f"imported {title} is real audio with clean tags",
              codec == "opus" and abs(d - dur) <= 8 and tags.get("title") == title and tags.get("artist") == ARTIST
              and tags.get("album") == LIBRARY_ALBUM and tags.get("albumartist", tags.get("album_artist")) == ARTIST
              and tags.get("track", "").split("/")[0].lstrip("0") == str(track) and tags.get("date") == str(YEAR)
              and tags.get("genre") == GENRE and "composer" in tags,
              f"{codec} {d:.0f}s {json.dumps({k: tags.get(k) for k in ('title', 'artist', 'album', 'albumartist', 'album_artist', 'track', 'date', 'genre', 'composer')})}")

    print("== 6. Scan fills the playlists")
    wait(lambda: len(ltitles(favourites)) == 4, "Piano Favourites to fill after scan", 180, 3)
    time.sleep(20)  # let the debounced sync and re-order settle
    ft = ltitles(favourites)
    check("Piano Favourites complete in source order", [t for t, _ in ft] == [NEW, OWNED, UPGRADE, TITLE_TRACK], str([t for t, _ in ft]))
    check("Piano Favourites uses the album version", any(p.endswith(album_dir + UPGRADE + ".opus") for _, p in ft), str(ft))
    singles = wait(lambda: lplaylists().get("Singles"), "Singles playlist", 60)
    st = wait(lambda: ltitles(singles), "Singles to fill", 60)
    check("Singles holds only the album version", [p for _, p in st] == ["/media/music/" + album_dir + UPGRADE + ".opus"], str(st))
    check("all playlist imports finished and dropped", call("GET", "/YouTubeAudio/Playlists") == [], str(call("GET", "/YouTubeAudio/Playlists")))
    check("single's library item removed by scan", not any(i["Id"] == single_item for i in call("GET", "/Items?includeItemTypes=Audio&recursive=true")["Items"]))

    print("== 7. Re-import the same export: nothing duplicated")
    lib_before = snapshot()
    sel = call("POST", "/YouTubeAudio/Queue/Playlist", {"FileName": "Library.xml", "Content": export, "UserId": listener_id})
    res = call("POST", "/YouTubeAudio/Queue/Playlist", {"FileName": "Library.xml", "Content": export, "UserId": listener_id,
                                                         "PlaylistIds": [p["Id"] for p in sel["Playlists"]]})
    check("only the removed unmatchable piece is queued again", [i["Title"] for i in res["Items"]] == [UNMATCHABLE], str([i["Title"] for i in res["Items"]]))
    check("everything else recognised as in library", res["AlreadyInLibrary"] == 4 and res["LibraryUpgrades"] == 0, json.dumps({k: res[k] for k in ("AlreadyInLibrary", "LibraryUpgrades", "AlreadyQueued")}))
    check("unmatchable piece queued once", sum(1 for i in queue() if i["Title"] == UNMATCHABLE) == 1)
    time.sleep(8)
    check("no duplicate playlists", sorted(n for n in lplaylists()) == ["Ghost", "Piano Favourites", "Singles"], str(sorted(lplaylists())))
    check("Piano Favourites unchanged", [t for t, _ in ltitles(favourites)] == [NEW, OWNED, UPGRADE, TITLE_TRACK], str([t for t, _ in ltitles(favourites)]))
    check("Singles unchanged", len(ltitles(singles)) == 1)
    check("library untouched by re-import", snapshot() == lib_before)

    logs = sh("docker", "logs", NAME, check=False) + subprocess.run(["docker", "logs", NAME], capture_output=True, text=True).stderr
    errs = [l for l in logs.splitlines() if "YouTubeAudio" in l and ("[ERR]" in l or "[FTL]" in l)]
    check("no plugin errors in server log", not errs, "\n".join(errs[:5]))
    print("\n  plugin log:")
    for l in logs.splitlines():
        if "YouTubeAudio" in l and any(w in l for w in ("Queued", "Matched", "Downloaded", "Imported", "Removed", "Created", "Added", "Re-ordered", "finished", "No YouTube", "yt-dlp", "Deno", "Retrying", "WRN")):
            print("  >", l.split("] ", 2)[-1][:220])

    print("\nRESULT:", f"FAILED ({len(FAILED)}): " + "; ".join(FAILED) if FAILED else "ALL PASSED")


try:
    main()
finally:
    if ARGS.keep:
        print(f"\nContainer {NAME} left running at {BASE} (logins: admin / listener, passwords in {WORK}/creds.json).")
        print(f"Remove it with: docker rm -f {NAME}")
    else:
        subprocess.run(["docker", "rm", "-f", NAME], capture_output=True)

sys.exit(1 if FAILED else 0)
