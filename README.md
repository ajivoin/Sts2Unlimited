# STS 2 Unlimited

Play **Slay the Spire 2** multiplayer with any number of players. The vanilla game caps lobbies at 4; this mod removes that limit and lets you configure it from the in-game settings menu.

![screenshot](example.png)

## Installation

**Steam Workshop (recommended):** Subscribe at [steamcommunity.com/sharedfiles/filedetails/?id=3747509118](https://steamcommunity.com/sharedfiles/filedetails/?id=3747509118) — the game loads it automatically.

**Manual:** Download `Sts2Unlimited.zip` from the [latest release](../../releases/latest) and extract all files into your Slay the Spire 2 `mods/` folder:
```
mods/
└── Sts2Unlimited/
    ├── sts2unlimited.dll
    ├── sts2unlimited.pck
    ├── Sts2Unlimited.json
    ├── icon.svg
    └── sts2unlimited.maxplayers.txt
```

## Known Issues

| Platform | Symptom | Fix |
|---|---|---|
| Linux | Mod shows as enabled but errors on startup; lobbies hang on "loading" | [Force-load `libgcc_s`](#linux-force-load-libgcc_s) on the game binary |

### Linux: force-load `libgcc_s`

Harmony generates `/tmp/mm-exhelper.so` at runtime, which needs `_Unwind_RaiseException` from `libgcc_s.so.1`. Godot loads .NET with `RTLD_LOCAL`, so the symbol isn't in the global namespace when the helper is `dlopen`ed and it fails to load. Adding `libgcc_s.so.1` as a direct `NEEDED` entry on the main game binary forces it into the global namespace at startup:

```bash
sudo dnf install patchelf   # or apt/pacman equivalent
cd ~/.local/share/Steam/steamapps/common/Slay\ the\ Spire\ 2
cp SlayTheSpire2 SlayTheSpire2.bak
patchelf --add-needed libgcc_s.so.1 SlayTheSpire2
```

Steam game updates overwrite the binary, so re-apply after every update. See [#9](https://github.com/ajivoin/Sts2Unlimited/issues/9) for the full investigation (credit: @pxlnght).

## Configuration

The easiest way is the **in-game settings menu**: open Settings and use the "Max Players" slider (range: 2–256). Changes save automatically.

Alternatively, edit `sts2unlimited.maxplayers.txt` in the mods folder and restart the game. The default is **8 players**.

## How It Works

The game's networking layer already supports arbitrary player counts — the limit is enforced entirely by hardcoded `4` values at the lobby initialization sites. This mod uses [Harmony](https://github.com/pardeike/Harmony) to prefix-patch those methods and substitute the configured value:

| Patched Method | Purpose |
|---|---|
| `NetHostGameService.StartSteamHost()` | Steam lobby creation |
| `NetHostGameService.StartENetHost()` | ENet (direct IP) lobby creation |
| `NCharacterSelectScreen.InitializeMultiplayerAsHost()` | Standard run lobby |
| `NCustomRunScreen.InitializeMultiplayerAsHost()` | Custom run lobby |

Multiplayer packets carry the lobby player list behind a 3-bit length header, capping it
at 7. The mod transpiles those call sites to a fixed 5-bit header (always 5, never your
local Max Players setting — peers must agree on the wire format regardless of what each
has configured).

Because another mod can cause the JIT to inline the game's original, unpatched
`Serialize` before this mod loads — STS2-RitsuLib patches
`NetMessageBus.SerializeMessage<LobbyBeginRunMessage>`, and inlining copies IL that a
later transpile can no longer reach — the same header width is also enforced at
`PacketWriter.WriteList` / `PacketReader.ReadList`, which are never inlined. Without that
guard, a host running both mods writes a 3-bit header while clients read a 5-bit one and
no client can join.

Because the desync happens at write time, the **host** in particular needs this update
to fix the RitsuLib case — a host still running an older Sts2Unlimited writes the 3-bit
header regardless of which version any client has installed.

Settings are persisted to `sts2unlimited.settings.json` via the game's own settings API, with fallback to the legacy text file.

## Limitations

- UI screens (character select, etc.) may feel crowded with many players
- Game balance is tuned for 4 players; behavior beyond that is untested
- Steam lobby size limits may apply regardless of this mod's setting

## Building from Source

Requires .NET 9 and the `sts2.dll` game reference assembly placed in the project root.

```bash
dotnet build --configuration ExportRelease
```

Output is bundled to `release/` by the `BundleRelease` MSBuild target.

### Project Structure

```
Sts2Unlimited.cs            — Harmony patches, config loading, entry point
SettingsMenuIntegration.cs  — Injects slider into the game's settings screen
sts2unlimited.csproj        — Build config and BundleRelease target
Sts2Unlimited.json          — Mod metadata (name, version, author)
export_presets.cfg          — Godot resource export config
```

## License

[MIT](LICENSE.md)
