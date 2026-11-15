# Documentation

How to write the README, the docs site, screenshots, and release notes for this plugin. It applies to every page under `docs/`, to `README.md`, and to the `changelog` in `build.yaml`.

## The One Rule

Documentation describes the release it ships with. It reads as if this version were the only one that ever existed.

* Write what the plugin does. Never write what it used to do, what it no longer does, or what it now does.
* Write how a feature works. Never write that a bug was fixed, a problem is gone, or a limit was lifted.
* History belongs in the release notes. See [Release Notes](#release-notes).

| Do not write | Write |
| :--- | :--- |
| Downloads now survive a restart. | Songs that were downloading go back to the queue when the server starts. |
| The Stream option was removed. | Leave it out. |
| No longer reads older databases. | Reads queue databases from 12.4.1.0 or later. |
| Fixed an issue where covers were missing. | The album cover is embedded in each downloaded file. |
| New in 12.4.5.0, Replace a Song swaps... | Replace a Song swaps... |
| This release carries no upgrade steps. | Version 12.4.5.0 carries no upgrade steps. |

"Now" and "no longer" are fine when they describe state rather than history. "The song is no longer in the queue" describes what the plugin sees, so it stays.

A version number belongs in docs only when the reader needs it to act, such as the minimum Jellyfin version or the oldest release that can upgrade directly. Keep those facts in [Installation](../docs/Installation.md) and [Versioning](../docs/Versioning.md), and name the version outright instead of "this release" or "an older release".

Remove compatibility notes for versions the plugin no longer supports. The plugin requires Jellyfin 12.4, so text about 12.2 describes servers that cannot run it.

## Writing Style

### Sentences

* Use present tense and plain declarative sentences.
* Describe the system in the third person. "YouTube Audio files the song under its album."
* Address the reader in the second person only in steps. "Select **Queue Playlist**."
* State one fact per sentence. Aim for 20 words or fewer and split anything over 30.
* Use no contractions. Write "cannot", not "can't".
* Use no em dashes, en dashes as dashes, or semicolons. Split the sentence, or join it with a comma and "and".
* Use colons only to introduce a list or a code block.
* Use parentheses rarely. Move the aside into its own sentence.
* End every sentence with a period.
* Write compounds as separate words the way the UI does, such as "one time setup" and "sign in".
* Write one paragraph per line. Never hard wrap prose.
* Use no marketing words such as "powerful", "seamless", "easily", or "simply".
* Write examples with generic names and `example.com` addresses, never a real server, person, or address. Public domain music, such as Debussy and Satie, makes safe song examples.

### Headings

* Use Title Case for every heading. "## Replace a Song Already in Your Library".
* Page titles are `#` and appear once per page.
* Name the thing, not the action, unless the section is a procedure. "## Settings" and "## Add the Plugin Repository" are both correct.
* Never rename a heading without fixing its anchors. `mkdocs build --strict` fails on a broken anchor.

### UI Names

* Write a label exactly as the page shows it, in bold. **Playlist Limit**.
* Write a path through the UI with `>` between the steps, in bold. **Dashboard > Plugins > YouTube Audio > Settings**.
* Write a tab as its label, in bold. **Download**, **Import**, **Settings**.
* Quote on-screen messages in double quotes. "Download started."
* Write routes, files, settings keys, and values in code. `POST /YouTubeAudio/Queue/Process`.

A docs page and the UI must use the same word for the same thing. When a label changes, search `docs/` for the old one and change it everywhere in the same commit.

## Site Structure

The docs site is MkDocs with the Material theme, built from `docs/` by `mkdocs.yml` and published to `https://jpkribs.github.io/jellyfin-plugin-youtubeaudio/`.

### Navigation

Keep the nav in this order.

1. **Overview** (`index.md`). What the plugin is and how it works.
2. **Installation** (`Installation.md`).
3. **Getting Started**. Settings, Download, Import, Public Queue, and Approved User Downloads, in the order a new user meets them.
4. **Development**. The End to End Test (`Testing.md`).
5. **Reference**. Versioning.

Name page files in PascalCase with no spaces, such as `PublicQueue.md`.

## Page Templates

A tab page follows this shape.

```markdown
# Download

One sentence that says what the tab is for.

![Download](screenshots/Download.png)

## <One Section per Task>

Next: [Import](Import.md).
```

A feature page opens with one sentence on what the feature is, then one section per question a reader asks, then **Security** and **Limitations** where they apply.

An API route follows this shape.

````markdown
### POST /YouTubeAudio/Example

One sentence on what the route does and who calls it.

```json
{ "Example": true }
```

| Status | Meaning |
| :--- | :--- |
| `200` | The change was saved |
````

### Settings References

List settings as bullets in the order the page shows them. Start each with the label in bold and follow it with a verb.

```markdown
* **Embed Cover Art** adds the album cover to each downloaded file.
```

When a page has many settings with defaults, use a table instead.

| Setting | What it does | Default |
| :--- | :--- | :--- |
| **Embed Cover Art** | Adds the album cover to each downloaded file | On |

Table cells have no closing period. Keep them to one sentence.

### Troubleshooting

Write trouble as a table of what the reader sees and what to do.

| Reason | What to do |
| :--- | :--- |
| No acceptable upload for *title* | Select **Change Link** on its row and paste the right YouTube link |

Put the exact on-screen text in the first column. Use italics for a placeholder inside it, such as *server*.

### Admonitions

Give every admonition a short title.

```markdown
!!! warning "Use at your own risk"
    One or two sentences.
```

* `note` for a detail the reader needs.
* `tip` for a better way to do something.
* `warning` for anything that can lose data or break a server.
* `info` for a requirement, such as both servers running the plugin.

### Links

* Link pages relatively with their `.md` name, such as `[Settings](Settings.md#approved-users)`.
* End a Getting Started page with one "Next" line, such as `Next: [Import](Import.md).`
* Link to Base's README for a shared component instead of describing it again.

## README

The README is a landing page. The docs site holds the detail.

1. The icon, `<img src="docs/assets/icon.png" alt="YouTube Audio" width="96" align="left">`, then a one paragraph pitch and `<br clear="left">`.
2. `## Documentation`. One sentence on what the docs cover, a bold link to the site, then the repository URL for Jellyfin in a code block.
3. Any closing section the owner keeps, unchanged.

Use `##` for README headings. Keep the README under 40 lines.

## Screenshots

### What to Capture

* Capture every page and dialog a docs page describes.
* Capture the dark theme only, which is Jellyfin's default.
* Capture from the end to end container, run with `--keep`, so every name is generic.
* Capture after a download has run so the queue, its counts, and its badges are filled.
* Hide nothing with editing. When a value is private, change the test server data instead.

### Sizes

| Kind | Size | How |
| :--- | :--- | :--- |
| Dialog | 800 wide | The browser at 800 px, cropped to the dialog |
| Full page | 1920 wide | The browser at 1920 px, cropped to the content area |
| Phone | 375 wide at 2x | The browser's device mode at 375 px |

Save every capture as PNG. Crop out the browser frame and the Jellyfin header unless the header is the subject.

### Names and Placement

* Save to `docs/screenshots/`.
* Name the file in Title Case after what it shows, with a hyphen for a space. `Settings.png`, `User-Download.png`.
* Link with alt text that names the subject. `![Approved User Downloads](screenshots/User-Download.png)`.
* Place two related shots side by side in a table whose header row holds the captions.

```markdown
| Queue | Queue on a phone |
| :---: | :---: |
| ![Queue](screenshots/Queue.png) | ![Queue on a phone](screenshots/Queue-Phone.png) |
```

### When to Retake

Retake a screenshot in the same commit as any change that alters what it shows, including a label, a tab, a layout, or the Jellyfin version in the header. A new Base version changes how every component looks, so retake every screenshot when it is raised. A screenshot that shows a tab or label the plugin no longer has is a bug.

## The Test Server

The end to end test can leave its Jellyfin running for screenshots and manual checks. Docker and the dotnet SDK must be on the path.

```bash
e2e/run.sh --keep
```

The server answers at http://localhost:8097, and the run's throwaway logins are in the ignored `e2e/.work/`. Stop it with `docker rm -f yta-e2e`. Take screenshots only after the run finishes. Reload the page after a new build, since the dashboard caches scripts.

## Installing a Build by Hand

```bash
./build.sh
```

The zip lands in `dist/`. Unzip it into `<config>/plugins/YouTube Audio_<version>/` on any Jellyfin 12.4 server and restart the server.

## Building the Docs

```bash
pip install mkdocs==1.6.1 mkdocs-material==9.7.7
```

```bash
mkdocs serve -a 127.0.0.1:8765
```

```bash
mkdocs build --strict
```

Run the strict build before every docs commit. CI runs the same build and refuses broken links, anchors, and nav entries.

## Release Notes

Release notes live in the `changelog` of `build.yaml`, under four keys. The publish workflow copies them into `manifest.json`, so every line reaches users' plugin catalogs.

| Key | Holds | Voice |
| :--- | :--- | :--- |
| `added` | A feature that did not exist | The feature name, then what it does |
| `changed` | Behavior a user will notice | The new behavior, in present tense |
| `fixed` | A bug | What went wrong in past tense, then the fix in present tense |
| `developer` | Routes, schema, tests, and tooling | Plain statements for other developers |

```yaml
changelog:
  added:
    - Replace a Song, which swaps a library song's audio for a different YouTube upload
  fixed:
    - A playlist import could queue a song twice. Each song is queued once
```

* This is the one place that may say "now", "no longer", and "was".
* Write full sentences with no closing period.
* Write one entry per change a user would notice. Fold internal steps into the change they serve.
* Name UI labels exactly as the page shows them, without bold or backticks.
* Name the Base version in `developer`, with what it replaced locally.
* Give a new or changed route or query parameter a `developer` line.

## Commit Messages

* Write an imperative sentence in sentence case with no closing period. "Keep the album folder when its year differs".
* Describe the behavior, not the files. Use no prefixes such as `fix:` or `feat:`.
* Add a body of plain paragraphs, wrapped at 72 characters, when the reason is not obvious.
* Commit docs and screenshots with the change they describe.

## Checklist

* [ ] Nothing describes an earlier version, a removed feature, or a fixed bug.
* [ ] Every UI label matches the page exactly and is bold.
* [ ] Every heading is Title Case.
* [ ] No em dashes, semicolons, or contractions.
* [ ] Every screenshot matches the current UI and uses test server data.
* [ ] Every term matches [glossary.md](glossary.md).
* [ ] `mkdocs build --strict` passes.
* [ ] `build.yaml` has a changelog entry for any change a user would notice.
