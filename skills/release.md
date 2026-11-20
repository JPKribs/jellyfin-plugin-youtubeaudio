# Release

How to cut a release of this plugin, from the version number to the manifest every server reads.

## Versions

A version is `JJ.JJ.F.B`.

| Part | Means | Raise it when |
| :--- | :--- | :--- |
| `JJ.JJ` | The Jellyfin version the release targets, such as `12.4` | The plugin moves to a new Jellyfin version |
| `F` | The feature release | The release adds or changes behavior a user will notice |
| `B` | The bug release within that feature | The release only fixes bugs |

Raising a part sets every part after it to `0`. `12.4.4.4` becomes `12.4.5.0` for a feature and `12.4.4.5` for a fix.

## Branch

A feature release is cut on `unstable`, then **Sync Branches** with `promote` moves `master` to it. A bug release is cut on `master`, and the sync carries it to `unstable`. See [branches.md](branches.md).

## Before the Release

1. **Base.** When the release needs a Base change, release Base first with its **Release** workflow, then raise `JPKribs.Jellyfin.Base` in `Jellyfin.Plugin.YouTubeAudio/Jellyfin.Plugin.YouTubeAudio.csproj` and delete any local copy Base replaces. Wait until NuGet serves the version, and never release against a local package. Raise Base on a branch, and merge it once every check below has passed on it.
2. **Database.** When the schema changed, raise `SchemaVersion` in `QueueDatabase`. Raise `MinReaderSchemaVersion` only when a migration changes or removes something older releases rely on.
3. **Build.**

    ```bash
    dotnet build
    ```

4. **Tests.**

    ```bash
    dotnet test
    ```

    ```bash
    YTA_RUN_INTEGRATION=1 dotnet test --filter "Category=Integration"
    ```

5. **End to end.** Build the release zip and run the end to end test against it.

    ```bash
    ./build.sh
    ```

    ```bash
    python3 tools/e2e/docker_e2e.py --zip dist/jellyfin-plugin-youtubeaudio-<version>.zip
    ```

    Every check must run on the commit that is released. Run them again after any later change.

6. **Pages.** Install the zip on a test server and open each tab and both public pages.

7. **Docs.** Every page describes this release. Retake any screenshot whose subject changed, and all of them when the Base version changed, with the [screenshot kit](documentation.md#screenshot-kit). Open each new PNG and look at it. Update [Versioning](../docs/Versioning.md) when the schema reader or the upgrade path changed.

    ```bash
    mkdocs build --strict
    ```

8. **Review.** Run [review.md](review.md) over everything since the last release.

## Set the Release

Edit `build.yaml`.

1. Set `version` to the new version.
2. Replace the whole `changelog` with this release's entries. Each release lists only its own changes, so drop every line that shipped in the last release and every line a later change in this release replaced. See [documentation.md](documentation.md#release-notes) for how to write them.
3. Name the Base version in `developer` when it changed, such as "Uses JPKribs.Jellyfin.Base 12.4.1", with what it replaced locally.
4. Keep `description` and `overview` true for this release. `manifest.json` takes its own description, so update both together.

Merge the release branch into `master` first, so every change for the release is on it. Then commit the version on its own, on `master`.

```bash
git checkout master
```

```bash
git merge --ff-only <branch>
```

```bash
git commit -am "Set the release to 12.4.5.0"
```

```bash
git push
```

## Publish

Follow these steps in order, and skip none.

1. **Draft.** The **Create/Update Release Draft** workflow builds the zip and writes a draft release named `v<version>` with the changelog as its notes. A push to `master` that carries any file besides `build.yaml`, Markdown, `docs/`, `mkdocs.yml`, `.github/`, and `.gitignore` starts it. A push that only changes those does not, so start it from the Actions tab only then. Draft builds run one at a time, so a push and a manual start that overlap build one after the other.

    ```bash
    gh run list -L 8
    ```

2. **Check.** Wait until no draft build is queued or running, since each one replaces the zip. Then list the releases and open the draft on GitHub. The notes must match `build.yaml`, and the zip and its `.md5` must be attached.

    ```bash
    gh release list -L 5
    ```

3. **Release.** Publish the draft. The **Publish Plugin** workflow waits for any draft build still running, hashes the attached zip, checks it against the `.md5`, adds the version to `manifest.json`, and commits "Update manifest for release v<version>". A draft build never replaces the assets of a published release.
4. **Confirm.** Check that the manifest commit landed and that the checksum of the new version in `manifest.json` matches the `.md5` on the release.
5. **Pull.**

    ```bash
    git pull --ff-only
    ```

6. **Verify.** On a test server, add the repository URL, install the release from the catalog, restart, and open each tab.

## When a Release Is Wrong

* Never edit or reuse a published version. Fix it in a new bug release.
* When a release breaks servers, publish the fix as the next bug release at once.
* When the manifest entry itself is wrong, run **Publish Plugin** by hand with the release tag after fixing the cause.

## Checklist

* [ ] The version follows `JJ.JJ.F.B`, and later parts are `0`.
* [ ] Base is released and referenced, with local copies deleted.
* [ ] The schema versions are raised when needed.
* [ ] `dotnet build`, `dotnet test`, and the download tests pass on the released commit.
* [ ] The end to end test passes against the release zip.
* [ ] Docs, screenshots, and Versioning describe this release.
* [ ] `mkdocs build --strict` passes.
* [ ] The changelog lists only this release, in the four keys.
* [ ] The draft that was published has the notes, the zip, and the `.md5`.
* [ ] The manifest commit landed with the release's checksum, and it is pulled.
