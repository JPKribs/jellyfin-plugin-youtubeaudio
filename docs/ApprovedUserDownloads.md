# Approved User Downloads

Let trusted users queue downloads without giving them admin access.

![Approved User Downloads](screenshots/User-Download.png)

You can allow specific Jellyfin users to submit download links from a simple page, without access to the plugin's admin tabs or the import step.

1. In [Settings](Settings.md#approved-users), open the **Approved Users** section, turn on the users you want to allow, and click **Save**.
2. Share the user page with them. The Settings tab shows no link to it. It is at `/YouTube/Download` after your server address, such as `https://jellyfin.example.com/YouTube/Download`. Approved users sign in to Jellyfin first, and administrators can always use it.
3. On **Request a Download**, they paste a link into **YouTube Link** and fill in **Artist**, **Album** and **Year**. **Artist** and **Album** suggest names already in your library as they type. **Title** is optional and comes from the video when it is empty. A playlist link turns **Title** off, since each video keeps its own title.
4. They click **Submit**. The download starts on the server and the song lands in your queue, ready to import.

The line under **Submit** says what happened. "Queued 1 track for download." confirms a request. A problem shows in red on the same line, such as "Add the YouTube link, artist, album and year." when a field is empty, or the server's reason when it refuses the link.

**View the Queue**, the secondary button at the bottom of the page, opens the [Public Queue](PublicQueue.md).

A playlist link queues each of its videos, up to the **Playlist Limit** set in [Settings](Settings.md#approved-users) (200 by default). A larger playlist is refused rather than cut short, so the user can ask an administrator to add it.

Approved users can only submit links. They cannot import into the library, and they never see the admin tabs. The page is reachable by its address, but the submit endpoint checks approval against the signed in Jellyfin account on the server, so an unapproved or signed out visitor cannot queue anything. A visitor who is not signed in sees **Sign in required** with a **Sign In** button, and a signed in user who is not approved sees **Not authorized** with **Back to Jellyfin**.
