# Code

How to write, comment, and test code in this plugin. It covers C#, JavaScript, HTML, CSS, Python, shell, and YAML.

## Base First

[JPKribs.Jellyfin.Base](https://github.com/JPKribs/jellyfin-plugin-base) holds everything two plugins could share. Its README lists every component and helper. Read it before you write anything new.

### Use What Base Has

* Use the Base type or component when one fits. Never write a local version, a wrapper that renames it, or a CSS override of a `base-` class.
* When Base almost fits, change Base. Never patch around it locally.
* Delete a local copy as soon as Base offers the same thing. Do it in the commit that moves to that Base version.
* Never shadow a Base name. A local `FormatUtilities` beside Base's `FormatUtilities` is ambiguous.
* Read Base from the package this plugin builds against. The C# source is in `~/.nuget/packages/jpkribs.jellyfin.base/<version>/src/`, and the JS typings are in `assets/jpkribs_shared.d.ts` beside it.
* Treat the local Base checkout as read only. Base has its own owner and its own release, so report a gap and wait for the version that closes it.
* When Base differs from the local code it replaces, keep the local behavior in a small step around the Base call, and cover it with a test.
* Base comes first, but a component used wrong is worse than none. Look at the result on a real server, not just in the code.
* Moving UI onto a Base component is UI work, even on a sensitive page. Only stored data, routes, auth, and secret handling are [stop and ask](../starthere.md#stop-and-ask).

| Need | Use from Base |
| :--- | :--- |
| Plugin class and config lock | `PluginBase<TPlugin, TConfiguration>` |
| Scheduled task | `PluginScheduledTask` |
| Outbound HTTP | `JpkHttp` and `HttpResult` |
| HTTP handlers | `JpkRetryHandler` with `JpkRetryOptions`, and `RedirectRefusingHandler` |
| Addresses another server or a user supplied | `SsrfGuard`, with `requireHttpsForPublic: true` |
| Retry and backoff | `RetryPolicy`, `BackoffPolicy`, `CircuitBreaker` |
| Parallel work | `ConcurrentTaskRunner` |
| Secrets | `SecretProtector`, `AddPluginSecretProtector` |
| Comparing a presented secret | `SecretCompare.FixedTimeEquals` |
| Writing a file whole or not at all | `AtomicFile`, with `ownerOnly: true` for a key or secret |
| Paths inside a folder | `PathContainment` |
| Streams | `StreamUtilities.CopyWithIdleTimeoutAsync` and `CopyThrottledAsync`, `SpeedBucket`, `ThrottledReadStream`, `CappedReadStream`, `ProgressReportingStream`, `StreamLimits` |
| Hashes and sizes | `HashUtilities`, `ByteSize` |
| The clock | `TimeProvider`, and `UtcTime` for stored times |
| Activity log | `ActivityLogger`, `ActivitySummary`, `ActivityText` |
| Paged lists | `PagedResult<T>`, `PagedQuery` |
| SQLite versioning | `DatabaseSchema` |
| JSON state file | `JsonFileStore<T>` |
| Formatting and names | `FormatUtilities`, `ItemTitles`, `RateUtilities`, `FileNameSanitizer` |
| Config page lifecycle | `createConfigPage`, through `createPage` in `youtubeaudio_shared.js` |
| Page helpers | `createShared` |
| Tables | `createPaginatedTable` |
| Lists and row editors | `createFilteredList`, `createList`, `createCheckboxList`, `createOrderedList`, `createRepeater` |
| Charts | `createPieChart`, `createUsageMeter`, `createBarChart` |
| Dialogs | `openDialog`, `confirmDialog`, `promptDialog`, `openEditorDialog`, `bindDialog` |
| Files the user chooses | `createFilePicker` |
| Public pages | `PublicPage` and `StatusPage`, with the `base-status-card` template |

### Move Code Into Base

Code belongs in Base when another plugin could use it unchanged. A helper with no YouTube Audio types in its signature is a candidate.

1. Add it to `../Shared/jellyfin-plugin-base/src/`, one file per component, following Base's [code guide](https://github.com/JPKribs/jellyfin-plugin-base/blob/main/skills/code.md).
2. Add tests in `JPKribs.Jellyfin.Base.Tests/`, and JS tests in `JPKribs.Jellyfin.Base.Tests/js/`.
3. Document it in Base's docs site inventory.
4. Pack it locally with `scripts/pack.sh` and build this plugin against it with `-p:RestoreAdditionalProjectSources=<base>/nupkg`.
5. Commit and push to Base, then run Base's **Release** workflow.
6. Raise the `JPKribs.Jellyfin.Base` version in this plugin's csproj, delete the local copy, and add a `developer` changelog line naming the Base version.

Never commit this plugin against an unreleased Base package.

## Comments

### The Rules

* Every class, method, function, and component gets a header comment.
* The header is the name, then a summary of one or two sentences, leaning to one.
* List inputs and outputs when there are any. One line each, as `name [type]: What or why`.
* Write no inline comments, with one exception. A branch whose reason is not obvious gets one short line above it.
* A branch comment says why, not what. Keep it under 15 words.
* Never comment what the code already says. `// Check if values differ` is noise.
* Never leave commented out code, TODO, FIXME, or HACK. Open an issue instead.
* Never describe history. "Used to", "now", and "was changed to" belong in commit messages.
* Use no em dashes or semicolons in comments.

```text
<Name>
<What it does, in one sentence.>

Inputs
name [type]: What it is or why it is needed.

Output
[type]: What comes back.
```

Leave out **Inputs** when there are none, and **Output** when nothing comes back.

### C#

C# uses XML docs so the compiler and IntelliSense read them. The member name is the title. The type comes from the signature, so a `param` gives only the what or why.

```csharp
/// <summary>
/// Returns whether a candidate title names a version, such as live or remix, that the source title does not.
/// </summary>
/// <param name="candidateTitle">The title of the YouTube upload.</param>
/// <param name="sourceTitle">The title from the playlist export.</param>
/// <returns><c>true</c> when the upload is another recording.</returns>
internal static bool IsOtherVersion(string? candidateTitle, string? sourceTitle)
{
    var candidate = " " + string.Join(' ', Tokenize(candidateTitle)) + " ";
    var source = " " + string.Join(' ', Tokenize(sourceTitle)) + " ";
    return OtherVersionWords.Any(word =>
        candidate.Contains(" " + word + " ", StringComparison.Ordinal)
        && !source.Contains(" " + word + " ", StringComparison.Ordinal));
}
```

* Every type and every member that is not private gets `<summary>`, every `<param>`, and `<returns>` when it returns a value.
* Private members get a `<summary>` when the name does not say enough.
* Use `<inheritdoc/>` on an interface or override member.
* Write `true` and `false` as `<c>true</c>` and `<c>false</c>`.
* A constructor summary is "Initializes a new instance of the <see cref="Name"/> class."
* Skip tautologies. `<param name="item">The item.</param>` says nothing, so write what the item is for.
* Put one type in each file, named after the type. A small enum used by one class may share its file.
* Match the namespace to the folder.

### JavaScript

```js
// createPage
// Wires a config page through Base's page lifecycle with the plugin's tabs.
//
// Inputs
// view [HTMLElement]: The page the dashboard passes to the module's default export.
// opts [object]: The page's createConfigPage options.
//
// Output
// [object]: The page handle.
export function createPage(view, opts) {
```

* Comment every top level function, every function assigned to `shared`, and every exported function.
* Do not comment the methods inside a returned handle. List them in the Output line instead.
* Open each file with a header naming the file and saying what it holds in one sentence. Use no `// ====` banners.
* Only `youtubeaudio_shared.js` imports Base, by the relative path `./configurationpage?name=youtubeaudio_jpkribs_shared.js`. It exports each Base builder a page uses, and every page imports from `youtubeaudio_shared.js`.
* The public pages are standalone documents that `YouTubePublicController` serves inside Base's status page template. They load Base from `../web/configurationpage?name=youtubeaudio_jpkribs_shared.js` and its CSS beside it.

### HTML

```html
<!-- Music Library -->
<!-- The library and folder imports are filed into. -->
<div class="base-collapsible-section">
```

* One comment line naming each section or dialog where it starts, and an optional second line saying what it is for.
* Use no inline `style` attributes. Page styles sit in one `<style>` block at the end of the page.

### CSS

```css
/* yta-import-body */
/* A file's cover picker beside its tag fields, wrapping under them on a narrow screen. */
/* Example: <div class="yta-import-body"><div class="yta-cover-cell"></div><div class="yta-import-fields"></div></div> */
.yta-import-body { ... }
```

* Three lines above each component, the name, what it looks like, and one example.
* Prefix every class with `yta-` in kebab case. `base-` is reserved for Base.
* Use Base tokens such as `--base-border` and `--base-fg` for colors. Never write a hex value for something Base already colors.

### Python, Shell, and YAML

* A Python module opens with a docstring that names it and lists its commands. Each function gets a one line docstring.
* A shell script opens with a comment saying what it does and how to run it.
* A YAML file opens with a comment saying what it is for. Comment a step only when its reason is not obvious.

## C# Conventions

* Target `net10.0` with `Nullable` on, `TreatWarningsAsErrors` on, and `AnalysisMode` set to `AllEnabledByDefault`.
* Keep `jellyfin.ruleset` identical across plugins. Raise a rule change in every plugin at once.
* Suppress a rule only at the narrowest scope, with a reason. `#pragma warning disable CA2100 // SQL is internal and parameterized.`
* Use file scoped namespaces, `sealed` classes, and records for data.
* Name private fields `_camelCase`.
* End every async method name in `Async`, pass the `CancellationToken` through, and use `ConfigureAwait(false)` on every await.
* Guard public arguments with `ArgumentNullException.ThrowIfNull`.
* Log through `ILogger<T>` with static, named templates. Pass untrusted text through `FormatUtilities.SanitizeForLog`.
* Catch a specific exception, or filter with `when`. Rethrow `OperationCanceledException` when the token was cancelled.
* Read the clock once in a method and reuse the value. Two reads can straddle a second and disagree.
* Take a `TimeProvider` wherever time decides an outcome, so a test can move it.
* Register services in `ServiceRegistrator`.

## JavaScript Conventions

* Each config page is an ES module whose default export calls `createPage`, which wraps Base's `createConfigPage` and holds the tab list.
* Keep one handle for each Base component a page draws. Call `destroy` before drawing it again, and `setValue` when only its numbers change.
* Drop an answer that arrives after the page moved on. Remember the picked key before the request and compare it when the answer lands.
* Use `const` and `let`, never `var`. Arrow functions are fine for callbacks.
* Escape every value placed in HTML with `escapeHtml`.
* Report results with `shared.setStatus`, and errors with `shared.errorText`.
* Register every poller and observer with the page handle so `viewhide` stops it.

## Security

* Guard every operator route with `[Authorize(Policy = "RequiresElevation")]`. The routes in `YouTubePublicController` are the only others, and each checks the signed in user itself.
* Check approval against the signed in Jellyfin account on the server for every user page call. Never trust a value the page sends.
* Accept only the YouTube hosts `DownloadService` lists, and refuse a link with spaces or quotes.
* Pass every value from a request, a file, or yt-dlp through `FormatUtilities.SanitizeForLog` before logging it.
* Run yt-dlp and Deno with `UseShellExecute = false`, through `YtDlpRunner` or `ProcessStartInfo.ArgumentList`. Never run them through a shell.
* Use a tool only after `ToolDownloads` has checked it against the checksum its release publishes.
* Never follow a redirect to another host when fetching a cover.
* Build SQL with parameters only. Never put a value into SQL text.
* Name files and folders with `DownloadService.SanitizePathComponent`, and keep new paths inside their folder with Base's `PathContainment`.
* Escape every value placed in HTML with `escapeHtml`.
* Never put a secret or a user's data in a URL, except the `ApiKey` an audio or image element must carry because it cannot send headers.

## Tests

Every source file has tests. See [testing.md](testing.md) for each layer, how to write tests, and the Docker end to end test.

## Checklist

* [ ] Base's source and typings were searched, nothing here duplicates Base, and anything reusable went to Base first.
* [ ] Every class, method, function, and component has a header.
* [ ] Inline comments appear only above branches, and say why.
* [ ] Every changed source file has tests that pass, per [testing.md](testing.md).
* [ ] The [security](#security) rules hold.
* [ ] `dotnet build` shows no warnings.
