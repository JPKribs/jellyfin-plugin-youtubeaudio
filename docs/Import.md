# Import

Tag your downloaded files and bring them into your library.

![Import](screenshots/Import.png)

## Tag and Import

1. Each downloaded file has its own panel. Its header shows the title, the file size, and the save and wand buttons, and the cover sits beside the fields for title, track, artist, album artist, featured artists, album, genre, and year. Songs your library already has are marked **In Library** in the header, so you can skip a second copy.
2. To apply the same metadata to multiple files at once, fill in the bulk fields at the top and click **Apply to Selected**.
3. To listen to a file before you tag it, click the play button across the bottom of its panel. The seek bar and the time sit beside it, and starting one file pauses any other.
4. To add or replace cover art, click **Set Cover** or **Replace** under a file's cover, or drop a picture on it. To give several files the same cover, choose it under **Cover** in Bulk Updates, select the files, and click **Set Cover on Selected**. Pictures are cropped to a centered square of at most 1200 pixels. Choosing an album your library already has takes that album's cover. For an album with no art, click the wand in the panel's header to generate a cover. It is a soft blend of three colors drawn from the album name, with the name in the middle.
5. Save each file's changes with the save button in its header before you import. The button turns orange while a file has unsaved changes.
6. Select the files you want to import and click **Import**.

## Where Files Go

Metadata is written directly to the audio files. On import, files are copied into your music library directory following Jellyfin's expected structure:

```
<library path>/Artist/Album (Year)/Song.ext
```

Before a file is filed, its artists are tidied. A joint credit such as "Yo-Yo Ma & Kathryn Stott" is stored as separate artists (the album files under the first), while names Apple or your library know as one act, like "Katia & Marielle Labèque", stay whole. Artists already in your library keep the library's spelling, so "LANG LANG" is filed as "Lang Lang".

Existing artist and album folders are reused when the names differ only in case, punctuation or year: "WIENER PHILHARMONIKER" files under "Wiener Philharmoniker", and "GoLdBeRg Variations (1982)" joins "Goldberg Variations (1981)" and takes its name and year. File and folder names avoid characters that network shares and Windows reject (a ":" becomes " - ") and are kept to 100 characters.

Imported files leave the list and the queue.

Next: [Public Queue](PublicQueue.md).
