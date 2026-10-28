#!/usr/bin/env bash
# Stage a Workshop upload: build the mod, verify what is about to ship, and copy it into
# the ModUploader workspace. Does NOT upload — publishing is a deliberate manual step.
#
# Usage: MOD_UPLOADER=/path/to/ModUploader-win-x64 ./scripts/stage-workshop.sh
set -euo pipefail

readonly EXPECTED_MAX_PLAYERS=8   # the documented default; see workshop/README.md

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cd "$repo_root"

uploader="${MOD_UPLOADER:?set MOD_UPLOADER to your ModUploader-win-x64 directory}"
workspace="$uploader/Sts2Unlimited"
content="$workspace/content"

[[ -d "$workspace" ]] || { echo "error: no workspace at $workspace" >&2; exit 1; }

# Release from a tag. A detached HEAD on an annotated tag is the expected state.
if ! tag=$(git describe --exact-match --tags 2>/dev/null); then
    echo "warning: HEAD is not a tag — staging $(git rev-parse --short HEAD) anyway" >&2
    tag="(untagged)"
fi

echo "==> Building ExportRelease"
dotnet build --configuration ExportRelease >/dev/null

# The maxplayers file is gitignored, so the working tree's local value is what gets
# bundled. Shipping a stray value is how v0.6.1 went out with 5 instead of 8.
actual_max=$(tr -d '[:space:]' < release/sts2unlimited.maxplayers.txt)
if [[ "$actual_max" != "$EXPECTED_MAX_PLAYERS" ]]; then
    echo "error: release/sts2unlimited.maxplayers.txt is '$actual_max', expected $EXPECTED_MAX_PLAYERS" >&2
    echo "       (it is gitignored; set it before staging)" >&2
    exit 1
fi

# The manifest version is what subscribers see as the mod version.
manifest_version=$(python3 -c "import json;print(json.load(open('release/Sts2Unlimited.json'))['version'])")

echo "==> Staging content into $content"
rm -f "$content"/*
cp release/Sts2Unlimited.json release/sts2unlimited.dll release/icon.svg \
   release/sts2unlimited.maxplayers.txt "$content/"
# release/ produces Sts2Unlimited.pck; subscribers get the lowercase name.
cp release/Sts2Unlimited.pck "$content/sts2unlimited.pck"
# README.md is deliberately not shipped to the Workshop.

echo "==> Staging store page"
cp workshop/workshop.json workshop/mod_id.txt "$workspace/"
cp example.png "$workspace/image.png"

echo
echo "Staged $tag (manifest $manifest_version), maxPlayers=$actual_max"
echo "Item:   $(tr -d '[:space:]' < workshop/mod_id.txt)"
echo "Files:"
ls -1 "$content" | sed 's/^/  /'
echo
echo "Change note:"
python3 -c "import json;print(json.load(open('workshop/workshop.json'))['changeNote'])" | sed 's/^/  /'
echo
echo "Review the above, then publish with:"
echo "  cd \"$uploader\" && ./ModUploader.exe upload --workspace Sts2Unlimited"
