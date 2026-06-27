# Difficulty Scaling Override Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Allow the host to override the player count used for multiplayer difficulty scaling (enemy HP, block, powers), independent of the actual number of connected players.

**Architecture:** Three Harmony transpilers each insert one `call GetEffectivePlayerCount(int)` instruction after each `Players.Count` call in the three game methods that drive difficulty scaling. A toggle + slider in the General settings tab control the override. All settings persist to the existing JSON file.

**Tech Stack:** C# 12, .NET 9, HarmonyX 2.x, Godot 4 (GodotSharp), sts2.dll reflection

## Global Constraints

- Build command: `cmd.exe /c "dotnet build 2>&1"` — must produce zero errors
- Game DLL reference path: `sts2.dll` in project root
- Log prefix convention: `[ClassName]` in square brackets, e.g. `[DifficultyPatch]`
- All public API uses `Sts2Unlimited` namespace
- `MaxPlayers` slider range: internal `[0,14]` + offset 2 → display `[2,16]`
- `DifficultyPlayers` slider range: internal `[0,15]` + offset 1 → display `[1,16]`
- No Co-Authored-By trailers in commit messages
- Settings file: `sts2unlimited.settings.json` next to mod DLL

---

## File Map

| File | Action | Responsibility |
|------|--------|----------------|
| `Sts2Unlimited.cs` | Modify | Add two new fields + property pair, `GetEffectivePlayerCount` helper, update `LoadConfig` to read new JSON keys |
| `SettingsMenuIntegration.cs` | Modify | Replace `SaveMaxPlayers` with `SaveSettings`; inject toggle row + difficulty slider row after the existing Max Players row |
| `DifficultyPatch.cs` | Create | Three Harmony transpilers that redirect `Players.Count` → `GetEffectivePlayerCount(int)` in the three difficulty-scaling methods; `Apply` method called from `Sts2Unlimited` |

---

## Task 1: Feature branch + core data model

**Files:**
- Modify: `Sts2Unlimited.cs`
- Modify: `SettingsMenuIntegration.cs` (SaveMaxPlayers → SaveSettings only)

**Interfaces:**
- Produces:
  - `Sts2Unlimited.DifficultyOverrideEnabled` — `public static bool { get; set; }`
  - `Sts2Unlimited.DifficultyPlayersOverride` — `public static int { get; set; }`
  - `Sts2Unlimited.GetEffectivePlayerCount(int actualCount)` — `public static int`
  - `SettingsMenuIntegration.SaveSettings()` — `public static void` (replaces `SaveMaxPlayers`)

- [ ] **Step 1: Create feature branch**

```bash
git checkout -b feature/difficulty-scaling-override
```

Expected: `Switched to a new branch 'feature/difficulty-scaling-override'`

- [ ] **Step 2: Add fields and helper to Sts2Unlimited.cs**

Open `Sts2Unlimited.cs`. After the `maxPlayersOverride` field and `MaxPlayersOverride` property, add:

```csharp
private static bool difficultyOverrideEnabled = false;
private static int  difficultyPlayersOverride  = 4;

public static bool DifficultyOverrideEnabled { get => difficultyOverrideEnabled; set => difficultyOverrideEnabled = value; }
public static int  DifficultyPlayersOverride  { get => difficultyPlayersOverride; set => difficultyPlayersOverride = value; }

public static int GetEffectivePlayerCount(int actualCount)
    => DifficultyOverrideEnabled ? DifficultyPlayersOverride : actualCount;
```

- [ ] **Step 3: Update LoadConfig to read new keys**

In `Sts2Unlimited.cs`, in `LoadConfig()`, inside the `if (File.Exists(jsonPath))` block, after `MaxPlayersOverride = val.GetInt32(); return;`, replace the early-return approach so ALL keys are read. The updated block should look like:

```csharp
string jsonPath = Path.Combine(dllDir, "sts2unlimited.settings.json");
if (File.Exists(jsonPath))
{
    string json = File.ReadAllText(jsonPath);
    var doc = JsonDocument.Parse(json);
    if (doc.RootElement.TryGetProperty("MaxPlayers", out var val))
        MaxPlayersOverride = val.GetInt32();
    if (doc.RootElement.TryGetProperty("DifficultyEnabled", out var de))
        DifficultyOverrideEnabled = de.GetBoolean();
    if (doc.RootElement.TryGetProperty("DifficultyPlayers", out var dp))
        DifficultyPlayersOverride = Math.Clamp(dp.GetInt32(), 1, 16);
    return;
}
```

(Note: the `return` after reading all keys keeps the legacy text-file fallback for `MaxPlayers` only, which is correct — there is no legacy format for the new keys.)

- [ ] **Step 4: Replace SaveMaxPlayers with SaveSettings in SettingsMenuIntegration.cs**

In `SettingsMenuIntegration.cs`, replace the `SaveMaxPlayers(int value)` method with:

```csharp
public static void SaveSettings()
{
    try
    {
        string json = $"{{\"MaxPlayers\":{Sts2Unlimited.MaxPlayersOverride}," +
                      $"\"DifficultyEnabled\":{(Sts2Unlimited.DifficultyOverrideEnabled ? "true" : "false")}," +
                      $"\"DifficultyPlayers\":{Sts2Unlimited.DifficultyPlayersOverride}}}";
        File.WriteAllText(SettingsPath, json);
    }
    catch (Exception e) { GD.PrintErr($"[Sts2Unlimited] Failed to save settings: {e.Message}"); }
}
```

- [ ] **Step 5: Update existing slider call site**

In `SettingsMenuIntegration.cs`, in `ConfigureMaxPlayersSlider`, the `ValueChanged` handler currently calls `SaveMaxPlayers(players)`. Replace that call with `SaveSettings()`. The handler should now look like:

```csharp
nslider.Connect(Godot.Range.SignalName.ValueChanged, Callable.From<double>(v =>
{
    int players = (int)Math.Round(v) + PLAYER_OFFSET;
    Sts2Unlimited.MaxPlayersOverride = players;
    SaveSettings();
    slider.GetNodeOrNull("SliderValue")?.Set("text", $"{players}");
}));
```

- [ ] **Step 6: Build and verify**

```bash
cmd.exe /c "dotnet build 2>&1"
```

Expected: `Build succeeded.` with 0 errors. Warnings are acceptable.

- [ ] **Step 7: Commit**

```bash
git add Sts2Unlimited.cs SettingsMenuIntegration.cs
git commit -m "feat: add DifficultyOverrideEnabled/DifficultyPlayersOverride fields and unified SaveSettings"
```

---

## Task 2: Transpiler patches (DifficultyPatch.cs)

**Files:**
- Create: `DifficultyPatch.cs`
- Modify: `Sts2Unlimited.cs` (add `DifficultyPatch.Apply(harmony)` call)

**Interfaces:**
- Consumes: `Sts2Unlimited.GetEffectivePlayerCount(int)` from Task 1
- Produces: `DifficultyPatch.Apply(Harmony harmony)` — `public static void`

**Background — why these three methods:**
The game scales enemy difficulty in exactly three places:
1. `MegaCrit.Sts2.Core.Combat.CombatState.CreateCreature` — calls `creature.ScaleMonsterHpForMultiplayer(Encounter, Players.Count, RunState.CurrentActIndex)` → controls enemy HP
2. `MegaCrit.Sts2.Core.Models.Singleton.MultiplayerScalingModel.ModifyBlockMultiplicative` — reads `_runState.Players.Count` → controls enemy block gains
3. `MegaCrit.Sts2.Core.Models.Singleton.MultiplayerScalingModel.ModifyPowerAmountGiven` — reads `_runState.Players.Count` → controls enemy power amounts

In all three methods, `Players` is `IReadOnlyList<Player>`, so the IL call for `.Count` resolves to `callvirt instance int32 IReadOnlyCollection`1<Player>::get_Count()`. No other `.Count` calls in these methods touch a `Player`-typed collection, so this generic-argument check is the discriminator.

- [ ] **Step 1: Create DifficultyPatch.cs**

Create a new file `DifficultyPatch.cs` in the project root with this content:

```csharp
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;

namespace Sts2Unlimited;

public static class DifficultyPatch
{
    private static readonly MethodInfo _getEffectivePlayerCount =
        typeof(Sts2Unlimited).GetMethod(nameof(Sts2Unlimited.GetEffectivePlayerCount),
            BindingFlags.Public | BindingFlags.Static)!;

    // Matches callvirt IReadOnlyCollection<Player>::get_Count() — the only .Count call
    // on a Player collection in each of the three target methods.
    private static bool IsPlayersCountCall(CodeInstruction ci)
    {
        if (ci.opcode != OpCodes.Callvirt) return false;
        if (ci.operand is not MethodInfo mi) return false;
        if (mi.Name != "get_Count") return false;
        var dt = mi.DeclaringType;
        if (dt == null || !dt.IsGenericType) return false;
        return dt.GetGenericArguments()[0].Name == "Player";
    }

    // Single transpiler shared across all three target methods.
    // Inserts `call GetEffectivePlayerCount(int)` after each Players.Count call,
    // so the int on the stack becomes the effective (possibly overridden) count.
    public static IEnumerable<CodeInstruction> Transpile_PlayerCount(
        IEnumerable<CodeInstruction> instructions,
        MethodBase original)
    {
        var codes = new List<CodeInstruction>(instructions);
        int patched = 0;

        for (int i = 0; i < codes.Count; i++)
        {
            if (!IsPlayersCountCall(codes[i])) continue;
            codes.Insert(i + 1, new CodeInstruction(OpCodes.Call, _getEffectivePlayerCount));
            i++; // skip past the instruction we just inserted
            patched++;
        }

        if (patched == 0)
            Log.LogMessage(LogLevel.Warn, LogType.Generic,
                $"[DifficultyPatch] No Players.Count call found in {original?.DeclaringType?.Name}.{original?.Name} — patch may be broken.");
        else
            Log.LogMessage(LogLevel.Debug, LogType.Generic,
                $"[DifficultyPatch] Patched {patched} Players.Count call(s) in {original?.DeclaringType?.Name}.{original?.Name}.");

        return codes;
    }

    public static void Apply(Harmony harmony)
    {
        var transpilerMethod = typeof(DifficultyPatch).GetMethod(
            nameof(Transpile_PlayerCount), BindingFlags.Public | BindingFlags.Static)!;
        var transpiler = new HarmonyMethod(transpilerMethod);

        // 1. CombatState.CreateCreature — controls enemy HP scaling
        var combatStateType = Type.GetType(
            "MegaCrit.Sts2.Core.Combat.CombatState, sts2", false);
        if (combatStateType != null)
        {
            var createCreature = combatStateType.GetMethod(
                "CreateCreature", BindingFlags.Public | BindingFlags.Instance);
            if (createCreature != null)
            {
                harmony.Patch(createCreature, transpiler: transpiler);
                Log.LogMessage(LogLevel.Info, LogType.Generic,
                    "[DifficultyPatch] Patched CombatState.CreateCreature (HP scaling).");
            }
            else
                Log.LogMessage(LogLevel.Warn, LogType.Generic,
                    "[DifficultyPatch] CombatState.CreateCreature not found.");
        }
        else
            Log.LogMessage(LogLevel.Warn, LogType.Generic,
                "[DifficultyPatch] CombatState type not found.");

        // 2 & 3. MultiplayerScalingModel — controls enemy block and power scaling
        var scalingModelType = Type.GetType(
            "MegaCrit.Sts2.Core.Models.Singleton.MultiplayerScalingModel, sts2", false);
        if (scalingModelType != null)
        {
            var modifyBlock = scalingModelType.GetMethod(
                "ModifyBlockMultiplicative", BindingFlags.Public | BindingFlags.Instance);
            if (modifyBlock != null)
            {
                harmony.Patch(modifyBlock, transpiler: transpiler);
                Log.LogMessage(LogLevel.Info, LogType.Generic,
                    "[DifficultyPatch] Patched MultiplayerScalingModel.ModifyBlockMultiplicative (block scaling).");
            }
            else
                Log.LogMessage(LogLevel.Warn, LogType.Generic,
                    "[DifficultyPatch] ModifyBlockMultiplicative not found.");

            var modifyPower = scalingModelType.GetMethod(
                "ModifyPowerAmountGiven", BindingFlags.Public | BindingFlags.Instance);
            if (modifyPower != null)
            {
                harmony.Patch(modifyPower, transpiler: transpiler);
                Log.LogMessage(LogLevel.Info, LogType.Generic,
                    "[DifficultyPatch] Patched MultiplayerScalingModel.ModifyPowerAmountGiven (power scaling).");
            }
            else
                Log.LogMessage(LogLevel.Warn, LogType.Generic,
                    "[DifficultyPatch] ModifyPowerAmountGiven not found.");
        }
        else
            Log.LogMessage(LogLevel.Warn, LogType.Generic,
                "[DifficultyPatch] MultiplayerScalingModel type not found.");
    }
}
```

- [ ] **Step 2: Register DifficultyPatch.Apply in Sts2Unlimited.cs**

In `Sts2Unlimited.cs`, in `ApplyHarmonyPatches()`, add this call just before the closing `}` of the try block (after the `ChestPatch` block):

```csharp
// Patch difficulty scaling to support player-count override
DifficultyPatch.Apply(harmony);
```

- [ ] **Step 3: Build and verify**

```bash
cmd.exe /c "dotnet build 2>&1"
```

Expected: `Build succeeded.` with 0 errors.

- [ ] **Step 4: Verify patch registration in game logs**

Launch the game and check logs (`strings "/mnt/c/Users/anjivo/AppData/Roaming/SlayTheSpire2/logs/godot.log" | grep DifficultyPatch`).

Expected lines (order may vary):
```
[DifficultyPatch] Patched CombatState.CreateCreature (HP scaling).
[DifficultyPatch] Patched MultiplayerScalingModel.ModifyBlockMultiplicative (block scaling).
[DifficultyPatch] Patched MultiplayerScalingModel.ModifyPowerAmountGiven (power scaling).
```

No `Warn` lines about missing types/methods.

- [ ] **Step 5: Commit**

```bash
git add DifficultyPatch.cs DifficultyPatch.cs.uid Sts2Unlimited.cs
git commit -m "feat: add DifficultyPatch transpilers for HP, block, and power scaling"
```

(If `DifficultyPatch.cs.uid` is not auto-generated yet, omit it from the add — Godot generates it on import.)

---

## Task 3: Settings menu UI

**Files:**
- Modify: `SettingsMenuIntegration.cs`

**Interfaces:**
- Consumes:
  - `Sts2Unlimited.DifficultyOverrideEnabled` (bool, read/write)
  - `Sts2Unlimited.DifficultyPlayersOverride` (int, read/write)
  - `SettingsMenuIntegration.SaveSettings()` from Task 1
- Produces: Toggle row + slider row injected into General tab after Max Players row

**Key structural facts (from game decompilation):**
- Settings rows in the General tab are `MarginContainer` nodes, each with a `MegaRichTextLabel` child named `"Label"` and a control child
- `FindNodeByType(screen, tickboxType)` finds the first `NSettingsTickbox` in the entire screen tree (may be in the Graphics tab — that's fine, we just use it as a Duplicate template)
- The tickbox has `IsTicked` property (bool) and emits signal `"Toggled"` with itself as argument
- The `NSettingsTickbox` connects its own internals in `_Ready`/`ConnectSignals` — just disconnect external `Toggled` handlers before adding ours
- Insertion indices follow the existing Max Players pattern: MoveChild to `insertAt + N` sequentially

- [ ] **Step 1: Add constants for the difficulty slider range**

In `SettingsMenuIntegration.cs`, alongside the existing `PLAYER_MIN/MAX/OFFSET` constants, add:

```csharp
private const int DIFFICULTY_MIN    = 1;
private const int DIFFICULTY_MAX    = 16;
private const int DIFFICULTY_OFFSET = DIFFICULTY_MIN;          // 1
private const int DIFF_INTERNAL_MIN = 0;
private const int DIFF_INTERNAL_MAX = DIFFICULTY_MAX - DIFFICULTY_OFFSET; // 15
```

- [ ] **Step 2: Add the toggle row and difficulty slider row injection**

In `SettingsMenuIntegration.cs`, in `Patch_NSettingsScreen_Ready`, after the block that inserts `divider` and `sliderRow` (the Max Players slider), add a second insertion block for the toggle and difficulty slider. The full additional block (append inside the `try` block, after the `tree.Connect(... OneShot)` section for the max players slider):

```csharp
// ── Difficulty scaling override ───────────────────────────────────────────
var tickboxType = Type.GetType(
    "MegaCrit.Sts2.Core.Nodes.Screens.Settings.NSettingsTickbox, sts2", false);
if (tickboxType == null)
{
    GD.PrintErr("[Sts2Unlimited] NSettingsTickbox type not found — difficulty toggle skipped.");
    return;
}
Node tickboxTemplate = FindNodeByType(screen, tickboxType);
if (tickboxTemplate == null)
{
    GD.PrintErr("[Sts2Unlimited] NSettingsTickbox node not found — difficulty toggle skipped.");
    return;
}

Node toggleRow      = (Node)tickboxTemplate.GetParent().Duplicate(15);
Node diffSliderRow  = (Node)templateRow.Duplicate(15);
toggleRow.Name     = "Sts2UnlimitedDifficultyToggleRow";
diffSliderRow.Name = "Sts2UnlimitedDifficultySliderRow";

targetParent.AddChild(toggleRow);
targetParent.AddChild(diffSliderRow);
targetParent.MoveChild(toggleRow,     insertAt + 3);
targetParent.MoveChild(diffSliderRow, insertAt + 4);

// Visibility of the difficulty slider mirrors the toggle state
diffSliderRow.Visible = Sts2Unlimited.DifficultyOverrideEnabled;

tree.Connect(SceneTree.SignalName.ProcessFrame, Callable.From(() =>
{
    try
    {
        ConfigureDifficultyToggle(tickboxType, toggleRow, diffSliderRow);
        ConfigureDifficultySlider(slider, diffSliderRow, Sts2Unlimited.DifficultyPlayersOverride);
    }
    catch (Exception e)
    {
        GD.PrintErr($"[Sts2Unlimited] Difficulty UI config error: {e.Message}\n{e.StackTrace}");
    }
}), (uint)GodotObject.ConnectFlags.OneShot);
```

- [ ] **Step 3: Implement ConfigureDifficultyToggle**

Add a new private static method to `SettingsMenuIntegration.cs`:

```csharp
private static void ConfigureDifficultyToggle(Type tickboxType, Node toggleRow, Node diffSliderRow)
{
    // Set the row label
    var nameLabel = toggleRow.GetNodeOrNull("Label");
    if (nameLabel != null)
        nameLabel.Set("text", "Adjust Multiplayer Difficulty");
    else
        GD.PrintErr("[Sts2Unlimited] 'Label' not found in toggleRow.");

    // Find the tickbox control inside the duplicated row
    Node tickbox = FindNodeByType(toggleRow, tickboxType);
    if (tickbox == null)
    {
        GD.PrintErr("[Sts2Unlimited] NSettingsTickbox not found in toggleRow.");
        return;
    }

    // Disconnect any existing Toggled handlers from the duplicate
    foreach (var conn in tickbox.GetSignalConnectionList("Toggled"))
        tickbox.Disconnect("Toggled", conn["callable"].As<Callable>());

    // Reflect initial ticked state
    tickbox.Set("IsTicked", Sts2Unlimited.DifficultyOverrideEnabled);

    // Wire our handler: toggle the override and show/hide the slider
    tickbox.Connect("Toggled", Callable.From<GodotObject>(sender =>
    {
        bool ticked = (bool)sender.Get("IsTicked");
        Sts2Unlimited.DifficultyOverrideEnabled = ticked;
        diffSliderRow.Visible = ticked;
        SaveSettings();
    }));

    GD.Print("[Sts2Unlimited] Difficulty toggle configured.");
}
```

- [ ] **Step 4: Implement ConfigureDifficultySlider**

Add a new private static method to `SettingsMenuIntegration.cs`:

```csharp
private static void ConfigureDifficultySlider(Node sliderTemplate, Node diffSliderRow, int currentDifficulty)
{
    // Set the row label
    var nameLabel = diffSliderRow.GetNodeOrNull("Label");
    if (nameLabel != null)
        nameLabel.Set("text", "Difficulty Scaling");
    else
        GD.PrintErr("[Sts2Unlimited] 'Label' not found in diffSliderRow.");

    // Find the NSettingsSlider and then its inner NSlider Range
    var masterType = Type.GetType(
        "MegaCrit.Sts2.Core.Nodes.Screens.Settings.NMasterVolumeSlider, sts2", false);
    Node diffSlider = FindNodeByType(diffSliderRow, masterType);
    if (diffSlider == null)
    {
        GD.PrintErr("[Sts2Unlimited] NMasterVolumeSlider not found in diffSliderRow.");
        return;
    }

    var nslider = diffSlider?.GetNodeOrNull("Slider") as Godot.Range;
    if (nslider == null)
    {
        GD.PrintErr("[Sts2Unlimited] 'Slider' child not found in difficulty slider.");
        return;
    }

    // Disconnect existing handlers (carries over from NMasterVolumeSlider template)
    foreach (var conn in nslider.GetSignalConnectionList("value_changed"))
        nslider.Disconnect("value_changed", conn["callable"].As<Callable>());

    int internalValue = Math.Clamp(currentDifficulty - DIFFICULTY_OFFSET, DIFF_INTERNAL_MIN, DIFF_INTERNAL_MAX);

    nslider.MinValue = DIFF_INTERNAL_MIN;
    nslider.MaxValue = DIFF_INTERNAL_MAX;
    nslider.Step     = 1;
    nslider.Value    = internalValue;
    nslider.Call("SetValueWithoutAnimation", (double)internalValue);

    nslider.Connect(Godot.Range.SignalName.ValueChanged, Callable.From<double>(v =>
    {
        int difficulty = (int)Math.Round(v) + DIFFICULTY_OFFSET;
        Sts2Unlimited.DifficultyPlayersOverride = difficulty;
        SaveSettings();
        diffSlider.GetNodeOrNull("SliderValue")?.Set("text", $"{difficulty}");
    }));

    // Initial value display
    diffSlider.GetNodeOrNull("SliderValue")?.Set("text", $"{currentDifficulty}");

    GD.Print($"[Sts2Unlimited] Difficulty slider configured: range [{DIFFICULTY_MIN},{DIFFICULTY_MAX}], current={currentDifficulty}");
}
```

- [ ] **Step 5: Build and verify**

```bash
cmd.exe /c "dotnet build 2>&1"
```

Expected: `Build succeeded.` with 0 errors.

- [ ] **Step 6: In-game smoke test**

Launch the game:
```bash
powershell.exe -Command "Start-Process 'G:\Games\SteamLibrary\steamapps\common\Slay the Spire 2\SlayTheSpire2.exe' -ArgumentList '-fastmp=host','--save-dir=user://host'"
```

Open Settings → General tab and verify:
- [ ] "Adjust Multiplayer Difficulty" toggle appears below the Max Players slider
- [ ] Toggle is OFF by default (not ticked)
- [ ] "Difficulty Scaling" slider is hidden while toggle is OFF
- [ ] Toggling ON reveals the slider, showing value 4
- [ ] Moving the slider updates the displayed number (1–16)
- [ ] Toggling back OFF hides the slider
- [ ] Closing and reopening settings preserves the toggle/slider state (check JSON was written)

Check logs for expected output:
```bash
strings "/mnt/c/Users/anjivo/AppData/Roaming/SlayTheSpire2/logs/godot.log" | grep -E "Sts2Unlimited|DifficultyPatch"
```

Expected (no Warn lines about missing nodes):
```
[DifficultyPatch] Patched CombatState.CreateCreature (HP scaling).
[DifficultyPatch] Patched MultiplayerScalingModel.ModifyBlockMultiplicative (block scaling).
[DifficultyPatch] Patched MultiplayerScalingModel.ModifyPowerAmountGiven (power scaling).
[Sts2Unlimited] Difficulty toggle configured.
[Sts2Unlimited] Difficulty slider configured: range [1,16], current=4
```

- [ ] **Step 7: In-game difficulty verification**

Host a multiplayer lobby with 2 real players. Without the override, enemies should have 2-player HP. Enable the override, set Difficulty Scaling to 8. Start a run and verify enemies have notably higher HP than normal 2-player scaling. Disable the override and start another run to confirm HP returns to 2-player values.

- [ ] **Step 8: Commit**

```bash
git add SettingsMenuIntegration.cs
git commit -m "feat: inject difficulty scaling toggle and slider into settings menu"
```
