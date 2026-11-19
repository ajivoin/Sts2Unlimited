# Steam Workshop release

The Workshop store page lives here so it is reviewed alongside the code it describes.
It drifted out of sync once already: the description advertised a Max Players range of
2–256 when the slider had always been 2–16, and the config file that was documented as
accepting "any number" is unclamped, so readers who believed it got silently broken
lobbies.

## What is here

| File | Purpose |
|---|---|
| `workshop.json` | Store page: `title`, `description` (Steam BBCode), `tags`, `visibility`, and the `changeNote` for the next upload |
| `mod_id.txt` | Workshop item ID `3747509118` — pins uploads to the existing item instead of creating a new one |

The Workshop preview image is `example.png` in the repository root — the same file the
README embeds. `scripts/stage-workshop.sh` copies it into place as `image.png`.

`title`, `description` and `tags` only change when the mod's features (or their accuracy)
change. `changeNote` is rewritten for every release.

## The uploader

Uploads are driven by **ModUploader**, a Windows tool that is deliberately *not* in this
repository (it bundles `steam_api64.dll`). Point `MOD_UPLOADER` at your copy:

```bash
export MOD_UPLOADER=/path/to/ModUploader-win-x64
```

Its workspace layout is `<MOD_UPLOADER>/Sts2Unlimited/{workshop.json,mod_id.txt,image.png,content/}`,
where `content/` holds exactly the files that ship to subscribers.

## Releasing

1. **Before** running the Release workflow, land this release's `changeNote` in
   `workshop/workshop.json`. The workflow tags whatever is on the default branch, and the
   staging script reads the note from the tag.
2. Run the Release workflow. It runs the headless patch harness, bumps the version, and tags.
3. Stage and upload from the tag:

```bash
git fetch --tags
git checkout v1.2.3                          # release from the tag, never a branch
./scripts/stage-workshop.sh                  # checks, builds, stages; prints the note
./scripts/stage-workshop.sh --upload         # same, then asks before publishing
```

The script refuses to stage when:

- HEAD is not a release tag (`STAGE_ALLOW_UNTAGGED=1` allows a dry run, never an upload);
- tracked files have uncommitted changes;
- `changeNote` is unchanged since the previous tag — nothing updates it automatically, and
  stale notes have shipped twice;
- the headless patch harness fails;
- the built manifest's version is not the tag — tags have pointed at the wrong bump commit
  before, and the manifest is what the Workshop item reports.

Uploading publishes to every subscriber and cannot be undone, so `--upload` always asks
first. Without it, the script prints the manual upload command.

### Things that bite

- **`--workspace Sts2Unlimited` is mandatory.** Running `./ModUploader.exe` bare silently
  creates a throwaway `NewModWorkspace/` copy of `template/` and uploads nothing. Delete it
  if that happens.
- **Steam must be running**, logged into the account that owns the item.
- **The `.pck` is renamed.** `release/` produces `Sts2Unlimited.pck`; what ships is
  lowercase `sts2unlimited.pck`. The staging script handles this.
- **No config file ships.** Releases through v0.6.1 bundled a gitignored
  `sts2unlimited.maxplayers.txt`, and shipped `5` from a working tree while both READMEs said
  `8`. The default now lives in code (`maxPlayersOverride = 8`), and the staging script
  copies an explicit file list, so a stale `.txt` left in `release/` never ships.
- **`MOD_README.md` ships as `README.md`** (see the `BundleRelease` target); the root
  `README.md` is not shipped to the Workshop at all.
