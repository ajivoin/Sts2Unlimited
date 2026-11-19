# Difficulty Block Scaling on v0.113 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make "Adjust Multiplayer Difficulty" scale enemy block again on game v0.113.x. Then make the headless harness fail whenever a patch target in `DifficultyPatch` stops resolving against `sts2.dll`. Also prove the fix end to end with a live 2-peer sts2-testkit scenario that asserts the reporter's exact block numbers.

**Architecture:** In v0.113, `MultiplayerScalingModel` moved from `MegaCrit.Sts2.Core.Models.Singleton` to `MegaCrit.Sts2.Core.Models`. `DifficultyPatch.Apply` looks the type up by a hard-coded string, so the lookup returned null and the block transpiler was never installed. The only signal was one `[WARN]` line, and the mod still reported "loaded successfully". The fix tries the new name first and falls back to the legacy name. `DifficultyPatch` also gets test seams (a `LogSink` and a player-count resolver) so the harness can compile it. Its patches register with `PatchRegistry`, so a dead hook makes the mod load as DEGRADED. New harness tests run the real transpiler over the real IL. A source scan checks that every `MegaCrit.Sts2.*` type-name string in the mod resolves.

**Tech Stack:** C# / .NET 9, HarmonyLib 2.4.2, the headless harness in `tests/PatchTests` (run against the repo-root `sts2.dll`).

**Spec:** Bug report (2026-11-19, game v0.113.1), quoted here in full:
> "Adjust Multiplayer Difficulty" doesn't affect enemy block. The log shows `[WARN] [DifficultyPatch] MultiplayerScalingModel type not found.` The class moved from Models.Singleton to Models in v0.113. With 2 players and Difficulty Scaling set to 9, an enemy's block of 10 became 20 instead of 99. Checking both namespaces fixes it.

Verified on 2026-11-19 against the repo-root `sts2.dll` with `ilspycmd`:
- `MegaCrit.Sts2.Core.Models.MultiplayerScalingModel` exists. The `.Singleton` name does not.
- `ModifyBlockMultiplicative` contains exactly **one** `callvirt IReadOnlyCollection<Player>::get_Count()` (IL_0032). That call is the rewrite site.
- `PowerModel`, the five `Powers.*Power` types and `Entities.Creatures.Creature` are still at their old names.

## Global Constraints

- Target framework stays `net9.0`. Do not bump it here (see the .NET 9 EOL note in memory).
- Harness command: `dotnet run --project tests/PatchTests`. The exit code is 0 only if every check passes.
- No harness test may reach the game's real `Log.LogMessage`, because it segfaults outside a game process. Every class compiled into the harness routes logging through a `LogSink` that `Main()` sets up front.
- Keep `DifficultyPatch.cs`'s existing 4-space indentation.
- Never write to the real Steam save profile during in-game verification.

## File Structure

| File | Change |
|---|---|
| `DifficultyPatch.cs` | Add a `LogSink` seam, a `PlayerCountResolver` seam, type-name constants plus a `ResolveFirstType` fallback, `GetModifyBlockMethod()`, `LastTranspilePatchCount`, and `PatchRegistry` records. Remove the direct references to `Sts2Unlimited`. |
| `Sts2Unlimited.cs` | Wire `DifficultyPatch.PlayerCountResolver = GetEffectivePlayerCount` before `DifficultyPatch.Apply`. |
| `tests/PatchTests/PatchTests.csproj` | Compile `DifficultyPatch.cs` into the harness. |
| `tests/PatchTests/Program.cs` | Add the `DifficultyPatch` sink, plus five tests: targets resolve, block transpile over real IL, fallback order, HP-prefix parameter binding, and the type-name literal scan. |
| **sts2-testkit** `driver/Sts2TestDriver.Mod/MoveBlockConsoleCmd.cs` | New generic driver command `moveblock`: gives an enemy block the way a monster move does, then logs what the hook pipeline produced. Lives in the separate kit repo (`/mnt/c/Users/anjivo/sts2-testkit`, branch `main`, no remote). |
| **sts2-testkit** `driver/Sts2TestDriver.Mod/Sts2TestDriver.json`, `README.md` | Driver version bump `v0.4.0` → `v0.5.0`; document `moveblock`. |
| `tests/testkit/sts2unlimited_profile.py` (branch `testkit-scenarios`) | Add a `moveblock` pattern builder, and add `DifficultyPatch`'s silent-failure lines to `failure_patterns`. |
| `tests/testkit/test_difficulty_block.py` (branch `testkit-scenarios`) | New live scenario: the reporter's exact case (99), plus an override-off control (20). |

---

### Task 1: Make DifficultyPatch compilable in the harness (behaviour-preserving)

This task changes no behaviour. It only adds the seams that Task 2's failing test needs. The legacy type name stays the **only** candidate, so the bug is still present when this task ends.

**Files:**
- Modify: `DifficultyPatch.cs` (whole file)
- Modify: `Sts2Unlimited.cs:300-301`
- Modify: `tests/PatchTests/PatchTests.csproj`
- Modify: `tests/PatchTests/Program.cs:37-43` (`Main`)

**Interfaces:**
- Produces (all on `Sts2Unlimited.DifficultyPatch`):
  - `internal static Action<LogLevel, string> LogSink`
  - `internal static Func<int, int> PlayerCountResolver` (default: identity)
  - `public static int EffectivePlayerCount(int actualCount)`: the method the transpiler inlines into game IL
  - `internal const string ScalingModelTypeName`
  - `internal const string LegacyScalingModelTypeName`
  - `internal static Type ResolveFirstType(params string[] fullNames)`: each name has no `, sts2` suffix
  - `internal static MethodInfo GetModifyBlockMethod()`
  - `internal static int LastTranspilePatchCount`: set by every `Transpile_PlayerCount` run
  - `internal static readonly string[] PowerScalingOverrideTypes` (was `private`)

- [ ] **Step 1: Add the seams to `DifficultyPatch.cs`**

Replace the top of the class, everything from `public static class DifficultyPatch {` through the end of `Transpile_PlayerCount`, with:

```csharp
public static class DifficultyPatch
{
    // The game's logger segfaults outside a game process; the harness swaps this out.
    internal static Action<LogLevel, string> LogSink =
        (level, message) => Log.LogMessage(level, LogType.Generic, message);

    // Sts2Unlimited.cs is not compiled into the harness, so the transpiled call goes through
    // a delegate that ModLoaded wires to Sts2Unlimited.GetEffectivePlayerCount.
    internal static Func<int, int> PlayerCountResolver = actual => actual;

    public static int EffectivePlayerCount(int actualCount) => PlayerCountResolver(actualCount);

    internal const string ScalingModelTypeName =
        "MegaCrit.Sts2.Core.Models.Singleton.MultiplayerScalingModel";
    internal const string LegacyScalingModelTypeName =
        "MegaCrit.Sts2.Core.Models.Singleton.MultiplayerScalingModel";

    internal static Type ResolveFirstType(params string[] fullNames)
    {
        foreach (var name in fullNames)
        {
            var type = Type.GetType($"{name}, sts2", false);
            if (type != null) return type;
        }
        return null;
    }

    internal static MethodInfo GetModifyBlockMethod()
        => ResolveFirstType(ScalingModelTypeName, LegacyScalingModelTypeName)
            ?.GetMethod("ModifyBlockMultiplicative", BindingFlags.Public | BindingFlags.Instance);

    internal static int LastTranspilePatchCount;

    private static readonly MethodInfo _getEffectivePlayerCount =
        typeof(DifficultyPatch).GetMethod(nameof(EffectivePlayerCount),
            BindingFlags.Public | BindingFlags.Static)!;

    private static bool IsPlayersCountCall(CodeInstruction ci)
    {
        if (ci.opcode != OpCodes.Callvirt) return false;
        if (ci.operand is not MethodInfo mi) return false;
        if (mi.Name != "get_Count") return false;
        var dt = mi.DeclaringType;
        if (dt == null || !dt.IsGenericType) return false;
        return dt.GetGenericArguments()[0].Name == "Player";
    }

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

        LastTranspilePatchCount = patched;

        if (patched == 0)
            LogSink(LogLevel.Warn,
                $"[DifficultyPatch] No Players.Count call found in {original?.DeclaringType?.Name}.{original?.Name} — patch may be broken.");
        else
            LogSink(LogLevel.Debug,
                $"[DifficultyPatch] Patched {patched} Players.Count call(s) in {original?.DeclaringType?.Name}.{original?.Name}.");

        return codes;
    }
```

Note: both constants deliberately hold the **legacy** name in this task. Task 2's test must fail first, and Task 3 corrects `ScalingModelTypeName`.

In `Apply`, replace the `Type.GetType(...)` lookup and the `if (modifyBlock != null)` block with:

```csharp
        var modifyBlock = GetModifyBlockMethod();
        if (modifyBlock != null)
        {
            harmony.Patch(modifyBlock, transpiler: transpiler);
            LogSink(LogLevel.Info,
                "[DifficultyPatch] Patched MultiplayerScalingModel.ModifyBlockMultiplicative (block scaling).");
        }
        else
            LogSink(LogLevel.Warn, "[DifficultyPatch] MultiplayerScalingModel type not found.");
```

In the rest of the file, change every `Log.LogMessage(X, LogType.Generic, msg)` to `LogSink(X, msg)`. Change `PowerScalingOverrideTypes` from `private` to `internal`. In `Prefix_ScaleHpForMultiplayer`, replace the body with:

```csharp
        int actual = playerCount;
        playerCount = EffectivePlayerCount(playerCount);
        LogSink(LogLevel.Debug,
            $"[DifficultyPatch] ScaleHpForMultiplayer: actual={actual} -> effective={playerCount}");
```

(The settings dump is dropped from this debug line because it read `Sts2Unlimited` fields directly. `DifficultySync` already logs the resolved inputs when it latches.)

- [ ] **Step 2: Wire the resolver in `Sts2Unlimited.cs`**

```csharp
			// Patch difficulty scaling to support player-count override
			DifficultyPatch.PlayerCountResolver = GetEffectivePlayerCount;
			DifficultyPatch.Apply(harmony);
```

- [ ] **Step 3: Compile `DifficultyPatch.cs` into the harness**

In `tests/PatchTests/PatchTests.csproj`, add this line after the `ChestPatch.cs` line:

```xml
    <Compile Include="../../DifficultyPatch.cs" Link="DifficultyPatch.cs" />
```

In `Program.cs` `Main()`, add this line after the `PatchRegistry.LogSink` line:

```csharp
        DifficultyPatch.LogSink = (level, message) => _log.Add($"{level}: {message}");
```

- [ ] **Step 4: Build both projects and run the harness**

Run: `dotnet build sts2unlimited.csproj && dotnet run --project tests/PatchTests`
Expected: the build succeeds and the harness prints `ALL TESTS PASSED`. No existing test changes.

- [ ] **Step 5: Commit**

```bash
git add DifficultyPatch.cs Sts2Unlimited.cs tests/PatchTests/PatchTests.csproj tests/PatchTests/Program.cs
git commit -m "refactor: give DifficultyPatch harness seams (log sink, player-count resolver)"
```

---

### Task 2: Failing tests that reproduce the report

**Files:**
- Modify: `tests/PatchTests/Program.cs`: add 5 test methods and call them from `Main()` after `ChestPatch*` / before `PatchRegistryNamesBrokenPatches()`

**Interfaces:**
- Consumes: everything Task 1 produced, plus the existing `RepoRoot()`, `Check`, `AreEqual` and `_log` in `Program.cs`.

- [ ] **Step 1: Add the calls to `Main()`**

```csharp
        DifficultyPatchTargetsResolve();
        DifficultyTranspilerRewritesRealBlockIl();
        DifficultyTypeFallbackOrder();
        DifficultyHpPrefixBindsByName();
        GameTypeNameLiteralsResolve();
```

- [ ] **Step 2: Add the tests**

```csharp
    // v0.113 moved MultiplayerScalingModel out of Models.Singleton, and the block hook died
    // behind a single Warn line. Every DifficultyPatch target is asserted here instead.
    private static void DifficultyPatchTargetsResolve()
    {
        Check("MultiplayerScalingModel resolves under its current name",
            DifficultyPatch.ResolveFirstType(DifficultyPatch.ScalingModelTypeName) != null,
            DifficultyPatch.ScalingModelTypeName);
        Check("MultiplayerScalingModel.ModifyBlockMultiplicative resolves",
            DifficultyPatch.GetModifyBlockMethod() != null);

        foreach (var name in DifficultyPatch.PowerScalingOverrideTypes)
            Check($"{name} resolves", DifficultyPatch.ResolveFirstType(name) != null);
    }

    // The real transpiler over the real IL: exactly one Players.Count site, and the
    // override call has to consume that Count directly.
    private static void DifficultyTranspilerRewritesRealBlockIl()
    {
        var method = DifficultyPatch.GetModifyBlockMethod();
        if (method == null) { Check("ModifyBlockMultiplicative IL available", false); return; }

        var before = PatchProcessor.GetOriginalInstructions(method);
        var after = DifficultyPatch.Transpile_PlayerCount(before, method).ToList();

        AreEqual("block transpiler rewrites 1 Players.Count site", 1, DifficultyPatch.LastTranspilePatchCount);
        AreEqual("block transpiler inserts exactly 1 instruction", before.Count + 1, after.Count);

        int at = after.FindIndex(c => c.opcode == OpCodes.Call
            && c.operand is MethodInfo mi && mi.Name == nameof(DifficultyPatch.EffectivePlayerCount));
        Check("EffectivePlayerCount follows a Players.Count call",
            at > 0 && after[at - 1].opcode == OpCodes.Callvirt
                   && after[at - 1].operand is MethodInfo c && c.Name == "get_Count");
    }

    // Current name first, legacy second. A pre-v0.113 install must still work, and the
    // fallback must not shadow the current name.
    private static void DifficultyTypeFallbackOrder()
    {
        var current = DifficultyPatch.ResolveFirstType(DifficultyPatch.ScalingModelTypeName);
        Check("legacy name is not what this sts2.dll uses",
            DifficultyPatch.ResolveFirstType(DifficultyPatch.LegacyScalingModelTypeName) == null);
        Check("an unresolvable first candidate falls through to the next",
            current != null && DifficultyPatch.ResolveFirstType(
                "MegaCrit.Sts2.Core.NoSuchType", DifficultyPatch.ScalingModelTypeName) == current);
        Check("no resolvable candidate yields null",
            DifficultyPatch.ResolveFirstType("MegaCrit.Sts2.Core.NoSuchType") == null);
    }

    // Harmony binds the HP prefix's "ref int playerCount" by parameter *name*. A rename
    // upstream would make Patch() throw at load and skip every patch after it.
    private static void DifficultyHpPrefixBindsByName()
    {
        foreach (var name in new[] { "ScaleHpForMultiplayer", "ScaleMonsterHpForMultiplayer" })
        {
            var m = AccessTools.Method(typeof(Creature), name);
            Check($"Creature.{name} resolves", m != null);
            if (m != null)
                Check($"Creature.{name} still has an int playerCount",
                    m.GetParameters().Any(p => p.Name == "playerCount" && p.ParameterType == typeof(int)));
        }
    }

    // The class of bug, not just this instance: every game type the mod names in a string
    // literal must exist in this sts2.dll. Legacy fallbacks are the only allowed exception.
    private static void GameTypeNameLiteralsResolve()
    {
        var root = RepoRoot();
        if (root == null) { Check("repository root is locatable", false); return; }

        var legacy = new HashSet<string> { DifficultyPatch.LegacyScalingModelTypeName };
        var literal = new Regex(@"""(MegaCrit\.Sts2\.[A-Za-z0-9_.]+?)(?:, sts2)?""");
        int checkedCount = 0;

        foreach (var file in Directory.GetFiles(root, "*.cs"))
        foreach (Match m in literal.Matches(File.ReadAllText(file)))
        {
            var name = m.Groups[1].Value;
            if (legacy.Contains(name)) continue;
            checkedCount++;
            Check($"{Path.GetFileName(file)}: {name} resolves",
                DifficultyPatch.ResolveFirstType(name) != null);
        }
        Check("type-name literals were found to check", checkedCount > 0);
    }
```

Note: `Directory.GetFiles(root, "*.cs")` only searches the top level, which is where the mod's sources live. That keeps the scan out of `tests/` and `obj/`. If you edit a file without saving, the scan reads the old text from disk.

- [ ] **Step 3: Run the harness and confirm it fails for the reported reason**

Run: `dotnet run --project tests/PatchTests`
Expected FAILs (the exit code is non-zero):
- `MultiplayerScalingModel resolves under its current name`
- `MultiplayerScalingModel.ModifyBlockMultiplicative resolves`
- `ModifyBlockMultiplicative IL available`
- `legacy name is not what this sts2.dll uses`
- `an unresolvable first candidate falls through to the next`

The literal scan stays green at this point because both constants hold the legacy name, which is allowlisted. Never widen that allowlist to make a failure go away. Fix the name instead.

Everything else should pass. If any other check fails, stop and investigate before Task 3. The likely cause is a test that is wrong, not the mod.

- [ ] **Step 4: Commit**

```bash
git add tests/PatchTests/Program.cs
git commit -m "test: reproduce dead block-scaling hook on game v0.113 (failing)"
```

---

### Task 3: Fix the lookup and make a dead hook loud

**Files:**
- Modify: `DifficultyPatch.cs` (the two constants and `Apply`/`PatchHpScaling`)

**Interfaces:**
- Consumes: `PatchRegistry.Record(string name, bool installed, bool required = true, string detail = null)`

- [ ] **Step 1: Point the current-name constant at the v0.113 location**

```csharp
    // v0.113+
    internal const string ScalingModelTypeName =
        "MegaCrit.Sts2.Core.Models.MultiplayerScalingModel";
    // up to v0.112; kept so an older install still gets block scaling
    internal const string LegacyScalingModelTypeName =
        "MegaCrit.Sts2.Core.Models.Singleton.MultiplayerScalingModel";
```

- [ ] **Step 2: Run the harness**

Run: `dotnet run --project tests/PatchTests`
Expected: `ALL TESTS PASSED`.

- [ ] **Step 3: Record the difficulty hooks in `PatchRegistry`**

The mod should have reported "loaded DEGRADED" on v0.113.0, not "loaded successfully". In `Apply`, replace the block-scaling section from Task 1 with:

```csharp
        const string blockTarget = "MultiplayerScalingModel.ModifyBlockMultiplicative (block scaling)";
        var modifyBlock = GetModifyBlockMethod();
        if (modifyBlock == null)
            PatchRegistry.Record(blockTarget, false,
                detail: $"not found as {ScalingModelTypeName} or {LegacyScalingModelTypeName}");
        else
        {
            harmony.Patch(modifyBlock, transpiler: transpiler);
            PatchRegistry.Record(blockTarget, LastTranspilePatchCount > 0,
                detail: "no Players.Count call to rewrite");
        }
```

In `PatchHpScaling`, add a `PatchRegistry.Record("Creature.ScaleHpForMultiplayer (HP scaling)", ...)` next to each existing success or not-found log. Do the same for `"Creature.ScaleMonsterHpForMultiplayer (HP scaling)"`, and for a missing `Creature` type, record both as failed with `detail: "Creature type not found"`. Keep the existing Info/Warn log lines. The registry logs at Debug on success and Error on failure, which complements them. Leave power scaling unregistered: it has no live caller, and its log line already says so.

- [ ] **Step 4: Build, then run the harness again**

Run: `dotnet build sts2unlimited.csproj && dotnet run --project tests/PatchTests`
Expected: the build succeeds and the harness prints `ALL TESTS PASSED`. `PatchRegistryNamesBrokenPatches` resets the registry itself, so the new records don't disturb it.

- [ ] **Step 5: Commit**

```bash
git add DifficultyPatch.cs
git commit -m "fix: find MultiplayerScalingModel at its v0.113 namespace, and register difficulty hooks"
```

---

### Task 4: Testkit driver: a `moveblock` command (sts2-testkit repo)

**Why a new command is needed.** The game's own `block` console command calls `CreatureCmd.GainBlock(target, new BlockVar(amount, ValueProp.Unpowered), null)`, and `ModifyBlockMultiplicative` returns `1m` unless `props.IsPoweredCardOrMonsterMoveBlock()`, meaning `Move` is set and `Unpowered` is not. So `block 10 <enemy>` gives 10 block whatever the difficulty setting, and can never show this bug. Waiting for a real enemy to block on its own turn would depend on the encounter's move order. A driver command that gains block with `ValueProp.Move` goes through the same `Hook.ModifyBlock` pipeline a monster's move does, and the numbers it produces are deterministic.

The command is mod-agnostic: it is "block as a monster move would gain it", with nothing about Sts2Unlimited in it. That keeps it within the kit's framework-only rule.

**Files (all under `/mnt/c/Users/anjivo/sts2-testkit`):**
- Create: `driver/Sts2TestDriver.Mod/MoveBlockConsoleCmd.cs`
- Modify: `driver/Sts2TestDriver.Mod/Sts2TestDriver.json` (`"version": "v0.4.0"` → `"v0.5.0"`)
- Modify: `README.md`: add `moveblock` to the driver row in the components table (line ~69), and to the console-command list next to `openchest`/`pickrelic`

**Interfaces:**
- Produces: console command `moveblock <amount:int> [enemy-index:int=0]`, **networked** (it runs on every peer, as the game's `block` does). On each peer it logs exactly:
  `[TestDriver] moveblock: <creature> requested=<amount> gained=<int> block=<int>`

- [ ] **Step 1: Write the command**

```csharp
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.ValueProps;

namespace Sts2TestDriver;

/// The base game's "block" command gains block as ValueProp.Unpowered, which every
/// block modifier that only scales "real" block (a powered card or a monster move)
/// ignores by design. This gains it as a monster move would, through the same
/// Hook.ModifyBlock pipeline, and logs what came out the other end so scenarios
/// can assert on the number.
public sealed class MoveBlockConsoleCmd : AbstractConsoleCmd
{
    public override string CmdName => "moveblock";

    public override string Args => "<int:amount> [enemy-index:int]";

    public override string Description =>
        "Gives an enemy block as a monster move would (ValueProp.Move), so block "
        + "modifiers apply, and logs the amount actually gained. Networked.";

    // Mirrors the game's own "block": a combat-state change has to happen on every
    // peer, or this command would be the divergence a scenario then reports.
    public override bool IsNetworked => true;

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        if (issuingPlayer == null)
            return new CmdResult(success: false, "moveblock: a run is not in progress");
        if (!CombatManager.Instance.IsInProgress)
            return new CmdResult(success: false, "moveblock: no combat in progress");
        if (args.Length is < 1 or > 2 || !int.TryParse(args[0], out var amount) || amount < 0)
            return new CmdResult(success: false, "moveblock: usage " + Args);

        int index = 0;
        if (args.Length == 2 && !int.TryParse(args[1], out index))
            return new CmdResult(success: false, "moveblock: enemy index must be an int");

        var enemies = issuingPlayer.Creature.CombatState.Enemies;
        if (index < 0 || index >= enemies.Count)
            return new CmdResult(success: false,
                $"moveblock: enemy index {index} out of range 0-{enemies.Count - 1}");

        var target = enemies[index];
        return new CmdResult(Gain(target, amount), success: true,
            $"moveblock: requested {amount} on {target}");
    }

    private static async Task Gain(Creature target, int amount)
    {
        decimal gained = await CreatureCmd.GainBlock(target, amount, ValueProp.Move, null);
        Log.LogMessage(LogLevel.Info, LogType.Generic,
            $"[TestDriver] moveblock: {target} requested={amount} gained={(int)gained} block={target.Block}");
    }
}
```

Check two names against the live `sts2.dll` with `ilspycmd` before building: `Creature.Block` (the property that holds current block), and the `CreatureCmd.GainBlock(Creature, decimal, ValueProp, CardPlay?, bool)` overload. That overload was verified on 2026-11-19, but `Block` was not.

- [ ] **Step 2: Build, deploy and smoke-test the driver** by the kit's README Quick start, against a lobby with **no** difficulty-scaling mod. Run `fight SLIMES_WEAK`, `instant`, then `moveblock 10` from the host.
Expected: every peer logs `moveblock: ... requested=10 gained=20`. With 2 players in vanilla, `ModifyBlockMultiplicative` returns `count` (2) for counts ≤ 2. That number is the reporter's "20", which shows the command really goes through the scaling hook.
If only the host logs it, the mod command is not being replayed on the peers. In that case, fall back to `IsNetworked => false` and have scenarios send it to every peer, and record the reason in the class comment.

- [ ] **Step 3: Run the kit's own suite:** `./.venv/Scripts/python.exe -m pytest` (not `-m live`). Expected: green, including `test_repo_is_portable.py`.

- [ ] **Step 4: Commit on the kit's `main`. Do not push**, since the kit has no remote and publishing is the user's call.

```bash
git add driver/Sts2TestDriver.Mod/MoveBlockConsoleCmd.cs driver/Sts2TestDriver.Mod/Sts2TestDriver.json README.md
git commit -m "feat(driver): moveblock, block gained as a monster move so modifiers apply"
```

---

### Task 5: Live scenario for block scaling, and a profile that stops tolerating dead hooks (branch `testkit-scenarios`)

**Why the existing live suite missed this.** The profile's `failure_patterns` catch `[PatchRegistry] FAILED` and `loaded DEGRADED`, but `DifficultyPatch` never reported to the registry. Its `[WARN] ... MultiplayerScalingModel type not found` is not a pattern, and the core scan ignores Warn on purpose. `test_difficulty_sync.py` only asserts the HP line (`actual=2 -> effective=9`), and HP scaling never broke. So every scenario stayed green on a build where block scaling was dead. Task 3's registry records close the install-time half of this. This task closes the rest.

**Files (repo `sts-2-unlimited`, branch `testkit-scenarios`; switch with `git switch testkit-scenarios` and rebase it onto `main` first so it contains Tasks 1-3):**
- Modify: `tests/testkit/sts2unlimited_profile.py`
- Create: `tests/testkit/test_difficulty_block.py`
- Modify: `tests/testkit/README.md`: list the new scenario and its driver ≥ v0.5.0 requirement

**Interfaces:**
- Consumes: driver `moveblock` (Task 4) and its log line.
- Produces: `Sts2UnlimitedProfile.move_block(requested: int, gained: int) -> str`

- [ ] **Step 1: Profile: a pattern builder and the missing failure lines**

Add this to `Sts2UnlimitedProfile`:

```python
    def move_block(self, requested: int, gained: int) -> str:
        """The driver's moveblock outcome, narrowed to one expected number.

        Anchored on "gained=" + a word boundary so 99 cannot match 990.
        """
        return rf"\[TestDriver\] moveblock: .* requested={requested} gained={gained}\b"
```

Add this to `failure_patterns`, with a comment in the existing style. The comment should say these are Warn-level lines that mean a scaling hook installed nothing, and that the v0.113 block bug passed the whole suite through exactly this gap:

```python
        # DifficultyPatch: a target that did not resolve, or a transpiler that found
        # no Players.Count to rewrite. Each leaves that scaling path vanilla while
        # the mod reports itself loaded -- the v0.113 block-scaling regression passed
        # every scenario through exactly this gap.
        r"\[DifficultyPatch\] .*not found",
        r"\[DifficultyPatch\] No Players\.Count call found",
```

Check both against `DifficultyPatch.cs` character for character, as the profile's comments require. After Task 3 the `not found` texts are: `Creature type not found.`, `Creature.ScaleHpForMultiplayer not found.`, `Creature.ScaleMonsterHpForMultiplayer not found.` and `{typeName} not found — skipping power scaling patch.`. The last one is the power path, which has no live caller; it is still a rename upstream, so it fails too.

- [ ] **Step 2: Write the scenario**

```python
"""The reporter's case: Difficulty Scaling must scale enemy block, not just HP.

Game v0.113 moved MultiplayerScalingModel out of Models.Singleton. DifficultyPatch
looked it up by the old name, found nothing, warned once, and left block scaling
vanilla -- while HP (a different hook) kept working, so test_difficulty_sync.py
stayed green. These scenarios assert on block itself.

The numbers are exact, not "bigger than before". ModifyBlockMultiplicative returns
the raw player count when it is <= 2, and count * GetMultiplayerScaling otherwise;
in act 0 that factor is 1.1. So 10 block is 10 * 2 = 20 unpatched (the reporter's
"20") and 10 * 9 * 1.1 = 99 with the override at 9 (the reporter's "99").

The base game's "block" command cannot test this: it gains block as
ValueProp.Unpowered, which this hook skips by design. The driver's "moveblock"
gains it as a monster move would.

Two peers, as in test_difficulty_sync.py: a hosted run with one player never begins.
The client's local value differs from the host's, so a pass also shows the client
scaled block by the HOST's value -- the host-authoritative rule from #24 -- not its own.
"""

import pytest

pytestmark = pytest.mark.live


def _fight(lb):
    lb.begin_run_everywhere()
    for instance in lb.all:
        instance.cmd("instant")
    lb.host.cmd("fight SLIMES_WEAK")
    # The HP line proves the combat exists on every peer before block is touched.
    lb.expect_all(lb.profile.effective(2, lb.expected_effective), timeout=120)


def test_override_scales_monster_block_on_every_peer(lobby):
    with lobby(
        clients=1,
        profile="sts2unlimited",
        settings=[{"DifficultyPlayers": 9}, {"DifficultyPlayers": 4}],
    ) as lb:
        lb.expected_effective = 9
        _fight(lb)
        lb.host.cmd("moveblock 10")
        lb.expect_all(lb.profile.move_block(10, 99), timeout=60)
        lb.host.cmd("kill all")


def test_override_off_leaves_monster_block_vanilla(lobby):
    with lobby(
        clients=1,
        profile="sts2unlimited",
        settings=[
            {"DifficultyEnabled": False, "DifficultyPlayers": 9},
            {"DifficultyEnabled": True, "DifficultyPlayers": 4},
        ],
    ) as lb:
        lb.expected_effective = 2
        _fight(lb)
        lb.host.cmd("moveblock 10")
        lb.expect_all(lb.profile.move_block(10, 20), timeout=60)
        lb.host.cmd("kill all")
```

(If `Lobby` refuses new attributes, i.e. it uses `__slots__`, pass `expected_effective` to `_fight` as a parameter instead.)

- [ ] **Step 3: Prove the scenario catches the bug.** This is the acceptance criterion, as it was for the kit's v1.0.1 difficulty proof.
Deploy the mod as it is on `main` **before** Task 3 (v2.0.0, i.e. `git stash` / check out `60130d9`, build, deploy) and run:
`./.venv/Scripts/python.exe -m pytest -m live <path>/tests/testkit/test_difficulty_block.py`
Expected: `test_override_scales_monster_block_on_every_peer` **FAILS**, because the log shows `gained=20`, not 99. Also expected: **both** tests fail on the new `\[DifficultyPatch\] .*not found` failure pattern. Save the failing run's artifact path in the commit message.

- [ ] **Step 4: Deploy the fixed build (Tasks 1-3) and re-run the scenario file, then the whole live suite**
Expected: both new tests pass, and every existing scenario still passes. That shows the stricter profile does not flag ordinary output. If a new failure pattern fires during a passing run, look at the line before you loosen anything. It is either a real regression or a pattern that is too broad.

- [ ] **Step 5: Commit on `testkit-scenarios`. Do not push.**

```bash
git add tests/testkit/sts2unlimited_profile.py tests/testkit/test_difficulty_block.py tests/testkit/README.md
git commit -m "test(testkit): live block-scaling scenario, and fail on DifficultyPatch's silent warnings"
```

---

### Task 6: Release check and reply

- [ ] **Step 1: With the fixed build deployed, launch once by hand with `-log Generic Debug` and read `godot.log`.** Expected:
  - `[DifficultyPatch] Patched MultiplayerScalingModel.ModifyBlockMultiplicative (block scaling).`
  - `[DifficultyPatch] Patched 1 Players.Count call(s) in MultiplayerScalingModel.ModifyBlockMultiplicative.`
  - `Sts2Unlimited loaded successfully! ... (all N patches applied)`
  - no `MultiplayerScalingModel type not found` line

- [ ] **Step 2: Remove the test deploy from `<game>/mods/`** (a stale local copy silently shadows the Workshop one).
- [ ] **Step 3: Reply on the bug report** with the fix version, and thank the reporter for the diagnosis and the patch.

---

## Self-Review notes

- The spec asks for the block hook to work on v0.113.x (Task 3, Step 1) and for both namespaces to be tried, current first (`ResolveFirstType` order, tested by `DifficultyTypeFallbackOrder`).
- "Tests catch this in the future" has three layers. The type and method checks catch this exact regression. The real-IL transpile catches a reshaped method. The literal scan catches the same kind of rename in any other file (`SettingsMenuIntegration.cs`'s `NSettingsScreen`, `NMasterVolumeSlider`, `StsColors`, `RunManager`, and the tickbox type). `PatchRegistry` then makes the remaining runtime failures loud.
- The headless harness cannot invoke `ModifyBlockMultiplicative` end to end, because it needs a live `RunState`/`CombatState`. Tasks 4-5 close that gap automatically: the testkit drives a real 2-peer combat and asserts the reporter's exact numbers (99 / 20). Task 5 Step 3 also has to show the scenario fails on the unfixed build.
- Ordering: Tasks 1-3 (mod, `main`) and Task 4 (kit repo) are independent and can run in parallel. Task 5 needs both of them.
- Unverified until Task 4 Step 2 runs: whether the console replays a **mod-added** networked command on the client peers. The game registers mod command types on every peer, so it should, and a fallback is written in.

## Execution notes (2026-11-19)

How the run went, and where it differed from the plan:

- **Task 2:** `legacy name is not what this sts2.dll uses` passed before the fix as well as after. That is correct: it asserts the legacy name does *not* resolve. The other four predicted failures failed as expected.
- **Task 3 + review:** two changes came out of the independent review. The block hook's failure detail now tells a missing type apart from a missing method. The outer `catch` in `ApplyHarmonyPatches` also records a registry failure, so a throwing `harmony.Patch` loads the mod DEGRADED instead of reporting success.
- **Task 4:** a networked console command runs from the action queue, and the game drops a failed `CmdResult` silently. `moveblock` therefore logs its refusals at Warn. The first live runs showed why: sent too early, it refused on both peers and left no trace.
- **Task 5:**
  - The scenario waits for `Combat state becomes PlayPhase` before it sends `moveblock`, because the HP line fires during pre-combat setup.
  - On v0.113.1, `fight SLIMES_WEAK` rolls its slime lineup separately on each peer, which caused a StateDivergence at the first checksum. Every multi-peer scenario now uses `SHRINKER_BEETLE_WEAK`, a single fixed monster.
  - Proof: on the unfixed v2.0.0 build, both peers logged `gained=20` and both scenarios failed. On the fix, both peers logged `gained=99` and `gained=20`, and both scenarios passed.
  - Full live suite on the fix: 8 passed, 3 failed. On re-run, `test_smoke` and `test_dread_treasure` passed, so those were flaky. `test_chest_overflow` failed twice: ENet handshake acks were sent by the host but never reached 5–6 of the clients, which looks like a 7-instance load problem. None of these fail on a pattern this plan added.
