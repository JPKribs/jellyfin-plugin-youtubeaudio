# Translations

How this plugin shows its text in the viewer's language. Base builds the strings in and checks them on every build. The full reference is Base's [Translations](https://jpkribs.github.io/jellyfin-plugin-base/Translations/) guide.

## Where the Strings Live

| Strings | Where |
| :--- | :--- |
| Jellyfin's own | `<JellyfinString Include="Key" />` items in `Jellyfin.Plugin.YouTubeAudio/Jellyfin.Plugin.YouTubeAudio.csproj` |
| This plugin's own | `Jellyfin.Plugin.YouTubeAudio/Localization/en-us.json`, one file per language |
| The endpoint pages load them from | `GET /YouTubeAudio/Translations` in `YouTubeAudioController`, which answers with `BaseTranslations.Serve` |

## Rules

* Every word a person reads comes from a key. No hard coded English in a page, a script, or a message the server shows.
* Use a Jellyfin string whenever Jellyfin has the words. Search the web client's `src/strings/en-us.json` and the server's `Localization/Core/en-US.json` before adding a key.
* Add a local key only when Jellyfin has no string for it. Put it in `en-us.json` with its US English.
* One string, one key. The build fails on a key said twice or English said twice.
* Keep placeholders as `{0}`, `{1}`. Never build a sentence from pieces, since word order differs by language.
* A key is PascalCase and says what the text is, such as `HeaderQueue`, `LabelApiKey`, `ButtonRetry`, `ToastSaved`, `ErrorServerUnreachable`.
* Log lines and exception text for admins in the log stay in English and need no key.
* Text the queue database stores, such as a song's error and its match note, stays as written.

## Add a String

1. Search Jellyfin's strings for the words.
2. Found: add `<JellyfinString Include="Key" />` to the csproj.
3. Not found: add `"Key": "English"` to `Localization/en-us.json`.
4. Use it.
    * Markup: `data-str="Key"`, `data-str-title`, `data-str-label`, or `data-str-placeholder`, with the English as the element's own text.
    * Config page script: `t.tr('Key')` or `t.format('Key', value)`, where `t` is `Shared.t`. `createPage` in `youtubeaudio_shared.js` loads the strings with `loadTranslations('YouTubeAudio/Translations')` and applies them to the markup before the page's `bind` runs.
    * A count: `countText(t, count, 'KeyOne', 'KeyMany')`, with the one and the other count as separate keys.
    * Base component text, such as a Save Bar's lines or a Filtered List's **Select all**, comes from Base's own strings. Pass a text option only when it says something different from Base's default.
    * User pages: they import Base's script and call `loadTranslations('/YouTubeAudio/Translations', { userId })` themselves, with the English as `tr`'s fallback, since they run without the dashboard. Their API calls send `t.language` in the `X-Jellyfin-Plugin-Language` header.
    * C#: `BaseTranslations.Get(TranslationKeys.Key, language)` or `BaseTranslations.Format(...)`, with the page's language from `BaseTranslations.RequestLanguage(Request)`, which reads `?language=`, then the `X-Jellyfin-Plugin-Language` header Base's `apiRequest` sends, then `Accept-Language`. A service throws `UserMessages.Argument`, `NotFound`, or `Conflict` with a key, and the controller answers with `UserMessages.ErrorBody(ex, Request)`. Text for everyone, such as an activity log entry, uses `BaseTranslations.ServerLanguage(config)`.
5. Build. Fix every `JPKT` error and warning.
6. Run `TranslationsTests`, which checks that every key the pages use has a string.

## Casing

Every string is in sentence case, mirroring Jellyfin, such as "Allow media playback".

* Write a local string in `en-us.json` in sentence case. Keep proper nouns as written.
* Add a word or phrase the plugin writes with special casing, such as a product name or a format, to `Localization/casing.json`. It is a JSON array, and Base's defaults, such as Jellyfin, URL, and TV, need no entry.
* A Jellyfin string stays as Jellyfin writes it. Add `Case="sentence"` to its `JellyfinString` only when it reads wrong beside its neighbors, such as `AlbumArtist`.
* Text built at runtime takes `ToSentenceCase` or `ToTitleCase` in C#, and `sentenceCase` or `titleCase` on a page. Each keeps the casing.json words.

## Add a Language

Add `Localization/<code>.json`, such as `de.json` or `pt-br.json`, with only keys `en-us.json` has. A missing key falls back to the language's base and then to US English. Jellyfin's strings come in every language Jellyfin has, with no file.

## Checklist

* [ ] No English in a page, script, or server message outside `Localization/en-us.json`.
* [ ] Jellyfin's string used wherever it has the words.
* [ ] Every label is in sentence case, and casing.json lists the words kept as written.
* [ ] The build has no `JPKT` error or warning.
* [ ] The pages show the strings, checked once in another language such as `de`.
