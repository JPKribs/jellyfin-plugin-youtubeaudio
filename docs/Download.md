# Download

Queue up audio for download.

![Download](screenshots/Download.png)

## Queue a YouTube Link

1. Paste a YouTube video or playlist URL into the input field.
2. Click **Queue** to add it to the download queue.
3. From the queue, select the items you want to download and click **Download**, or remove any you don't want with **Delete**.

Only `youtube.com`, `www.youtube.com`, `m.youtube.com`, `music.youtube.com` and `youtu.be` links are accepted, and a link containing spaces or quotes is refused.

## Cancel Downloads

While downloads are running, **Cancel Downloads** appears above the queue. It stops yt-dlp, deletes the partial files of the song it was downloading and puts that song back in the queue. Songs that had not started stay queued, so you can start them again later. Use it, for example, to stop a large playlist so you can run **Update yt-dlp** in [Settings](Settings.md).

## Import a Playlist From Another Service

1. Choose **Apple Music** or **Spotify** on the Download tab, then export:
    * **Apple Music:** for one playlist, select it and choose **File → Library → Export Playlist…** (Unicode Text or XML). For your whole library, choose **File → Library → Export Library…**, which lets you pick any of your playlists, or the entire library, in the next step.
    * **Spotify:** use [Exportify](https://github.com/watsonbox/exportify) to export one playlist as a CSV file, or all of them as a ZIP to pick from in the next step. Playlist names come from the file names. Liked Songs is offered like Apple Music's entire library and does not become a playlist. Spotify's own album cover is embedded, the genre (which Spotify does not export) comes from the Apple Music lookup, and each track's ISRC is used to find its official YouTube upload.
2. Pick the **Playlist Owner**. Each imported playlist becomes a private Jellyfin playlist with the same name, owned by that user (choose "Don't create playlists" to only download). Then, under **Playlist Export**, choose the file and click **Queue Playlist**. For a library export, tick the playlists you want and click **Queue Selected**. A track in more than one selected playlist is queued once. Every track is added to the queue with its title, artist, album, genre, composer, year and track and disc numbers from the export (and its album artist, from a library or XML export).
    * Album names are cleaned so every edition groups as one album. Labels like "(Deluxe Edition)", "(Remastered)", "(Bonus Track Version)", " - Single" and " - EP" are removed, while ones naming a different recording ("Live", "Orchestral Version") are kept. "feat." artists are moved out of titles and tagged as featured artists.
    * Nothing is queued twice. Songs already in any of your Jellyfin music libraries or the queue are skipped, and the playlist uses your existing copy. Classical movements count when one side names only the movement: an export's "Suite bergamasque, L. 75: III. Clair de lune" uses your "Clair de lune" by the same artist, and the other way round, as long as the lengths agree and the movement isn't just a tempo like "Allegro". A song appearing more than once keeps its best version (album, then compilation, then EP, then single). If your library only has a song's single and the import brings its album version, the album version is queued and the single's file is deleted once it is imported.
    * New tracks from an album you already have reuse that album's name and year, so they land in its existing folder.
    * The owner's playlist fills in as songs reach the library. Songs you already have are added straight away, the rest once they are downloaded, imported and scanned. If the owner already has a playlist with that name, only the songs it is missing are added, and its songs are put in the export's order (songs the owner added themselves keep their place). Each import is one time: it finishes once every song is in, or once a song's download fails or is removed. Importing again later simply adds what is new and reorders.
3. Download as usual. Each track is matched to a YouTube upload when it downloads, preferring the artist's own channel and an upload close to the track's length. When the regular search only finds music videos of the wrong length (or nothing), YouTube Music's song results are checked too, under the same rules. An upload from another channel is only accepted when its title names the artist, so another performer's recording of the same piece is not picked. For long classical titles ("Suite bergamasque, L. 75: III. Clair de lune"), the movement name is enough to match. A match worth checking (a length difference over 3 seconds, or an upload not from the artist) is noted on the queue row, and a track with no acceptable upload is marked as an error.
4. To fix a wrong or failed match, click the link button on its queue row, paste the right YouTube link and click **Use Link**. The track downloads again from that link and keeps its title, artist, album and other details, and its place in any playlist import.

## Replace a Song Already in Your Library

If a song reached your library with the wrong audio, choose **Replace a Song** on the Download tab, search for the song and click it, paste the right YouTube link and click **Queue Replacement**. The new audio downloads with the song's own tags and cover, and importing it from the [Import](Import.md) tab overwrites the song's file in place, so it keeps its place in the library and in playlists.

Next: [Import](Import.md).
