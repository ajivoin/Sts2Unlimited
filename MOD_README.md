# STS 2 Unlimited

Play **Slay the Spire 2** multiplayer with any number of players instead of the hardcoded limit of 4.

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

Open the in-game **Settings** menu and use the **Max Players** slider. Changes are saved automatically.

To configure without launching the game, edit `sts2unlimited.maxplayers.txt` in the mods folder and set it to any number. Restart the game to apply.

The default is **8 players**.

## Troubleshooting

**Lobby still shows 4 player slots**
- Restart the game after changing the config
- Make sure `sts2unlimited.maxplayers.txt` is in the same folder as the DLLs and contains only a number

**Game crashes on startup**
- Verify all files are present in the mods folder
- Check that the files are in the correct `mods/` directory

**Slider not visible in Settings**
- Ensure `Sts2Unlimited.pck` is present alongside the DLL
- Check the game log for any mod loading errors

## Limitations

- Character selection and lobby screens may feel crowded with many players
- Game balance is designed for 4 players; performance and balance with more is untested
- Steam lobby size limits may apply independently of this mod

## Credits

Created by Andrew Jivoin
