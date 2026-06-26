# Difficulty Scaling Override — Design Spec

**Date:** 2026-06-26  
**Feature:** Allow the host to override the player count used for enemy difficulty scaling, independently of the actual number of connected players.

---

## Problem

The game scales enemy HP, block, and power amounts by the number of connected players. A 4-player lobby always gets 4-player difficulty. There is no way to play at a different difficulty level (e.g., 8-player scaling for a harder run, or 2-player scaling for an easier one).

---

## Goals

- Host can set a "virtual" player count for difficulty scaling, independent of max players
- Default behavior is unchanged: when the override is off, difficulty scales with actual connected players
- Setting persists to disk and is restored on next session
- UI matches the game's existing settings style (toggle to reveal a slider)

---

## Out of Scope

- Client-side display of the difficulty override (UI is host-only, clients play at whatever the host's patch produces)
- Changing in the middle of a run (the override is read at combat start; mid-run changes won't retroactively rescale existing enemies)

---

## Data Model

### New fields in `Sts2Unlimited`

```csharp
private static bool difficultyOverrideEnabled = false;
private static int  difficultyPlayersOverride  = 4;

public static bool DifficultyOverrideEnabled { get; set; }
public static int  DifficultyPlayersOverride  { get; set; }

public static int GetEffectivePlayerCount(int actualCount)
    => DifficultyOverrideEnabled ? DifficultyPlayersOverride : actualCount;
```

`DifficultyPlayersOverride` is always a valid player count (1–16). `DifficultyOverrideEnabled` is the sole gate; the two values are independent.

### Settings JSON

```json
{
  "MaxPlayers": 8,
  "DifficultyEnabled": false,
  "DifficultyPlayers": 4
}
```

- Absent `DifficultyEnabled` → `false`
- Absent `DifficultyPlayers` → `4`
- `SaveMaxPlayers(int)` is replaced by a unified `SaveSettings()` that writes all three keys at once

---

## Transpiler Patches (`DifficultyPatch.cs`)

Three Harmony transpilers, all using the same instruction-insertion pattern as `PacketSizePatch`:

| Method | What we patch |
|--------|---------------|
| `CombatState.CreateCreature` | `callvirt IReadOnlyList<Player>::get_Count()` → insert `call GetEffectivePlayerCount(int)` after it. This is the `Players.Count` fed into `ScaleMonsterHpForMultiplayer`. |
| `MultiplayerScalingModel.ModifyBlockMultiplicative` | Same: find `callvirt get_Count()` on the Players list, insert our helper call. |
| `MultiplayerScalingModel.ModifyPowerAmountGiven` | Same. This method reads `Players.Count` twice (guard + computation); both hits are intercepted by the same transpiler pass. |

**Helper:**
```csharp
public static int GetEffectivePlayerCount(int actualCount)
    => DifficultyOverrideEnabled ? DifficultyPlayersOverride : actualCount;
```

When `DifficultyOverrideEnabled` is false, the helper is a no-op passthrough — zero runtime cost to the normal path.

**Patch registration** added alongside existing patches in `Sts2Unlimited.ApplyHarmonyPatches()`.

---

## Settings Menu UI (`SettingsMenuIntegration.cs`)

Inserted immediately after the Max Players row in the General tab.

### Toggle row — "Adjust Multiplayer Difficulty"

- Template: find a `NSettingsTickbox` node in the screen via `FindNodeByType`, `Duplicate(15)` it
- Node name: `Sts2UnlimitedDifficultyToggleRow`
- Label: `"Adjust Multiplayer Difficulty"`
- Initial ticked state: `DifficultyOverrideEnabled`
- `Toggled` signal handler:
  1. Flip `DifficultyOverrideEnabled`
  2. Set difficulty slider row `Visible` to match
  3. Call `SaveSettings()`

### Slider row — "Difficulty Scaling"

- Template: same `NMasterVolumeSlider` duplication approach as Max Players slider
- Node name: `Sts2UnlimitedDifficultySliderRow`
- Label: `"Difficulty Scaling"`
- Internal range: `[0, 15]`, display offset `+1` → shows `[1, 16]`
- Initial value: `DifficultyPlayersOverride`
- Initial visibility: `DifficultyOverrideEnabled`
- `ValueChanged` signal handler:
  1. Update `DifficultyPlayersOverride`
  2. Update value label
  3. Call `SaveSettings()`

### Insertion order (after existing Max Players row)

```
... (existing content)
[Max Players Divider]
[Max Players Slider Row]       ← already exists
[Difficulty Toggle Row]        ← new
[Difficulty Slider Row]        ← new, hidden until toggle is on
```

---

## Files Changed

| File | Change |
|------|--------|
| `Sts2Unlimited.cs` | Add `DifficultyOverrideEnabled`, `DifficultyPlayersOverride`, `GetEffectivePlayerCount`; update `LoadConfig`; replace `SaveMaxPlayers` with `SaveSettings` |
| `SettingsMenuIntegration.cs` | Add toggle row + slider row injection; update save call |
| `DifficultyPatch.cs` | New file — three transpilers + patch registration helper |

---

## Testing Checklist

- [ ] Default behavior: game with override off scales difficulty by actual player count (no regression)
- [ ] Toggle on → slider appears, difficulty uses override value
- [ ] Toggle off → slider hides, difficulty reverts to actual player count
- [ ] Override = 1 → enemies have solo (unscaled) HP/block/powers even in multiplayer
- [ ] Override = 8 in a 2-player lobby → enemies have 8-player scaling
- [ ] Setting persists after closing and reopening the settings menu
- [ ] Setting persists after relaunching the game (JSON round-trip)
- [ ] Max Players slider still saves correctly (unified `SaveSettings` didn't break it)
- [ ] No crash when `DifficultyPlayers` key is absent from JSON (backward compat)
