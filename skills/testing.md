# Testing

How to test this plugin, from a single method to a throwaway Jellyfin server. A change is done when the tests for every layer it touches pass.

## The Rule

Every source file has tests, and every test proves that given inputs produce the expected output. A bug fix starts with a test that fails without the fix.

## Layers

Test each behavior at the lowest layer that can prove it. Go up a layer only for what the lower one cannot reach.

| Layer | Proves | Runs | Time |
| :--- | :--- | :--- | :--- |
| Unit | One class or function gives the right output for each input | `dotnet test` | Seconds |
| Store | SQL, schema, and migrations against a real SQLite file | `dotnet test` | Seconds |
| Service | A service against real files in a temp folder, such as tagging, filing, and import | `dotnet test` | Seconds |
| Controller | A route's status codes, guards, and response shape | `dotnet test` | Seconds |
| Download | yt-dlp downloads and matches against real YouTube | `YTA_RUN_INTEGRATION=1 dotnet test` | Minutes |
| End to end | The whole pipeline on a throwaway Jellyfin in Docker | `tools/e2e/run.sh` | Minutes |
| Harness | A tab or a user page draws and behaves right under Jellyfin's real CSS | The [harness](#harness) in headless Chromium | Minutes |
| Page | A config page draws, reads, and saves as it should | By hand, on a test server | Minutes |

| A change to | Needs |
| :--- | :--- |
| A parser, matcher, cleaner, or model | Unit |
| `QueueDatabase` or its schema | Store |
| `DownloadService`, `LibraryService`, or a playlist service | Service |
| A controller or route attribute | Controller |
| How yt-dlp is run, or how a match is searched | Download |
| Anything that moves or deletes library files, or the public queue | End to end |
| A config page or a public page | Harness and Page |

## Layout

The test project is one folder. Name each file after the area it covers.

| Source | Test |
| :--- | :--- |
| `Services/QueueDatabase.cs` | `QueueDatabaseTests.cs` |
| `Services/Playlists/PlaylistSyncService.cs` | `PlaylistSyncTests.cs` |
| `Api/YouTubePublicController.cs` | `PublicControllerTests.cs` |

* Shared setup lives in `ServiceTestSupport.cs`, whose `ServiceFixture` builds the services over a temp folder.
* Test files, such as catalog answers, exports, and a short Opus file, live in `TestData/`.

## C# Tests

Tests use xUnit with plain `Assert`, and `Xunit.SkippableFact` for the opt in tests. There is no mocking library.

```csharp
/// <summary>
/// A live upload is another recording of a studio title.
/// True: a live version never replaces the studio song the export names.
/// False: playlist imports would download the wrong recording.
/// </summary>
[Fact]
public void IsOtherVersion_LiveUploadOfStudioTitle_ReturnsTrue()
{
    var result = YouTubeMatcher.IsOtherVersion("Clair de lune (Live)", "Clair de lune");

    Assert.True(result);
}
```

### Naming and Comments

* Name the class `<Subject>Tests` and each method `Method_Scenario_Expectation`.
* Give the class a one sentence summary of what its subject must guarantee.
* Give each test the three line summary. What it proves, what passing means, and what failing would cause.

### Writing a Test

* Arrange, act, and assert, separated by blank lines.
* Assert one behavior per test. Several asserts are fine when they check one outcome.
* Use `[Theory]` with `[InlineData]` for one rule over several inputs.
* Test the edges. Null, empty, the largest value, a cancelled token, and an answer that never comes.
* Never sleep. Pass a clock or a delay in, and control it from the test.
* A test that fails once in many runs has found a race. Fix the source or the test.
* A test never reaches the network, apart from the opt in download tests. Answer HTTP with a stub `HttpMessageHandler` and catalog answers from `TestData/`.
* A test that sets `Plugin.Instance` joins `[Collection("Plugin")]`, defined by `PluginCollection`, which runs its members one at a time.

### Stores and Files

* Use a real SQLite file and real files in a temp folder, and delete them in `Dispose`.
* Open the database through `QueueDatabase`, so the test runs the same create and migrate as the plugin.
* Test a migration by writing the older schema by hand, opening it, and checking the result.
* Fingerprint a library folder before and after a change to files, so an unexpected write fails the test.

```bash
dotnet test
```

## Download Tests

`DownloadIntegrationTests` and `PlaylistMatchIntegrationTests` download from YouTube with the yt-dlp on the path. They are skipped unless the variable is set.

```bash
YTA_RUN_INTEGRATION=1 dotnet test --filter "Category=Integration"
```

* Run them for any change to how yt-dlp is run or how a match is searched.
* They need yt-dlp and ffmpeg on the path, and network access.

## End to End Test

`tools/e2e/docker_e2e.py` runs the plugin on a throwaway Jellyfin container and drives the whole Apple Music import pipeline through the API, with real YouTube downloads. Every file in its test library is fingerprinted before and after each stage. See [Testing](../docs/Testing.md) for what it covers.

```bash
tools/e2e/run.sh
```

```bash
python3 tools/e2e/docker_e2e.py --zip dist/jellyfin-plugin-youtubeaudio-<version>.zip
```

* Docker must be running, and the run needs network access.
* `run.sh` builds the plugin in Release first. `--zip` installs the release zip instead, which catches a dependency missing from `build.yaml`'s artifacts.
* `--keep` leaves the server running on port 8097 to look at.
* Scratch files and the run's throwaway logins go to the ignored `tools/e2e/.work/`.
* Run it for any change that moves or deletes library files, and before every release.

## Harness

The harness is the page the [screenshot kit](documentation.md#screenshot-kit) serves. It loads a tab the way the dashboard does, with no Jellyfin server behind it.

* It loads Jellyfin's real stylesheets from the installed jellyfin-web, `index.html`'s first two, then the dark theme and the chunks. It leaves out the chunks that set the root font to anything but the dashboard's 93%, which belong to other layouts such as TV. Base's CSS alone hides real bugs, such as Jellyfin's `.card` rule or its widened inputs.
* It mimics the upgrade of each `emby-*` element in `upgrade.js`, so labels, select arrows, and checkboxes are drawn as the dashboard draws them.
* It serves the tab's HTML and JS from the repository and Base's assets from the NuGet package the plugin builds against, with stub API answers and generic data.
* It renders the user pages through Base's status template, as the controller does, so they get only the template's styles.

Click through each changed page like a user, at 1280 and at 375 px wide.

* Type real keystrokes into every picker and lookup, such as part of an artist's name, and check that the list fills.
* Open every tab, every collapsible, and every dialog, such as **Change link** and **Delete files**.
* Take a screenshot of each state and look at it.

A harness run never makes a change done. It finds problems before the test server does.

## Pages

The config pages and the public pages have no automated tests. After the harness, check every changed page by hand on a test server.

* Install the build, restart, and hard reload the dashboard, since it caches scripts.
* Open every changed tab, and use every control the change touched.
* Check the page at 375 px wide.
* Check the browser console for errors.

## Checklist

* [ ] Every changed source file has tests.
* [ ] A bug fix has a test that fails without it.
* [ ] Edges are covered, including null, empty, and cancelled.
* [ ] `dotnet test` passes.
* [ ] The download tests pass for any change to how songs download.
* [ ] The end to end test passes for any change to library files.
* [ ] Every changed page was clicked through in the harness at 1280 and 375 px, and each screenshot was looked at.
* [ ] Every changed page was opened and used on a test server after a hard reload.
