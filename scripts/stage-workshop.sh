set -euo pipefail

# Stages a release into the ModUploader's Sts2Unlimited workspace, and with --upload
# publishes it. Run it from a checked-out release tag:
#
#   git checkout vX.Y.Z
#   MOD_UPLOADER=/mnt/c/.../ModUploader-win-x64 scripts/stage-workshop.sh [--upload]
#
# It refuses to stage when:
#   - tracked files differ from the tag;
#   - the built manifest's version is not the tag (tags have pointed at the wrong
#     bump commit before, and the manifest is what the Workshop item reports);
#   - workshop/workshop.json's changeNote is the same as at the previous release tag
#     (nothing updates it automatically, so a stale note shipped twice);
#   - the headless patch harness fails.
# STAGE_ALLOW_UNTAGGED=1 stages a non-tag HEAD (for a dry run; skips the tag checks).

upload=0
for arg in "$@"; do
    case "$arg" in
        --upload) upload=1 ;;
        *) echo "usage: $0 [--upload]" >&2; exit 2 ;;
    esac
done

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cd "$repo_root"

uploader="${MOD_UPLOADER:?set MOD_UPLOADER to your ModUploader-win-x64 directory}"
workspace="$uploader/Sts2Unlimited"
content="$workspace/content"

die() { echo "error: $*" >&2; exit 1; }

[[ -d "$workspace" ]] || die "no workspace at $workspace"

change_note() {  # $1 = git revision; prints that revision's changeNote
    git show "$1:workshop/workshop.json" | python3 -c "import json,sys;print(json.load(sys.stdin)['changeNote'])"
}

if tag=$(git describe --exact-match --tags --match 'v[0-9]*' 2>/dev/null); then
    # The checks below read the tagged files, but the build and copy use the working
    # tree; uncommitted edits would ship something that is not the release.
    git diff --quiet HEAD || die "tracked files have uncommitted changes — staging $tag would not ship the tag"
    prev_tag=$(git describe --tags --abbrev=0 --match 'v[0-9]*' "$tag^" 2>/dev/null || true)
    if [[ -n "$prev_tag" ]] && [[ "$(change_note HEAD)" == "$(change_note "$prev_tag")" ]]; then
        die "workshop/workshop.json changeNote is unchanged since $prev_tag — write this release's note first"
    fi
elif [[ "${STAGE_ALLOW_UNTAGGED:-}" == 1 ]]; then
    tag="(untagged)"
    echo "warning: HEAD is not a release tag — staging $(git rev-parse --short HEAD) without tag checks" >&2
else
    die "HEAD is not a release tag (check one out, or set STAGE_ALLOW_UNTAGGED=1 for a dry run)"
fi

echo "==> Running headless patch harness"
if ! harness_log=$(dotnet run --project tests/PatchTests 2>&1); then
    echo "$harness_log" | grep -E 'FAIL|error' >&2 || echo "$harness_log" | tail -20 >&2
    die "patch harness failed"
fi

echo "==> Building ExportRelease"
dotnet build --configuration ExportRelease >/dev/null

manifest_version=$(python3 -c "import json;print(json.load(open('release/Sts2Unlimited.json'))['version'])")
if [[ "$tag" != "(untagged)" && "$manifest_version" != "$tag" ]]; then
    die "manifest says $manifest_version but HEAD is tagged $tag — the tag points at the wrong commit"
fi

echo "==> Staging content into $content"
rm -f "$content"/*
cp release/Sts2Unlimited.json release/sts2unlimited.dll release/icon.svg "$content/"
cp release/Sts2Unlimited.pck "$content/sts2unlimited.pck"

echo "==> Staging store page"
cp workshop/workshop.json workshop/mod_id.txt "$workspace/"
cp example.png "$workspace/image.png"

echo
echo "Staged $tag (manifest $manifest_version)"
echo "Item:   $(tr -d '[:space:]' < workshop/mod_id.txt)"
echo "Files:"
ls -1 "$content" | sed 's/^/  /'
echo
echo "Change note:"
change_note HEAD | sed 's/^/  /'
echo

# The uploader must get --workspace: run bare, it silently copies template/ into a new
# throwaway workspace instead of uploading anything.
upload_cmd=(./ModUploader.exe upload --workspace Sts2Unlimited)

if (( upload )); then
    [[ "$tag" != "(untagged)" ]] || die "refusing to upload an untagged build"
    read -r -p "Upload $manifest_version to the Workshop now? Steam must be running. [y/N] " answer </dev/tty
    [[ "$answer" == [yY]* ]] || { echo "Not uploaded."; exit 0; }
    (cd "$uploader" && "${upload_cmd[@]}")
else
    echo "Review the above, then publish with --upload, or:"
    echo "  cd \"$uploader\" && ${upload_cmd[*]}"
fi
