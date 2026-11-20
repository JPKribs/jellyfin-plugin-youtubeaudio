# Public queue

Let anyone signed in see what is coming.

![Public queue](screenshots/Queue.png)

The public queue is at `/YouTube/Queue` after your server address, such as `https://jellyfin.example.com/YouTube/Queue`. The Settings tab shows no link to it, so share the address yourself. Any signed in Jellyfin user can open it, approved or not, to see what is waiting, downloading, downloaded or failed.

* The pills above the list are **All**, **Waiting**, **Downloading**, **Downloaded** and **Failed**, each with its count. Click one to show only those songs.
* Each row shows the song's title, then its artist, album and source, with its status at the right. The statuses read "Waiting to download", "Downloading", "Downloaded, not yet added" and "Could not be downloaded".
* The list refreshes every 10 seconds, and the line under it shows when it was last updated.

The page is read only and shows titles, artists, albums and sources only. It shows no links, file names or error details. A visitor who is not signed in sees **Sign in required** with a **Sign in** button. When the queue cannot be loaded, the page shows **Queue unavailable** with **Back to Jellyfin**.

Next: [Approved user downloads](ApprovedUserDownloads.md).
