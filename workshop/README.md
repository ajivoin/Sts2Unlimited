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

```bash
git checkout v1.2.3                  # release from the tag, not from main
./scripts/stage-workshop.sh          # builds, verifies, stages into the workspace
```

Then review the staged change note and upload:

```bash
cd "$MOD_UPLOADER" && ./ModUploader.exe upload --workspace Sts2Unlimited
```

The upload is intentionally a separate manual step — it publishes to every subscriber and
cannot be undone.

### Things that bite

- **`--workspace Sts2Unlimited` is mandatory.** Running `./ModUploader.exe` bare silently
  creates a throwaway `NewModWorkspace/` copy of `template/` and uploads nothing. Delete it
  if that happens.
- **Steam must be running**, logged into the account that owns the item.
- **The `.pck` is renamed.** `release/` produces `Sts2Unlimited.pck`; what ships is
  lowercase `sts2unlimited.pck`. The staging script handles this.
- **`sts2unlimited.maxplayers.txt` is gitignored as a local setting**, so whatever is in
  your working tree gets bundled. Releases through v0.6.1 shipped `5` this way while both
  READMEs documented `8`. The staging script refuses to stage anything else.
- **`MOD_README.md` ships as `README.md`** (see the `BundleRelease` target); the root
  `README.md` is not shipped to the Workshop at all.
