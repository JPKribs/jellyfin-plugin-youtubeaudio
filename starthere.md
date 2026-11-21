# Start Here

Read this file before you change anything in this repository. It applies to people and to AI assistants alike.

## The Guides

Each guide in [skills](skills/) covers one kind of work. Read the guide that matches the task before you start it.

| Guide | Read it before you |
| :--- | :--- |
| [skills/glossary.md](skills/glossary.md) | Name anything, in the UI, the docs, or code. |
| [skills/style.md](skills/style.md) | Build or change anything a user sees, including labels, messages, and layout. |
| [skills/code.md](skills/code.md) | Write or change code or comments in any language. |
| [skills/testing.md](skills/testing.md) | Write or run tests, or the Docker end to end test. |
| [skills/profiling.md](skills/profiling.md) | Find or fix slow C#, or change code on a hot path. Profile only for a reported symptom, a hot path change, or a number that grew. |
| [skills/documentation.md](skills/documentation.md) | Write or change the README, the docs site, screenshots, or release notes. |
| [skills/review.md](skills/review.md) | Call any change done. |
| [skills/release.md](skills/release.md) | Cut a release. |
| [skills/branches.md](skills/branches.md) | Pick a branch, sync the two, and promote unstable. |
| [skills/translations.md](skills/translations.md) | Add or change a string a person reads. |
| [skills/logging.md](skills/logging.md) | Write an activity log entry or a server log line. |

A task that touches several areas needs every matching guide. A new setting, for example, needs the glossary, style, code, testing, documentation, and review guides.

## Ground Rules

* **Base comes first.** Anything reusable lives in [JPKribs.Jellyfin.Base](https://github.com/JPKribs/jellyfin-plugin-base). Search it before you build anything, use what it has, and never copy or override it locally.
* **Describe the current release only.** Docs, labels, and comments say what the plugin does today. Release notes are the only place for what changed.
* **Keep it short.** Every label, message, and comment is as brief as it can be while staying clear.
* **One word per concept.** Use the term in the glossary, everywhere.
* **Prove it with a test.** A change is not done until a test shows that its inputs give the right output.

## Verify Before Done

Work is done when it has been checked, not when it has been written.

* Run every check the change needs, and read its output. A check that did not run has not passed, and neither has one that ran before the last change.
* Look at every changed page on a test server, after a reload.
* Read the whole diff before reporting, using [skills/review.md](skills/review.md).
* Check every new string against the style checklist and the glossary.
* Check every claim in a doc against the code or the running plugin.
* Report what was checked, what was not, and why. Never call work done while a check fails.

## Stop and Ask

Stop and ask before any of these, even when the task seems to call for it.

* **The queue database.** A schema change, a migration, a change to the minimum reader, or anything that resets, moves, or deletes `queue.db`.
* **Downloading.** A change to how yt-dlp is run, how a match is picked, or what a download writes to the download cache.
* **The tools.** A change to how yt-dlp and Deno are fetched, checked against their checksums, updated, or run.
* **The library.** Anything that writes, renames, moves, replaces, or deletes files in a music library, including the duplicate rules and replacing a library single.
* **The public pages.** A change to `/YouTube/Download` or `/YouTube/Queue`, to who may use them, or to what they show.
* **Security.** A change to authentication, the approved users check, the accepted YouTube hosts, or the Playlist limit.
* **Base.** Any change to Base, since every plugin takes it.
* **Settings.** Renaming, removing, or changing the default of a setting users already have.
* **Releases.** Raising the version, publishing, or pushing to `master`.
* **The rules.** Any case where following a guide would make the result worse.

Explain what you would change, why, and what it risks. Wait for an answer before you act.

## The Repository

| Path | What it holds |
| :--- | :--- |
| `Jellyfin.Plugin.YouTubeAudio/` | The plugin source, laid out as [Layout](skills/code.md#layout) shows. |
| `Jellyfin.Plugin.YouTubeAudio/Web/` | The dashboard pages and the public pages, as HTML and JS. |
| `Jellyfin.Plugin.YouTubeAudio/Localization/` | The plugin's own strings, in `en-us.json`. |
| `Jellyfin.Plugin.YouTubeAudio.Tests/` | Unit, store, and controller tests, with the opt in download tests. |
| `tools/` | Development tools that never ship, such as the screenshot kit and the end to end test. |
| `tools/e2e/` | The Docker end to end test that runs the plugin on a throwaway Jellyfin. |
| `docs/` | The documentation site, built with MkDocs. |
| `build.yaml` | The version, the artifacts, and the release notes. |
| `build.sh` | Builds the release zip into `dist/`. |
| `skills/` | The guides. |
| `../Shared/jellyfin-plugin-base/` | A local checkout of Base, when present. Read it, and never change it from work on this plugin. |
