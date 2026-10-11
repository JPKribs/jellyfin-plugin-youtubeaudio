#!/bin/bash
# Builds the plugin in Release and runs the Docker end-to-end test against it.
# Extra arguments are passed to docker_e2e.py (see: python3 e2e/docker_e2e.py --help).
set -e

cd "$(dirname "$0")/.."
dotnet build Jellyfin.Plugin.YouTubeAudio/Jellyfin.Plugin.YouTubeAudio.csproj -c Release --nologo -v quiet
python3 -u e2e/docker_e2e.py "$@"
