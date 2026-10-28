# STS 2 Unlimited

Play **Slay the Spire 2** multiplayer with up to 16 players instead of the hardcoded limit of 4.

## Installation

**Steam Workshop (recommended):** Subscribe at [steamcommunity.com/sharedfiles/filedetails/?id=3747509118](https://steamcommunity.com/sharedfiles/filedetails/?id=3747509118) — the game loads it automatically.

**Manual:** Copy **all files** from this package into your Slay the Spire 2 `mods/` folder:
- **Steam (Windows)**: `C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\mods\`

Files to copy:
- `sts2unlimited.dll`
- `sts2unlimited.pck`
- `Sts2Unlimited.json`
- `icon.svg`
- `sts2unlimited.maxplayers.txt`

Launch the game — the mod loads automatically.

## Configuration

Open the in-game **Settings** menu and use the **Max Players** slider (range: 2–16). Changes are saved automatically and apply to the next lobby you host.

To configure without launching the game, edit `sts2unlimited.maxplayers.txt` in the same folder as the mod's other files and restart. This is a legacy fallback: once you have used the in-game slider the value lives in `sts2unlimited.settings.json` and the text file is ignored.

The default is **8 players** and 16 is the maximum. Higher values are not clamped, but they break lobbies — every peer has to agree on the packet format, which is sized for 16.

## Troubleshooting

**Lobby still shows 4 player slots**
- Restart the game after editing `sts2unlimited.maxplayers.txt` (slider changes need no restart; they apply to the next lobby you host)
- Make sure `sts2unlimited.maxplayers.txt` is in the same folder as the DLLs and contains only a number

**Game crashes on startup**
- Verify all files are present in the mods folder
- Check that the files are in the correct `mods/` directory

**Slider not visible in Settings**
- Ensure `sts2unlimited.pck` is present alongside the DLL
- Check the game log for any mod loading errors

## Limitations

- Character selection and lobby screens may feel crowded with many players
- Game balance is designed for 4 players; performance and balance with more is untested
- Steam lobby size limits may apply independently of this mod

## Credits

Created by Andrew Jivoin
