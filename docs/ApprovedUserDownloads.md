# Approved User Downloads

Let trusted users queue downloads without giving them admin access.

![Approved User Downloads](screenshots/User-Download.png)

You can allow specific Jellyfin users to submit download links from a simple page, without access to the plugin's admin tabs or the import step.

1. In [Settings](Settings.md), open the **Approved Users** section, select the users you want to allow, and click **Save**.
2. Approved users sign in to Jellyfin and visit `/YouTube/Download` on your server (for example `https://jellyfin.example.com/YouTube/Download`).
3. They paste a YouTube link, fill in artist, album, year, and title, and submit it. The download then runs on the server and lands in your queue.

Approved users can only submit links. They cannot import into the library, and they never see the admin tabs. The page is reachable by its URL, but the submit endpoint checks approval against the signed in Jellyfin account on the server, so an unapproved or signed out visitor cannot queue anything.
