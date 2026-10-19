# End-to-End Test

`e2e/` holds an opt in test that runs the plugin on a disposable Jellyfin container and drives the whole Apple Music import pipeline through the API, including real YouTube downloads. It covers the paths that move or delete library data: duplicate skipping, filing into existing album folders, replacing a library single with its album version, importing into the library, private playlists for a non admin owner, fixing a failed match with a new YouTube link, a container restart, the public queue, and a reimport that must change nothing. Every file in the test library is fingerprinted before and after each stage, so any unexpected change fails the run.

It needs Docker and internet access, and uses only the Python standard library. It is not part of `dotnet test`.

```bash
e2e/run.sh
```

This builds the plugin in Release and runs `e2e/docker_e2e.py` against the stable Jellyfin 12.2 image. Options such as `--image`, `--port` and `--keep` (leave the server running to inspect it) are listed by `python3 e2e/docker_e2e.py --help`. Scratch files, including the run's throwaway logins, go to the git ignored `e2e/.work/`.

To test the release package rather than the build folder, run `./build.sh` and pass the ZIP it writes, for example `python3 e2e/docker_e2e.py --zip dist/jellyfin-plugin-youtubeaudio-<version>.zip`. The server then installs exactly the files the ZIP holds, which catches a dependency missing from `build.yaml`'s artifacts.
