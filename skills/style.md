# Style

How every page, dialog, label, and message in this plugin looks and reads. It is drawn from Base and from all nine JPKribs plugins, so the same rules carry to each of them.

Base is [JPKribs.Jellyfin.Base](https://github.com/JPKribs/jellyfin-plugin-base). Its README documents each component's options. This guide says which component to use, when, and what to write in it.

## Principles

1. **One component per concept.** When Base has it, use it. Look before you build, per [Find the Component](#find-the-component). When Base lacks it and a second plugin needs it, add it to Base. Never build a second version.
2. **Short beats complete.** A label is a few words. A description is one sentence. A message is one sentence.
3. **The page explains itself.** A reader understands a setting from its label and its one line description, without the docs.
4. **The same thing has the same name everywhere.** The label, the docs, the message, and the activity log use one word for one concept.
5. **Follow Jellyfin.** Use Jellyfin's own markup and theme, so the plugin looks like part of the dashboard.

## Writing

### The Rules

These rules apply to every string a user can see. That includes HTML, JS, C# messages the API returns, and activity log entries.

* **No em dashes or en dashes as punctuation.** Split the sentence, or join it with a comma and "and". An en dash in a numeric range, such as `22–6`, is fine.
* **No semicolons.** Split the sentence.
* **Avoid colons.** Never end a label with one. In a sentence, rewrite it. "How fast files move: downloads and uploads." becomes "Sets how fast files move in each direction."
* **One sentence.** Two only when the second gives the fix or the consequence.
* **End every sentence with a period.** Labels, buttons, titles, tabs, and table cells are not sentences and take no period.
* **No "please", "successfully", "simply", or "just".**
* **No exclamation marks.**
* **No ALL CAPS for emphasis.** Say why it matters instead.
* **No "(s)".** Write the right plural for the count.
* **No contractions.** Write "cannot", not "can't".
* **No "e.g." or "i.e.".** Write "such as".
* **American spelling.** Color, behavior, favorite, center.
* **Use the reader's words.** "Song" and "library", not "queue item" and "collection folder".

### Casing

| Text | Case | Example |
| :--- | :--- | :--- |
| Tab | Title Case, one word | Download |
| Section and collapsible title | Title Case | Import Behavior |
| Field label | Title Case | Target Library |
| Checkbox label | Title Case | Embed Cover Art |
| Button | Title Case, verb first | Queue Playlist |
| Dialog title | Title Case | Change Link |
| Badge and status chip | Title Case | Downloading |
| Column header | Title Case | Status |
| Activity log name | Title Case | 3 Songs Imported |
| Description, message, placeholder sentence | Sentence case | Adds the album cover to each downloaded file. |

Title Case capitalizes every word except short articles, conjunctions, and prepositions in the middle, such as "a", "and", "of", "to", and "in". Keep acronyms as written, such as "API Key", "URL", "Mbps", and "GB".

### Words to Use

Pick the word on the left every time.

| Use | Not | When |
| :--- | :--- | :--- |
| Remove | Delete | Takes an entry out of a list or the config, and nothing on disk is lost |
| Delete | Remove | Destroys data, files, or history that cannot come back |
| Clear | Empty, Reset | Removes every entry in a list or log |
| Reset | Restore | Puts settings back to their defaults |
| Save | Apply, Update | Writes changes to the config |
| Done | OK, Close | Closes an editor and keeps its changes |
| Cancel | Close, No | Closes without keeping changes |
| Add | New, Create | Puts a new entry into a list on the page |
| New | Add, Create | Starts a new record in a record picker |
| Queue | Add, Submit | Puts songs into the queue |
| Import | Move, Publish | Files downloaded songs into the library |
| Select | Pick, Choose, Tick, Click | Any instruction to make a choice |
| Turn on, Turn off | Enable, Disable, Tick, Untick | An instruction about a checkbox or toggle |
| Leave blank | Leave empty | An empty field means the default |
| YouTube URL | Link field, Address | A YouTube video or playlist link |
| Could not | Failed to, Unable to | An error message |

The control names its own result. Deleting a queue item drops its cached file, so it is Delete. Stopping a playlist import keeps the Jellyfin playlist, so it is Stop.

### Labels

* Two to four words. Name the thing the field holds, not the action.
* Put a unit at the end in lowercase parentheses, such as **Timeout (seconds)**. Use symbols for standard units, such as `GB`.
* No colons, periods, or question marks.
* A checkbox label names the feature it turns on, never "Enable X". Write **Embed Cover Art**, not **Enable Cover Art Embedding**.

### Descriptions

A `fieldDescription` sits under every field that is not obvious from its label.

* One sentence that says what the setting does, starting with a verb. "Adds the album cover to each downloaded file."
* Add a second sentence only for the default or a consequence. "0 is no limit."
* Describe the current behavior only. Never "now" or "no longer".
* Never repeat the label.
* Put units, ranges, and what blank means here, never in the label or the placeholder.

| Bad | Good |
| :--- | :--- |
| When enabled, importing a file that already exists will overwrite it. | Overwrites a file the library already has. |
| Choose the text it shows, top to bottom; use the arrows to change the order. | Select the text it shows, top to bottom. |
| Absolute path to the yt-dlp binary (e.g. /opt/homebrew/bin/yt-dlp). | The absolute path of your own yt-dlp. |
| This setting enables the embedding of cover art into downloaded files. | Adds the album cover to each downloaded file. |

A section intro is one `fieldDescription` right under the section title. It says what the section is for in one sentence.

### Placeholders

A placeholder shows an example of a valid value. It is never the only place an instruction lives, since it disappears once the reader types.

* Show a realistic example with no prefix. `https://www.youtube.com/watch?v=dQw4w9WgXcQ`, not `e.g. https://...`.
* Use `example.com` addresses and generic names only.
* For an override that falls back to a default, show the value it falls back to. When that value is not known, write `Default`.
* For a search box, write "Search" and the noun, with no ellipsis. `Search queue`.
* For a secret already stored, Base's `bindSecretField` writes "Saved, leave blank to keep". Leave it as is.
* Never put a sentence in a placeholder. Move it to the description.

### Buttons

* A verb, or a verb and a noun. **Save**, **Queue Playlist**, **Import**.
* Name the object when the page has more than one of that action. **Queue Playlist** and **Queue Replacement** sit on the same page.
* A primary Save is **Save**, even when each section has its own.
* An icon only button carries a `title` of one to three words, such as `Remove`, `Move Up`, or `Retry`. Never a sentence.
* A button that runs for a while shows progress in place. See [Progress](#progress).

### Messages

A message reports what happened in one sentence that ends with a period.

| Kind | Pattern | Example |
| :--- | :--- | :--- |
| Saved | `<Thing> saved.` or `Saved.` | Library saved. |
| Done | `<Thing> <past verb>.` | Queue reset. |
| Copied | `<Thing> copied.` | Link copied. |
| Working | `<Verb>ing…` | Importing… |
| Could not | `Could not <verb> the <thing>.` | Could not save the tags. |
| Could not, with a fix | Add one sentence with the fix | Could not load the settings. Reload the page. |
| Missing input | `Enter <the thing>.` or `Select <the thing>.` | Enter a YouTube URL. |
| Nothing to do | `No <things> to <verb>.` | No downloads to cancel. |

* Use the ellipsis character `…`, never three dots, and only for work in progress.
* When the server gave a reason, show it with `shared.errorText(err, 'Could not save the server.')`.
* Name the count with a correct plural. "Deleted 3 files." and "Deleted 1 file."
* Never say "error occurred". Say what could not happen.

### Confirm Dialogs

A confirm asks one question and states the consequence.

```js
confirmDialog('Delete 3 items from the queue? This cannot be undone.', {
    title: 'Delete Items',
    confirmText: 'Delete',
    destructive: true
});
```

* The message is a question, then one sentence on the consequence. "Delete 3 items? This cannot be undone."
* The title is the action in Title Case, such as **Delete Items**. Never leave the default **Confirm**.
* The confirm button repeats the action verb, such as **Delete** or **Stop**. Never **OK** or **Yes**.
* Set `destructive: true` for anything that loses data.
* Never "Are you sure you want to". Ask the question directly.
* Never use `window.confirm` or `Dashboard.confirm`.

### Empty States

| Where | Pattern | Example |
| :--- | :--- | :--- |
| List under a form | `No <things> yet. Add one above.` | No songs queued yet. Add one above. |
| List filled elsewhere | `No <things> yet. <Where they come from>.` | No downloaded files yet. Download songs on the Download tab. |
| Search results | `No <things> match.` | No library songs match. |

Give a Base list its sentence as `emptyText`. Use `shared.emptySection(text)` for a region with no list.

### Activity Log

Use `ActivitySummary` and `ActivityText` from Base.

* The name is short and Title Case. "3 Songs Imported".
* The overview is one sentence with no closing period, as Base writes it.
* Use the same verb the UI uses for the action.

### Messages From C#

The API returns text that the page shows as is, so it follows the same rules.

* A full sentence that starts with a capital and ends with a period. "Only YouTube links are accepted."
* Say what is wrong and, when it helps, what to do. "This playlist has more videos than the Playlist Limit. Ask an administrator to add it."
* Never return an exception message, a stack trace, or a type name.

## Color

### Tokens

Use Base tokens. They follow the dashboard's theme, light themes included. Never write a hex value for something these cover.

| Token | Use |
| :--- | :--- |
| `--base-accent` | Links, focus rings, primary buttons, progress |
| `--base-accent-hover` | Hover on accent elements |
| `--base-fg` | Washes on elements that fade their own text |
| `--base-surface` | Panel and dialog backgrounds |
| `--base-border` | Every border and divider |
| `--base-gap-wide` | Space between components, such as one field row and the next |
| `--base-gap-narrow` | Space inside a component, such as between a label and its field |
| `--base-mono` | Code, ids, paths, and raw errors |
| `--base-success`, `--base-warning`, `--base-error` | Solid status colors |
| `--base-<color>-bg`, `--base-<color>-fg` | Tinted badge and card backgrounds and text, for green, red, orange, purple, blue, and gray |

### Meaning

A color means the same thing on every page and in every plugin.

| Color | Means | States |
| :--- | :--- | :--- |
| Green | Done and healthy | Downloaded |
| Red | Failed, or about to lose data | Error |
| Orange | Waiting, or needs attention soon | Queued, Check Match, In Library |
| Purple | Working right now | Downloading |
| Blue | Informational | Imported |
| Gray | Off or not applicable | Songs Only |

* Map a status to a Base badge state through `STATUS_STATES` in `youtubeaudio_shared.js`, which Base colors through `BADGE_STATES`. Never keep a color map in the plugin.
* Disabled is gray when it means off. Red is only for failure.
* Use "orange" for the warning color in class names, never "amber", "yellow", or "warning".
* Never use color alone. A badge always has a word, and a dot always has a `title`.

## Page Structure

### Shell

Every config page uses Jellyfin's page shell.

```html
<div id="YouTubeAudioSettingsPage" data-role="page" class="page type-interior pluginConfigurationPage withTabs"
     data-require="emby-input,emby-button,emby-select,emby-checkbox"
     data-controller="__plugin/youtubeaudio_settings.js">
    <div data-role="content">
        <div class="content-primary">
            <div class="verticalSection">
                <div class="sectionTitleContainer flex align-items-center">
                    <h1 class="sectionTitle">Settings</h1>
                </div>
                <div class="fieldDescription">Sets where songs go and how they are downloaded.</div>
            </div>
        </div>
    </div>
    <link rel="stylesheet" href="configurationpage?name=youtubeaudio_jpkribs_shared.css">
</div>
```

* List `data-require` in the order `emby-input,emby-button,emby-select,emby-checkbox`, dropping any the page does not use.
* Use no inline `style` attributes. Add a `yta-` class to the page's `<style>` block, or to Base when it is reusable.

### User Pages

`/YouTube/Download` and `/YouTube/Queue` are content fragments the controller renders through Base's status template with `StatusPage.Render`. The template loads no Material Icons and no Base script.

* Use only the template's classes, `base-status-card`, `base-fields`, `base-field-pair`, `base-hint`, `base-submit`, `base-btn`, `base-btn-secondary`, `base-btn-stack`, `base-status-error`, `base-note`, `base-spinner`, and `base-logo`, and `hidden` to switch states.
* Never use an icon ligature or glyph. The template has no icon font, so the icon's name shows as text.
* Show a result in the line under the form. An error takes `base-status-error`, which is red and hides itself when empty, and any other result takes `base-note`.
* A second action, such as **View the Queue**, is a `base-btn base-btn-secondary` link under the primary one.
* The Public Queue imports Base's shared CSS and script itself, for its Record List, Pill Group, and status badges. It uses only components that draw no icons.
* Hide the template's loading heading and spinner when the page leaves its loading state.

### Tabs

* One registered page per tab, wired with `createConfigPage` and `setTabs`.
* Tab names are one Title Case word.
* Keep the tab list in one place, the plugin's shared module, never copied into each page.
* Order tabs from what is used most to what is set once. YouTube Audio is **Download**, **Import**, **Settings**.

### Sections

| Need | Component |
| :--- | :--- |
| A page with several independent groups of settings | `base-collapsible-section`, one per group |
| A group that is always open | `base-collapsible-section fixed`, whose header is a `div` with no icon, so it matches the collapsible sections around it |
| A heading inside a section | `base-subsection-title` |
| An ordered setup | `base-collapsible-section numbered`, which Base numbers in order |
| Settings that only matter when a checkbox is on | `base-indent` under the checkbox, shown with `shared.bindReveal` |

* A collapsible header is a `button.base-collapsible-header` holding `base-collapsible-title` and `base-collapsible-icon`. Base replaces the icon with `expand_more`.
* Start collapsibles closed, except the first one on a page someone opens to act on.
* Give every top level group on one page the same header. A chart or a summary takes the `fixed` section, never a bare `base-subsection-title`.
* Hide a whole section while its feature is off or it has nothing to show. Never show an empty section.
* Put anything that depends on a picker under that picker, and redraw it when the pick changes.

### Saving

Every save is Base's `createSaveBar` with `fill: true`, and its button reads **Save**. Pick one model per page and keep it.

| Model | Use it when | Save |
| :--- | :--- | :--- |
| One Save per page | Settings depend on each other | One Save Bar at the bottom of the page |
| One Save per section | Sections are independent and long | One Save Bar at the bottom of each section's content |
| Editor dialog | Each record is edited on its own | **Done** in `openEditorDialog` |

* The Save Bar is the last element of what it saves. Nothing sits beside it or below it in that section, and no divider line sits above it.
* Other actions that belong with the save, such as Update yt-dlp, go in one `base-button-row` directly above the Save Bar, each `raised base-button secondary fill`, or `destructive fill` for one that deletes or resets data. Base spaces that row. Its buttons have no margin of their own, so their edges line up with the Save Bar's, the row sits one `--base-gap-narrow` above the Save Bar, and its buttons use the same font weight as Save. A plugin adds no margin, gap, or weight of its own. The Save Bar's element goes directly after the row, never inside a wrapper, since Base spaces them as siblings.
* Never hand place a Save button, use a `small` Save, or keep a status span beside one. The Save Bar shows the unsaved, saving, saved, and failed lines.
* Call the bar's `setDirty(true)` when a field in what it saves changes, and clear it after the page loads.
* `onSave` returns the save promise, so the bar shows saved or failed. Use `shared.saveConfigWith` for get, change, and save. The Settings tab saves one section at a time this way.
* Guard leaving the page with `page.guardUnsaved`.

## Components

Every component below is in Base. Use it as Base ships it. When it does not fit, change Base.

### Find the Component

Before writing markup for a list, a row editor, a chart, or a picker, look for it in Base.

```bash
grep -o "export function create[A-Za-z]*" ~/.nuget/packages/jpkribs.jellyfin.base/<version>/assets/jpkribs_shared.d.ts
```

* The typings in that file document every option of every component. Read the component's block before using it.
* Items to search, filter by status, select, and act on are a `createFilteredList`. Rows of fields with Add and Remove are a `createRepeater`. A list of checkboxes is a `createCheckboxList`. A short list the user ranks is a `createOrderedList`. A file to upload is a `createFilePicker`.
* A hand built version of any of these is a defect, even when it looks the same.
* When a Base component lacks one thing the page needs, use the component and report the gap to Base. Never fall back to building it by hand.
* Never put a stand-in where Base has the option, and never keep a Base gap silently. Name the missing option in [Hand Built Pieces](#hand-built-pieces) and ask the Base owner for it.
* A component used wrong is worse than none. Check the result on a real server, not just in the code.

### Fields

| Need | Use |
| :--- | :--- |
| Text, number, or URL | `inputContainer` with `<label for>`, `<input is="emby-input">`, and a `fieldDescription` |
| Choice from a short list | `selectContainer` with `label.selectLabel` and `<select is="emby-select">` |
| On or off | `checkboxContainer checkboxContainer-withDescription` with `emby-checkbox` and `fieldDescription checkboxFieldDescription` |
| A mode that needs a sentence per option | `createChoiceGroup` with `base-choice` cards |
| Several on or off options in a row | `base-toggle-group` with `base-toggle` pills |
| Many checkboxes | `base-check-grid` |
| Many items from a long or searchable list | `createCheckboxList`, with `renderRow` giving each row a `label`, a `sublabel`, and an `image` loader |
| A short list the user ranks | `createOrderedList` |
| A file to upload | `createFilePicker` |
| A picture, such as a cover | `createImagePicker`, with `removable: false` when the image can only be replaced |
| One number on a range, such as an opacity | `createSliderField` |
| A color | `createColorPicker` |
| A code, path, or link to copy | `createCopyField` |
| A name looked up from the library | `shared.createSearchableComboBox` |
| Rows of several fields that are added and removed | `createRepeater` |
| Pairs of a name and a value | `createKeyValueEditor` |
| A list of plain text values | `createStringList` or `createTagInput` |
| Typeahead with removable chips | `createChipSelect`, with `single: true` for one choice |
| One user | `createUserSelector` |
| Several users | `createUserMultiSelector` |
| Several fields side by side | `base-field-row` with `base-field` |
| A secret | `bindSecretField`, `readSecretField`, and `clearSecretField` |
| A template with `{name}` tokens | `placeholderHint` and `bindPlaceholders` |
| Code or JSON, read only | `base-code-block` |

* Use `emby-select`, never `base-selector-dropdown`, for a field. `base-selector-dropdown` is only for the record picker.
* Every input has a `<label for>`.
* Mount a Base component that holds its own inputs, such as a File Picker, Checkbox List, or Filtered List, in a `selectContainer` with its `<label>` and `fieldDescription`, never in an `inputContainer`. Base widens every text and file input in an `inputContainer` to the full width, and exempts only checkboxes and radios.
* Never hand roll a slider, a color picker, a copy field, or an image picker.
* A lookup field must look up. Type part of a real name and see the list fill before calling it done.
* Every row is visibly separate from the next, and every control says what it does beside it. A lone toggle word at the far edge of a row is not a label.

### Buttons

| Need | Class |
| :--- | :--- |
| Primary action | `raised base-button primary` |
| Destructive action | `raised base-button destructive` |
| Secondary action | `raised base-button secondary` |
| Compact action in a section, never in a save's action row | add `small` |
| Save | `createSaveBar` with `fill: true`, never a hand placed button |
| Action that belongs with a save | `raised base-button secondary fill` in the `base-button-row` above the Save Bar |
| Icon and label in a toolbar | `raised base-button` holding a `material-icons` span and a label |
| Icon only in a row | `base-row-btn`, with `.dirty` for unsaved and `.success` after saving |

* Every button is `<button is="emby-button" type="button">`.
* Icons are `<span class="material-icons" aria-hidden="true">name</span>`.
* Close a dialog with `base-dialog-close` and `&times;`. Never "✕" or another glyph.

| Action | Icon |
| :--- | :--- |
| Add | `add` |
| Edit | `edit` |
| Remove or delete | `delete` |
| Copy | `content_copy` |
| Move up and down | `keyboard_arrow_up`, `keyboard_arrow_down` |
| Drag | `drag_indicator` |
| Expand | `expand_more` |
| Retry | `refresh` |
| Open | `chevron_right` |
| Import and export | `upload`, `download` |
| Show | `visibility` |

### Records

| Need | Use |
| :--- | :--- |
| One record at a time from a list | `base-selector` with New, Rename, and Remove |
| Records in priority order | `createSortableCardList` |
| A plain list of records | `base-card-list` with `base-card-item-*` |
| Edit one record in a dialog | `openEditorDialog` |
| A large searchable list from a paged route | `createPaginatedTable` |
| Items to search, filter, select, and act on | `createFilteredList`, with `kind: 'cards'` for status counts |
| Edit table rows in place | `base-bulk-edit-bar` and `base-edit-row` |
| Uploaded files | `base-asset-list` |
| A thumbnail in a table row | A column `image(item)` loader with `imageShape`, one shape for each column |
| A mark on a table row, such as picked | The table's `rowClass(item, index)` |

* A record picker's actions read **New**, **Rename**, **Remove**, then **Import** and **Export**, in that order.
* Use `createFilteredList` for a list the page already holds, and `createPaginatedTable` for one a route pages through.

### Status

| Need | Use |
| :--- | :--- |
| The state of one item | `shared.statusBadge(text, state)` |
| A label on an item | `shared.badge(color, label)` |
| Counts at the top of a page | `shared.renderCards` with `base-cards`, and `compact` for many counts on a phone |
| Counts that filter a list | The Filtered List's `kind: 'cards'` filter, as the queue's status cards |
| Label and value pairs | `base-record-card` |
| A notice in the page | `base-info`, `base-success`, `base-warn`, `base-error`, `base-neutral` |
| Raw failure text | `base-error-message` |
| The result of an action | `shared.setStatus` |
| A row of reset actions | `base-reset-group` with `base-reset-row` |

* Never use `Dashboard.alert` for a result. It blocks the page. Use `setStatus`.
* A notice holds one sentence. Use a callout for something the reader must know before acting, not for a result.
* Count cards in one row each take their own color, and no two neighbors match. The queue's status cards are orange **Queued**, purple **Downloading**, green **Downloaded**, and red **Error**.

### Charts

| Need | Use |
| :--- | :--- |
| Parts of a whole | `createPieChart`, with `donut` and the total in `centerText` |
| How much of a limit is used | `createUsageMeter`, with `bytes: true` for sizes |
| Values compared across names | `createBarChart` |
| A value over time | `createLineChart` or `createSparkline` |
| One number with a label | `createStatCard` |

* Pick the chart by the question it answers. A single used of total bar is a Usage Meter, never a Bar Chart.
* Name each slice with a glossary term in Title Case, such as **Downloaded**.
* Take colors from Base's series colors. Set a color only when it carries a [meaning](#meaning).
* Give every chart a `label`, and an `emptyText` sentence for when it has nothing to draw.
* Scope a chart to what the page shows.

### Media

| Need | Use |
| :--- | :--- |
| A song or file in a list, with its artwork, status, and actions | `createMediaCard`, with `shape: 'square'` and the `music_note` icon |
| A player for a song | `createAudioPlayer`, with `size: 'small'` in a list |
| A cover to set or replace | `createImagePicker` through the Import tab's `createCoverPicker` |

* Every player is Base's Audio Player. Never use a bare `<audio>` element or a hand built play button.
* A media card action that cannot run now stays in place with `disabled: true`, such as **Change Link** while its song downloads.
* An embedded cover can only be replaced, so its Image Picker takes `removable: false` and shows no Remove.

### Import Panel

Each downloaded file on the Import tab is one `base-panel` drawn by `renderImportRow`.

* The header holds the selection checkbox and the title at the start, then the size, the **In Library** badge, **Generate Cover**, and **Save Tags** at the end. On a phone the end group takes a line of its own.
* The body is a grid with the cover cell first, as wide as the cover, so **Set Cover** sits under it. The cover cell takes back the Image Picker's bottom margin, so the cover is one gap from the fields.
* The fields share three equal columns, with **Track** and **Year** in a narrow last column that keeps four digits. The rows are Title and Track, then Artist, Album Artist, and Featured Artists, then Album, Genre, and Year.
* The Audio Player spans the full width under the cover and the fields.
* At 768 px the cover moves above the fields. At 600 px each field takes its own line.
* Size the columns in `rem` and `fr` from `--base-gap-wide`, never in pixels or `em`.

### Progress

* A task started from a button shows progress in that button with `shared.pollTaskProgress`.
* Progress inside a row uses `base-progress` and `base-progress-bar`.
* Never block the page with `Dashboard.showLoadingMsg`.

### Dialogs

| Need | Use |
| :--- | :--- |
| A yes or no question | `confirmDialog` |
| A few values | `promptDialog` |
| A record editor | `openEditorDialog`, with `wide: true` for a large one |
| A custom dialog | `openDialog` |
| A dialog already in the page markup | `bindDialog` |

* Build dialogs in JS with `openDialog` and its wrappers. Use `bindDialog` only when the markup is large and static.
* A dialog title is Title Case and names what the dialog does.
* Buttons read, from left to right, the destructive action, then **Cancel**, then the primary action.

## Layout

* Use Base's breakpoints, 768 px for tablet and 600 px for phone. Use 700 px only where Base does, for dialogs.
* Test every page at 375 px wide. Nothing scrolls sideways except a table.
* Look at every changed page on a test server before calling it done. Reload the page after installing the build, since the dashboard caches scripts.
* Spacing comes from `--base-gap-wide` and `--base-gap-narrow`. Never add margins between fields.
* Never set a width in pixels on a field. Let it fill its container.
* A list that can grow past one screen uses `height: 'fill'`, so it ends at the window bottom and scrolls inside.
* Watch for class collisions with Jellyfin's own CSS. Base's card Repeater adds a bare `card` class, and Jellyfin's global `.card` rule gives it paint containment. A Base component that draws fixed popups never sits inside an element with `contain`, `transform`, or `filter`, since its dropdowns move off screen.

## Accessibility

* Every input has a label, and every icon only button has a `title` and an `aria-label`.
* Every icon span has `aria-hidden="true"`.
* Rows that open on click also open with Enter and Space. Base's table and card list do this already.
* Never rely on color alone.

## CSS in This Plugin

* Plugin classes use the `yta-` prefix in kebab case. `base-` is reserved for Base.
* Each page keeps its own classes in one `<style>` block at its end, with the three line comment from [code.md](code.md#css) above each.
* Keep plugin CSS for layout that only this plugin has. A component another plugin could use goes to Base.
* Never override a `base-` class. Change Base instead.
* When Base has a defect that breaks a page, report it and add one marked stopgap rule under a comment that names the defect. Delete the rule in the commit that moves to the Base version with the fix.
* Size a column in `rem` or `fr`. An `em` width changes with the font size of each row, so a smaller heading row falls out of line with its columns.
* Never define a color token. Use Base's.

## Hand Built Pieces

Every list, row, card, badge, picker, player, and button in this plugin is a Base component or Base markup. These small pieces work around an option Base lacks. Delete each in the commit that moves to the Base version with the option. Add nothing to this list.

| Piece | Missing Base option |
| :--- | :--- |
| The user page suggests artist and album names through a plain `datalist` | The status template loads no Base script, so `createComboBox` cannot run there |

## Checklist

Run every new or changed string through this list.

* [ ] No em dashes, en dashes as punctuation, or semicolons.
* [ ] No colon at the end of a label, and none in a sentence unless it introduces a list.
* [ ] One sentence, or two when the second is the fix or the consequence.
* [ ] Sentences end with a period. Labels, buttons, and titles do not.
* [ ] Title Case for labels, buttons, titles, tabs, and badges. Sentence case for sentences.
* [ ] No "please", "successfully", exclamation marks, ALL CAPS, "(s)", or contractions.
* [ ] The word matches [Words to Use](#words-to-use).
* [ ] Base's typings were searched before any markup was built by hand.
* [ ] The component comes from Base, unchanged.
* [ ] Colors come from Base tokens and mean what [Meaning](#meaning) says.
* [ ] The docs use the same label.
