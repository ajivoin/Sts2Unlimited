# Host-authoritative Difficulty Scaling — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the host's Difficulty Scaling value apply to every player in a multiplayer run, so peers with different local values no longer compute different monster HP and get disconnected with a State Divergence.

**Architecture:** A mod-defined `DifficultySyncMessage` (an `INetMessage`, which the game's id registry picks up automatically from mod assemblies) carries the host's resolved player count. The host broadcasts it immediately before the vanilla begin-run message and latches its own value at the same moment; every peer receives it through one prefix on `NetMessageBus.SendMessageToAllHandlers` and latches it. `Sts2Unlimited.GetEffectivePlayerCount` becomes the single decision point: in a multiplayer run it uses the latched host value, and with no latched value it falls back to the real player count and warns.

**Tech Stack:** C# / .NET 9, Godot.NET.Sdk 4.5.1, Lib.Harmony 2.4.2, `sts2.dll` reference assembly.

**Spec:** `docs/superpowers/specs/2026-11-04-difficulty-scaling-host-authoritative.md` — read it first. It carries the reporter's log, the numeric reproduction of the divergence, and findings F1–F7 verified against the current `sts2.dll` (every signature this plan uses comes from there).

## Global Constraints

- **The game's logger segfaults headless.** `Log.LogMessage` calls into Godot natives; outside a game process it kills the harness with SIGSEGV (exit 139) and no exception. All logging in new code goes through a `LogSink` indirection exactly as `ListHeaderWidthPatch.cs:38` does, and the harness swaps it for a capturing sink. The harness must never call an `Apply` method that logs directly.
- **`LogLevel` values are `VeryDebug=0, Load=1, Debug=2, Info=3, Warn=4, Error=5`** (verified). The vanilla lobby messages return `LogLevel = 0`, i.e. `LogLevel.VeryDebug`.
- **Patch sites follow the existing house style**: resolve the target, null-check, log a `Warn` and skip when it is missing, never throw out of `ApplyHarmonyPatches`. See `DifficultyPatch.Apply` for the pattern.
- **Wire payload is fixed-width and version-tagged**: `sentinel` as 32 bits, `effectiveCount` as 8 bits. Never widen or reorder these without changing the sentinel.
- **Messages are structs.** Every vanilla `INetMessage` is a sealed value type, and the bus does `Activator.CreateInstance(type)` then calls `Deserialize` through `IPacketSerializable` on the resulting box — so the box the handlers receive is the one `Deserialize` mutated. `DifficultySyncMessage` is a `struct` for the same reason. Read it in handlers; never try to mutate it through the interface.
- **The message id on the wire is a single byte** (`ReadByte(8)`), so there is a hard ceiling of 256 message types across the game and all loaded mods.
- **Harmony targets here are mostly non-public.** `BeginRunForAllPlayers`, `BeginRunLocally`, `TryBeginRunForAllPlayers` and `HandleLobbyBeginRunMessage` are all private; `AccessTools.Method` finds them regardless. Do not "fix" a working lookup by adding `BindingFlags`.
- **Never name a member `LogLevel` and then use the enum unqualified.** A property called `LogLevel` shadows the type of the same name, so inside such a type `LogLevel.VeryDebug` does not compile — write `MegaCrit.Sts2.Core.Logging.LogLevel.VeryDebug`.
- **`0` is the "host has no override" value** for `effectiveCount`, everywhere, with no second boolean.
- Branch: `fix/difficulty-sync-host-authoritative` (already created). Conventional commits — CI derives the version bump from them, so do **not** hand-edit `Sts2Unlimited.json`.
- Mod build check: `dotnet build --configuration ExportRelease` from the repo root.
- Test command: `dotnet run --project tests/PatchTests`. The baseline is green at **58 assertions** before this work starts; it must stay green and only grow.
- `dotnet build --configuration ExportRelease` needs a `sts2unlimited.maxplayers.txt` at the repo root (the `BundleRelease` target copies it). It is gitignored and absent from a fresh worktree — create a local placeholder if the build complains, and never commit it: it leaks local values into releases.
- `sts2.dll`, `Sentry.dll` and `Sentry.Godot.dll` are gitignored at the repo root and are already present in this worktree. `sts2.dll` is 9757184 bytes, md5 `a91f9e7be785f504205313f3aff55854`.
- **Environment note:** in this worktree a user hook rewrites `git add`/`git commit` to `rtk git …`, which the worktree-isolation guard then refuses. `git stage -A` passes through. If a commit step is refused, stage the files and ask the user to run the commit with `! git commit …`.

## File Structure

| File | Responsibility |
|---|---|
| `DifficultySyncMessage.cs` (create) | The wire contract. `INetMessage` + `IPacketSerializable`, two int fields, sentinel validation. Nothing else — no logging, no patching, so the harness can construct it freely. |
| `DifficultySync.cs` (create) | The latch, the resolve rule, the warning, and all four Harmony seams (send ×3, receive ×1) plus teardown. Owns a `LogSink`. |
| `Sts2Unlimited.cs` (modify) | `GetEffectivePlayerCount` delegates the multiplayer branch to `DifficultySync`; `ApplyHarmonyPatches` calls `DifficultySync.Apply(harmony)`. |
| `SettingsMenuIntegration.cs` (modify) | Difficulty rows show the host's value and lock when a host value is latched and we are not the host. |
| `tests/PatchTests/PatchTests.csproj` (modify) | Compile `DifficultySyncMessage.cs` and `DifficultySync.cs` into the harness. |
| `tests/PatchTests/Program.cs` (modify) | New tests: message round trip, sentinel rejection, the decision table, the HP-formula characterization, and patch-target resolution. |
| `README.md`, `MOD_README.md`, `workshop/workshop.json` (modify) | Document that the host controls difficulty scaling in multiplayer. |

---

### Task 1: The wire contract

Build the message and prove it round-trips through the game's real `PacketWriter`/`PacketReader`, including that a corrupted sentinel is detected. This task has no Harmony and no logging, so it is fully testable headlessly.

**Files:**
- Create: `DifficultySyncMessage.cs`
- Modify: `tests/PatchTests/PatchTests.csproj`
- Test: `tests/PatchTests/Program.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `public struct DifficultySyncMessage : INetMessage, IPacketSerializable` with `public int Sentinel { get; private set; }`, `public int EffectiveCount { get; private set; }`, `public bool IsValid`, `public const int SentinelV1 = 0x44494631`, `public const int NoOverride = 0`, and a `DifficultySyncMessage(int effectiveCount)` constructor. Being a struct it already has an implicit parameterless constructor, which is what `Activator.CreateInstance` uses on receipt — and it leaves `Sentinel` at `0`, so an un-deserialized or foreign instance is invalid by default.

- [ ] **Step 1: Add the two new sources to the harness project**

In `tests/PatchTests/PatchTests.csproj`, inside the existing `<ItemGroup>` that holds the `<Compile Include="../../…">` entries, add:

```xml
    <Compile Include="../../DifficultySyncMessage.cs" Link="DifficultySyncMessage.cs" />
    <Compile Include="../../DifficultySync.cs" Link="DifficultySync.cs" />
```

`DifficultySync.cs` does not exist until Task 2. Create it now as a placeholder holding only `namespace Sts2Unlimited; public static class DifficultySync { }` so the harness compiles; Task 2 fills it in. (The harness compiles these sources directly rather than referencing the mod assembly, which is how it reaches `internal` members like `LogSink` — same arrangement as `ListHeaderWidthPatch`.)

- [ ] **Step 2: Write the failing test**

Add to `tests/PatchTests/Program.cs`, and call `DifficultySyncMessageRoundTrips();` from `Main` after the existing calls:

```csharp
    // The host's value has to survive the game's own bit-packing unchanged, and a packet that
    // did not come from this mod has to be recognisable as foreign. Mod-added message ids are
    // positional, so a peer running a different set of message-adding mods can route someone
    // else's message in here; the sentinel is the only thing standing between that and a
    // silently applied bogus player count.
    private static void DifficultySyncMessageRoundTrips()
    {
        foreach (int count in new[] { DifficultySyncMessage.NoOverride, 1, 4, 16, 255 })
        {
            var writer = new PacketWriter();
            writer.Reset();
            new DifficultySyncMessage(count).Serialize(writer);

            var reader = new PacketReader();
            reader.Reset(writer.Buffer);
            var received = new DifficultySyncMessage();
            received.Deserialize(reader);

            Check($"round trip {count} is valid", received.IsValid);
            AreEqual($"round trip {count} preserves the count", count, received.EffectiveCount);
            AreEqual($"round trip {count} costs 40 bits", 40, writer.BitPosition);
        }

        // A foreign payload: right length, wrong sentinel.
        var foreign = new PacketWriter();
        foreign.Reset();
        foreign.WriteInt(0x0BADF00D, 32);
        foreign.WriteInt(7, 8);

        var foreignReader = new PacketReader();
        foreignReader.Reset(foreign.Buffer);
        var rejected = new DifficultySyncMessage();
        rejected.Deserialize(foreignReader);

        Check("a foreign sentinel is not valid", !rejected.IsValid);
    }
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet run --project tests/PatchTests`
Expected: compile error — `DifficultySyncMessage` does not exist.

- [ ] **Step 4: Write the message**

Create `DifficultySyncMessage.cs`:

```csharp
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

namespace Sts2Unlimited;

/// <summary>
/// Carries the host's difficulty-scaling player count to every peer at run start.
///
/// Monster HP and block are derived state: the game never replicates them, it relies on every
/// peer computing the same number from the same replicated player list and gates that with
/// ChecksumTracker. The difficulty override feeds that math, so unless every peer uses the same
/// value the run dies with a State Divergence at the first fight.
///
/// The game's message-id registry picks this type up on its own — MessageTypes.Initialize()
/// folds in ReflectionHelper.GetSubtypesInMods() — but those ids are *positional*, so a peer
/// running a different set of message-adding mods can route a foreign message into our handler.
/// Hence the sentinel: a payload that does not start with it is dropped, never applied.
/// </summary>
/// <remarks>
/// A struct, like every vanilla message: the bus instantiates an inbound message with
/// Activator.CreateInstance and then calls Deserialize through IPacketSerializable, so the box
/// handed to the handlers is the one Deserialize wrote into. The implicit parameterless
/// constructor leaves Sentinel at 0, which is exactly the "not from us" state IsValid rejects.
/// </remarks>
public struct DifficultySyncMessage : INetMessage, IPacketSerializable
{
    /// <summary>ASCII "DIF1". Change this if the payload layout ever changes.</summary>
    public const int SentinelV1 = 0x44494631;

    /// <summary>The host has no override active, so peers scale by the real player count.</summary>
    public const int NoOverride = 0;

    private const int SentinelBits = 32;
    private const int CountBits    = 8;

    public int Sentinel       { get; private set; }
    public int EffectiveCount { get; private set; }

    public DifficultySyncMessage(int effectiveCount) : this()
    {
        Sentinel       = SentinelV1;
        EffectiveCount = effectiveCount;
    }

    public bool IsValid => Sentinel == SentinelV1;

    // Mirrors LobbySeedChangedMessage and LobbyBeginRunMessage.
    // LogLevel must be fully qualified: the property name shadows the enum type.
    public bool            ShouldBroadcast => true;
    public NetTransferMode Mode            => NetTransferMode.Reliable;
    public LogLevel        LogLevel        => MegaCrit.Sts2.Core.Logging.LogLevel.VeryDebug;
    public bool            ShouldBuffer    => true;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteInt(Sentinel, SentinelBits);
        writer.WriteInt(EffectiveCount, CountBits);
    }

    public void Deserialize(PacketReader reader)
    {
        Sentinel       = reader.ReadInt(SentinelBits);
        EffectiveCount = reader.ReadInt(CountBits);
    }
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet run --project tests/PatchTests`
Expected: the new assertions PASS and the existing 23 still pass.

If `LogLevel` or `NetTransferMode` fail to resolve, check the `using` lines against F5 in the spec — `NetTransferMode` lives in `MegaCrit.Sts2.Core.Multiplayer.Transport`, not in `…Serialization`.

- [ ] **Step 6: Build the mod**

Run: `dotnet build --configuration ExportRelease`
Expected: `0 Error(s)`.

- [ ] **Step 7: Commit**

```bash
git add DifficultySyncMessage.cs DifficultySync.cs tests/PatchTests/PatchTests.csproj tests/PatchTests/Program.cs
git commit -m "feat: add DifficultySyncMessage wire contract"
```

---

### Task 2: The latch and the decision rule

The pure logic: what count does a given peer scale by? This is where the bug is actually fixed. No networking yet — the latch is set directly by the tests.

**Files:**
- Modify: `DifficultySync.cs`
- Modify: `Sts2Unlimited.cs:35-40`
- Test: `tests/PatchTests/Program.cs`

**Interfaces:**
- Consumes: `DifficultySyncMessage.NoOverride` from Task 1.
- Produces, all on `DifficultySync`:
  - `internal static Action<LogLevel, string> LogSink`
  - `public static bool IsMultiplayerRun { get; }`
  - `public static void MarkMultiplayerRun()` — sets the flag only; **never** touches the latch
  - `public static void Latch(int effectiveCount, string source)`
  - `public static bool TryGetHostEffectiveCount(out int count)`
  - `public static int Resolve(int actualCount, bool overrideEnabled, int playersOverride, bool scaleSingleplayerEnabled)` — the whole decision as a pure function
  - `public static int ResolveLocalOverrideForBroadcast(bool overrideEnabled, int playersOverride)`
  - `internal static Func<bool> LocalOverrideEnabled` and `internal static Func<int> LocalPlayersOverride` — defaulted providers, wired to `Sts2Unlimited`'s settings at mod load in Task 3
  - `public static void Reset()`

**Why the providers exist.** The send seams in Tasks 4–5 need the local settings, but
`DifficultySync.cs` is compiled into the headless harness and `Sts2Unlimited.cs` is not — reading
`Sts2Unlimited.DifficultyOverrideEnabled` from here would force the harness to compile the
Godot-dependent half of the mod and break its build. Same indirection as `LogSink`, same reason.

**Why the rule is a pure function taking its inputs.** The obvious shape would be for
`DifficultySync` to read `Sts2Unlimited.DifficultyOverrideEnabled` itself. Don't: the harness
would then have to compile `Sts2Unlimited.cs`, which pulls in `SettingsMenuIntegration.cs` and
every other patch file and needs Godot node types to resolve. Passing the three settings in keeps
the decision testable on its own, and `Sts2Unlimited.GetEffectivePlayerCount` becomes a one-line
forwarder.

- [ ] **Step 1: Write the failing test**

Add to `tests/PatchTests/Program.cs` and call `DifficultyDecisionTable();` and `HpFormulaStillMatchesTheBugReport();` from `Main`:

```csharp
    // The whole fix in one table. Each row is a peer deciding what to scale by.
    private static void DifficultyDecisionTable()
    {
        var captured = new List<string>();
        DifficultySync.LogSink = (level, message) => captured.Add($"{level}: {message}");

        // Singleplayer is untouched by any of this.
        DifficultySync.Reset();
        AreEqual("solo, override on, sp-scaling off -> actual", 1,
            DifficultySync.Resolve(1, overrideEnabled: true, playersOverride: 9, scaleSingleplayerEnabled: false));
        AreEqual("solo, override on, sp-scaling on -> override", 9,
            DifficultySync.Resolve(1, overrideEnabled: true, playersOverride: 9, scaleSingleplayerEnabled: true));
        AreEqual("solo, override off -> actual", 1,
            DifficultySync.Resolve(1, overrideEnabled: false, playersOverride: 9, scaleSingleplayerEnabled: true));

        // Multiplayer with the host's value latched: the host's number wins, whatever ours says.
        DifficultySync.Reset();
        DifficultySync.MarkMultiplayerRun();
        DifficultySync.Latch(6, "test");
        AreEqual("mp, host says 6, we want 9 -> 6", 6,
            DifficultySync.Resolve(3, overrideEnabled: true, playersOverride: 9, scaleSingleplayerEnabled: false));

        // Host with no override: everyone scales by the real count, even peers who have one.
        DifficultySync.Reset();
        DifficultySync.MarkMultiplayerRun();
        DifficultySync.Latch(DifficultySyncMessage.NoOverride, "test");
        AreEqual("mp, host has no override -> actual", 3,
            DifficultySync.Resolve(3, overrideEnabled: true, playersOverride: 9, scaleSingleplayerEnabled: false));

        // Nothing latched (old-version host): fall back to the real count and say so once.
        DifficultySync.Reset();
        DifficultySync.MarkMultiplayerRun();
        captured.Clear();
        AreEqual("mp, unsynced -> actual", 3,
            DifficultySync.Resolve(3, overrideEnabled: true, playersOverride: 9, scaleSingleplayerEnabled: false));
        AreEqual("mp, unsynced -> actual again", 3,
            DifficultySync.Resolve(3, overrideEnabled: true, playersOverride: 9, scaleSingleplayerEnabled: false));
        AreEqual("unsynced warns exactly once", 1, captured.Count(l => l.StartsWith("Warn")));

        // ...but the next run must warn again, so Reset() has to clear the once-only flag.
        // Without this case, deleting `_warnedUnsynced = false` from Reset() passes every assertion.
        DifficultySync.Reset();
        DifficultySync.MarkMultiplayerRun();
        captured.Clear();
        AreEqual("mp, unsynced after reset -> actual", 3,
            DifficultySync.Resolve(3, overrideEnabled: true, playersOverride: 9, scaleSingleplayerEnabled: false));
        AreEqual("a new unsynced run warns again", 1, captured.Count(l => l.StartsWith("Warn")));

        // A latch arriving before the client's begin-run seam must survive it.
        DifficultySync.Reset();
        DifficultySync.Latch(6, "test");
        DifficultySync.MarkMultiplayerRun();
        AreEqual("latch survives MarkMultiplayerRun", 6,
            DifficultySync.Resolve(3, overrideEnabled: true, playersOverride: 9, scaleSingleplayerEnabled: false));

        // What the host puts on the wire.
        AreEqual("broadcast value with override on", 6,
            DifficultySync.ResolveLocalOverrideForBroadcast(overrideEnabled: true, playersOverride: 6));
        AreEqual("broadcast value with override off", DifficultySyncMessage.NoOverride,
            DifficultySync.ResolveLocalOverrideForBroadcast(overrideEnabled: false, playersOverride: 6));

        // Teardown clears everything, so the next run cannot inherit a stale value.
        DifficultySync.Reset();
        Check("reset clears the latch", !DifficultySync.TryGetHostEffectiveCount(out _));
        Check("reset clears the multiplayer flag", !DifficultySync.IsMultiplayerRun);
    }

    // Characterises the real game formula against the numbers in the bug report, so a future
    // game build that changes the scaling maths fails here loudly instead of silently making
    // the spec's arithmetic wrong. Base HP 39 is in ShrinkerBeetle's 38-40 roll; act index 0
    // scales by 1.1; SetMaxHpInternal truncates.
    private static void HpFormulaStillMatchesTheBugReport()
    {
        AreEqual("reporter's Local HP (4 players)", 171,
            (int)Creature.ScaleHpForMultiplayer(39m, null, 4, 0));
        AreEqual("reporter's Remote HP (5 players)", 214,
            (int)Creature.ScaleHpForMultiplayer(39m, null, 5, 0));
        AreEqual("one player short-circuits unscaled", 39,
            (int)Creature.ScaleHpForMultiplayer(39m, null, 1, 0));
    }
```

Add `using MegaCrit.Sts2.Core.Entities.Creatures;` to the file's usings if it is not already there.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet run --project tests/PatchTests`
Expected: compile errors — `DifficultySync.LogSink`, `MarkMultiplayerRun`, `Latch`, `ResolveForMultiplayer`, `Reset` and `TryGetHostEffectiveCount` do not exist.

- [ ] **Step 3: Write the latch and resolve rule**

Replace the placeholder body of `DifficultySync.cs` with the following (the Harmony seams land in Tasks 3–5; `Apply` is added in Task 3):

```csharp
using System;
using MegaCrit.Sts2.Core.Logging;

namespace Sts2Unlimited;

/// <summary>
/// Makes the host's difficulty-scaling value authoritative for the whole lobby.
///
/// Monster HP and block are computed locally on every peer and then checksummed, so a
/// per-client difficulty override desyncs the run by construction. The host broadcasts its
/// resolved player count at run start (see <see cref="DifficultySyncMessage"/>); every peer
/// latches it here, and <see cref="Sts2Unlimited.GetEffectivePlayerCount"/> reads it.
/// </summary>
public static class DifficultySync
{
    /// <summary>
    /// Logging indirection. The game's logger calls into Godot natives and segfaults outside a
    /// game process, so the headless test harness swaps this for a capturing sink.
    /// </summary>
    internal static Action<LogLevel, string> LogSink =
        (level, message) => Log.LogMessage(level, LogType.Generic, message);

    /// <summary>
    /// The local difficulty settings, injected rather than read directly: this file is compiled
    /// into the headless test harness and Sts2Unlimited.cs is not. Wired at mod load.
    /// </summary>
    internal static Func<bool> LocalOverrideEnabled = () => false;
    internal static Func<int>  LocalPlayersOverride = () => 0;

    private static int? _hostEffectiveCount;
    private static bool _isMultiplayerRun;
    private static bool _warnedUnsynced;
    private static bool _broadcastThisRun;

    public static bool IsMultiplayerRun => _isMultiplayerRun;

    /// <summary>
    /// Records that this peer is in a multiplayer run. Set from vanilla begin-run seams, which
    /// fire regardless of any peer's mod version — inferring it from having received our own
    /// message would make an old-version host look like singleplayer and reproduce the bug.
    ///
    /// Deliberately does NOT clear the latch: our message is sent *before* the vanilla
    /// begin-run message, so on a client the latch is often already set when this runs.
    /// </summary>
    public static void MarkMultiplayerRun()
    {
        if (_isMultiplayerRun) return;
        _isMultiplayerRun = true;
        LogSink(LogLevel.Info, "[DifficultySync] Multiplayer run detected.");
    }

    public static void Latch(int effectiveCount, string source)
    {
        _hostEffectiveCount = effectiveCount;
        LogSink(LogLevel.Info,
            $"[DifficultySync] Host difficulty value latched: {effectiveCount} " +
            $"({(effectiveCount == DifficultySyncMessage.NoOverride ? "no override" : "override")}, from {source}).");
    }

    public static bool TryGetHostEffectiveCount(out int count)
    {
        count = _hostEffectiveCount ?? DifficultySyncMessage.NoOverride;
        return _hostEffectiveCount.HasValue;
    }

    /// <summary>
    /// The whole decision, as a pure function of the latch plus the three local settings. Takes
    /// the settings as arguments rather than reading Sts2Unlimited so the test harness can
    /// exercise it without compiling the Godot-dependent half of the mod.
    /// </summary>
    public static int Resolve(int actualCount, bool overrideEnabled, int playersOverride,
                              bool scaleSingleplayerEnabled)
    {
        if (_isMultiplayerRun) return ResolveForMultiplayer(actualCount);

        if (!overrideEnabled) return actualCount;
        if (actualCount == 1 && !scaleSingleplayerEnabled) return actualCount;
        return playersOverride;
    }

    /// <summary>What this peer scales by during a multiplayer run.</summary>
    private static int ResolveForMultiplayer(int actualCount)
    {
        if (_hostEffectiveCount.HasValue)
        {
            int host = _hostEffectiveCount.Value;
            return host == DifficultySyncMessage.NoOverride ? actualCount : host;
        }

        if (!_warnedUnsynced)
        {
            _warnedUnsynced = true;
            LogSink(LogLevel.Warn,
                $"[DifficultySync] No difficulty value received from the host, so this run scales " +
                $"by the actual player count ({actualCount}) and the local Difficulty Scaling " +
                $"setting is ignored. The host is probably running a version of this mod older " +
                $"than the host-authoritative sync; if the host has a difficulty override enabled, " +
                $"this run can still diverge.");
        }

        return actualCount;
    }

    /// <summary>The value this peer would broadcast if it is the host.</summary>
    public static int ResolveLocalOverrideForBroadcast(bool overrideEnabled, int playersOverride)
        => overrideEnabled ? playersOverride : DifficultySyncMessage.NoOverride;

    /// <summary>Clears all per-run state. Called on run teardown — never mid-run.</summary>
    public static void Reset()
    {
        _hostEffectiveCount = null;
        _isMultiplayerRun   = false;
        _warnedUnsynced     = false;
        _broadcastThisRun   = false;
    }
}
```

- [ ] **Step 4: Route the decision through it**

In `Sts2Unlimited.cs`, replace `GetEffectivePlayerCount` (currently at line 35) with:

```csharp
	// In a multiplayer run the host's value is authoritative for every peer: monster HP and
	// block are derived locally and then checksummed, so a per-client value desyncs the run.
	// Singleplayer keeps the local setting, including the challenge-mode toggle.
	public static int GetEffectivePlayerCount(int actualCount)
		=> DifficultySync.Resolve(actualCount, DifficultyOverrideEnabled,
			DifficultyPlayersOverride, DifficultyScaleSingleplayerEnabled);
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet run --project tests/PatchTests`
Expected: the decision table and the HP characterization PASS; everything else still passes.

- [ ] **Step 6: Build the mod**

Run: `dotnet build --configuration ExportRelease`
Expected: `0 Error(s)`.

- [ ] **Step 7: Commit**

```bash
git add DifficultySync.cs Sts2Unlimited.cs tests/PatchTests/Program.cs
git commit -m "fix: make the host's difficulty value authoritative in multiplayer"
```

---

### Task 3: Receive the host's value

One prefix on `NetMessageBus.SendMessageToAllHandlers` sees every inbound message on every peer, with no service reference to obtain and no register/unregister lifecycle to get wrong.

**Files:**
- Modify: `DifficultySync.cs`
- Modify: `Sts2Unlimited.cs` (one call in `ApplyHarmonyPatches`)
- Test: `tests/PatchTests/Program.cs`

**Interfaces:**
- Consumes: `DifficultySync.Latch`, `DifficultySyncMessage.IsValid` from Tasks 1–2.
- Produces: `DifficultySync.Apply(Harmony harmony)` and `public static void Prefix_SendMessageToAllHandlers(INetMessage message, ulong senderId)`.

- [ ] **Step 1: Write the failing test**

Add to `tests/PatchTests/Program.cs` and call `ReceivingHostValue();` from `Main`:

```csharp
    // The receive path, driven directly: a valid message latches, a foreign one is dropped and
    // reported, and an unrelated game message is ignored without noise.
    private static void ReceivingHostValue()
    {
        var captured = new List<string>();
        DifficultySync.LogSink = (level, message) => captured.Add($"{level}: {message}");

        DifficultySync.Reset();
        DifficultySync.Prefix_SendMessageToAllHandlers(new DifficultySyncMessage(7), 42UL);
        Check("a valid message latches", DifficultySync.TryGetHostEffectiveCount(out int latched));
        AreEqual("a valid message latches its count", 7, latched);

        // A foreign payload decoded into our type: sentinel absent because it never round-tripped.
        DifficultySync.Reset();
        captured.Clear();
        DifficultySync.Prefix_SendMessageToAllHandlers(new DifficultySyncMessage(), 42UL);
        Check("a message with no sentinel is dropped", !DifficultySync.TryGetHostEffectiveCount(out _));
        AreEqual("dropping a foreign message warns", 1, captured.Count(l => l.StartsWith("Warn")));

        // Someone else's message must pass straight through.
        DifficultySync.Reset();
        captured.Clear();
        DifficultySync.Prefix_SendMessageToAllHandlers(new LobbySeedChangedMessage(), 42UL);
        Check("an unrelated message does not latch", !DifficultySync.TryGetHostEffectiveCount(out _));
        AreEqual("an unrelated message logs nothing", 0, captured.Count);
    }

    // The patch targets this task and Tasks 4-5 hook, resolved against the real sts2.dll. These
    // are the signatures that silently move between game builds; a rename shows up here rather
    // than as a mod that loads fine and syncs nothing.
    private static void SyncPatchTargetsResolve()
    {
        Check("NetMessageBus.SendMessageToAllHandlers resolves",
            AccessTools.Method(typeof(NetMessageBus), "SendMessageToAllHandlers",
                new[] { typeof(INetMessage), typeof(ulong) }) != null);
        Check("StartRunLobby.BeginRunForAllPlayers resolves",
            AccessTools.Method(typeof(StartRunLobby), "BeginRunForAllPlayers") != null);
        Check("StartRunLobby.BeginRunLocally resolves",
            AccessTools.Method(typeof(StartRunLobby), "BeginRunLocally") != null);
        Check("StartRunLobby.HandleLobbyBeginRunMessage resolves",
            AccessTools.Method(typeof(StartRunLobby), "HandleLobbyBeginRunMessage") != null);
        Check("StartRunLobby.NetService resolves",
            AccessTools.Property(typeof(StartRunLobby), "NetService") != null);
        Check("LoadRunLobby.TryBeginRunForAllPlayers resolves",
            AccessTools.Method(typeof(LoadRunLobby), "TryBeginRunForAllPlayers") != null);
        Check("LoadRunLobby.BeginRunLocally resolves",
            AccessTools.Method(typeof(LoadRunLobby), "BeginRunLocally") != null);
        Check("LoadRunLobby.HandleLobbyBeginRunMessage resolves",
            AccessTools.Method(typeof(LoadRunLobby), "HandleLobbyBeginRunMessage") != null);
        Check("LoadRunLobby.NetService resolves",
            AccessTools.Property(typeof(LoadRunLobby), "NetService") != null);
        Check("RunLobby.HandleClientRejoinRequestMessage resolves",
            AccessTools.Method(typeof(RunLobby), "HandleClientRejoinRequestMessage") != null);
        Check("RunLobby._netService resolves",
            AccessTools.Field(typeof(RunLobby), "_netService") != null);
        Check("RunLobby.Dispose resolves",
            AccessTools.Method(typeof(RunLobby), "Dispose") != null);
    }
```

Add these usings to `tests/PatchTests/Program.cs`:

```csharp
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
```

Call `SyncPatchTargetsResolve();` from `Main` as well.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet run --project tests/PatchTests`
Expected: compile error — `Prefix_SendMessageToAllHandlers` does not exist.

- [ ] **Step 3: Write the receive prefix and `Apply`**

Add to `DifficultySync.cs` — extend the using block with:

```csharp
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
```

and add these members:

```csharp
    /// <summary>
    /// Every inbound message passes through here, on host and client alike, which is why this is
    /// the receive seam: no service reference to find, and no subscription lifecycle to leak.
    /// </summary>
    public static void Prefix_SendMessageToAllHandlers(INetMessage message, ulong senderId)
    {
        if (message is not DifficultySyncMessage sync) return;

        if (!sync.IsValid)
        {
            LogSink(LogLevel.Warn,
                $"[DifficultySync] Dropped a difficulty-sync message with an unrecognised " +
                $"sentinel (0x{sync.Sentinel:X8}) from {senderId}. Mod-added message ids are " +
                $"positional, so this is most likely another mod's message decoded into ours — " +
                $"check that every peer has the same mods installed.");
            return;
        }

        Latch(sync.EffectiveCount, $"peer {senderId}");
    }

    public static void Apply(Harmony harmony)
    {
        TryPatch(harmony, AccessTools.Method(typeof(NetMessageBus), "SendMessageToAllHandlers",
                     new[] { typeof(INetMessage), typeof(ulong) }),
                 nameof(Prefix_SendMessageToAllHandlers), prefix: true,
                 "NetMessageBus.SendMessageToAllHandlers (receive host difficulty value)");
    }

    /// <summary>
    /// House style: resolve, warn and skip when the game moved the method, never throw out of
    /// ApplyHarmonyPatches.
    /// </summary>
    private static void TryPatch(Harmony harmony, MethodBase target, string patchName,
                                 bool prefix, string description)
    {
        if (target == null)
        {
            LogSink(LogLevel.Warn, $"[DifficultySync] {description} NOT FOUND — skipping.");
            return;
        }

        var patch = new HarmonyMethod(typeof(DifficultySync).GetMethod(patchName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));

        try
        {
            if (prefix) harmony.Patch(target, prefix: patch);
            else        harmony.Patch(target, postfix: patch);
            LogSink(LogLevel.Info, $"[DifficultySync] Patched {description}.");
        }
        catch (Exception e)
        {
            LogSink(LogLevel.Warn, $"[DifficultySync] Failed to patch {description}: {e.Message}");
        }
    }
```

Add `using System.Reflection;` to the file's usings for `MethodBase` and `BindingFlags`.

- [ ] **Step 4: Wire it into mod load**

In `Sts2Unlimited.cs`, in `ApplyHarmonyPatches`, immediately after the existing `DifficultyPatch.Apply(harmony);` line, add:

```csharp
			// DifficultySync is compiled into the headless harness, which does not compile this
			// file — so it reads the local settings through providers rather than directly.
			DifficultySync.LocalOverrideEnabled = () => DifficultyOverrideEnabled;
			DifficultySync.LocalPlayersOverride = () => DifficultyPlayersOverride;
			DifficultySync.Apply(harmony);
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet run --project tests/PatchTests`
Expected: the receive tests and all twelve patch-target assertions PASS.

If any `… resolves` assertion fails, the game build moved that member — stop and re-probe `sts2.dll` rather than guessing a new name.

- [ ] **Step 6: Build the mod**

Run: `dotnet build --configuration ExportRelease`
Expected: `0 Error(s)`.

- [ ] **Step 7: Commit**

```bash
git add DifficultySync.cs Sts2Unlimited.cs tests/PatchTests/Program.cs
git commit -m "feat: receive the host's difficulty value on every peer"
```

---

### Task 4: Broadcast from the host, and detect a multiplayer run

The host latches its own value and broadcasts it just before the vanilla begin-run message; both fresh and saved runs are covered, and all four begin-run seams mark the run as multiplayer.

**Files:**
- Modify: `DifficultySync.cs`
- Test: manual in-game, plus the patch-target assertions already added in Task 3

**Interfaces:**
- Consumes: `MarkMultiplayerRun`, `Latch`, `ResolveLocalOverrideForBroadcast`, `TryPatch` from Tasks 2–3.
- Produces, on `DifficultySync`:
  - `private static void SendHostValue(INetGameService netService, string seam)`
  - `public static void Prefix_StartRunLobbyBeginRun(StartRunLobby __instance)`
  - `public static void Prefix_LoadRunLobbyBeginRun(LoadRunLobby __instance)`
  - `public static void Prefix_ClientBeginRun()`
  - `public static void Postfix_RunLobbyDispose()`
  - seven more registrations inside `Apply`

- [ ] **Step 1: Write the host and client begin-run seams**

Add to `DifficultySync.cs`, and extend its usings with `using MegaCrit.Sts2.Core.Multiplayer.Game;` and `using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;`:

```csharp
    /// <summary>
    /// Decides whether this peer should broadcast as host, and what value — the whole decision as
    /// a pure function of the service's role plus the local settings, so it is testable without
    /// faking INetGameService (whose event members reference types the harness cannot load).
    ///
    /// ONLY a true host may act. This gate is load-bearing: the game reaches
    /// StartRunLobby.BeginRunLocally from BOTH StartRunLobby.BeginRunForAllPlayers (host) and
    /// StartRunLobby.HandleLobbyBeginRunMessage (client). Without the gate, a client handling the
    /// host's begin-run message would latch its OWN setting as "local host" and clobber the value
    /// the host just sent it — reintroducing the exact divergence this feature exists to prevent.
    /// It also keeps singleplayer (NetGameType.Singleplayer) and replays (NetGameType.Replay) from
    /// ever being treated as multiplayer runs, which would bypass the singleplayer toggle.
    ///
    /// Idempotent per run: the host reaches this twice per start (BeginRunForAllPlayers then the
    /// nested BeginRunLocally), and one packet is enough. Cleared by <see cref="Reset"/>.
    /// </summary>
    internal static bool TryBeginHostBroadcast(NetGameType type, out int value)
    {
        value = DifficultySyncMessage.NoOverride;

        if (type != NetGameType.Host) return false;
        if (_broadcastThisRun) return false;

        _broadcastThisRun = true;
        MarkMultiplayerRun();

        value = ResolveLocalOverrideForBroadcast(LocalOverrideEnabled(), LocalPlayersOverride());
        Latch(value, "local host");
        return true;
    }

    /// <summary>
    /// Host side of run start. Sends before the vanilla begin-run message on the same Reliable
    /// channel so peers latch the value before the first CombatState.CreateCreature, and latches
    /// locally too — so host and clients then follow the identical resolve path.
    /// </summary>
    private static void SendHostValue(INetGameService netService, string seam)
    {
        if (netService == null)
        {
            LogSink(LogLevel.Warn,
                $"[DifficultySync] {seam}: no net service available, so no difficulty value was " +
                $"broadcast and this peer's role is unknown. Peers will scale by the actual player count.");
            return;
        }

        if (!TryBeginHostBroadcast(netService.Type, out int value)) return;

        try
        {
            netService.SendMessage(new DifficultySyncMessage(value));
            LogSink(LogLevel.Info, $"[DifficultySync] {seam}: broadcast difficulty value {value}.");
        }
        catch (Exception e)
        {
            LogSink(LogLevel.Warn, $"[DifficultySync] {seam}: broadcast failed: {e.Message}");
        }
    }

    public static void Prefix_StartRunLobbyBeginRun(StartRunLobby __instance)
        => SendHostValue(__instance?.NetService, "StartRunLobby begin-run");

    public static void Prefix_LoadRunLobbyBeginRun(LoadRunLobby __instance)
        => SendHostValue(__instance?.NetService, "LoadRunLobby begin-run");

    /// <summary>
    /// Client side of run start. Vanilla, so it fires whatever version the host runs — which is
    /// exactly why the multiplayer flag comes from here and not from our own message.
    /// </summary>
    public static void Prefix_ClientBeginRun() => MarkMultiplayerRun();

    /// <summary>Run teardown. The only place per-run state is cleared.</summary>
    public static void Postfix_RunLobbyDispose()
    {
        LogSink(LogLevel.Info, "[DifficultySync] Run lobby disposed — clearing synced state.");
        Reset();
    }
```

- [ ] **Step 2: Register the seams**

In `DifficultySync.Apply`, after the existing receive registration, add:

```csharp
        // Host: broadcast before the vanilla begin-run message goes out.
        TryPatch(harmony, AccessTools.Method(typeof(StartRunLobby), "BeginRunForAllPlayers"),
                 nameof(Prefix_StartRunLobbyBeginRun), prefix: true,
                 "StartRunLobby.BeginRunForAllPlayers (broadcast difficulty value)");
        TryPatch(harmony, AccessTools.Method(typeof(StartRunLobby), "BeginRunLocally"),
                 nameof(Prefix_StartRunLobbyBeginRun), prefix: true,
                 "StartRunLobby.BeginRunLocally (broadcast difficulty value)");
        TryPatch(harmony, AccessTools.Method(typeof(LoadRunLobby), "TryBeginRunForAllPlayers"),
                 nameof(Prefix_LoadRunLobbyBeginRun), prefix: true,
                 "LoadRunLobby.TryBeginRunForAllPlayers (broadcast difficulty value, saved run)");
        TryPatch(harmony, AccessTools.Method(typeof(LoadRunLobby), "BeginRunLocally"),
                 nameof(Prefix_LoadRunLobbyBeginRun), prefix: true,
                 "LoadRunLobby.BeginRunLocally (broadcast difficulty value, saved run)");

        // Client: vanilla seams, so these fire regardless of the host's mod version.
        TryPatch(harmony, AccessTools.Method(typeof(StartRunLobby), "HandleLobbyBeginRunMessage"),
                 nameof(Prefix_ClientBeginRun), prefix: true,
                 "StartRunLobby.HandleLobbyBeginRunMessage (detect multiplayer run)");
        TryPatch(harmony, AccessTools.Method(typeof(LoadRunLobby), "HandleLobbyBeginRunMessage"),
                 nameof(Prefix_ClientBeginRun), prefix: true,
                 "LoadRunLobby.HandleLobbyBeginRunMessage (detect multiplayer run, saved run)");

        // Teardown.
        TryPatch(harmony, AccessTools.Method(typeof(RunLobby), "Dispose"),
                 nameof(Postfix_RunLobbyDispose), prefix: false,
                 "RunLobby.Dispose (clear synced state)");
```

- [ ] **Step 3: Write the role-gate tests, then run the suite**

These are not optional extras: the first two are the regression test for a defect that shipped in
this task's first attempt. Add to `tests/PatchTests/Program.cs` and call `HostBroadcastRoleGate();`
from `Main`:

```csharp
    // The game reaches StartRunLobby.BeginRunLocally from the host path
    // (BeginRunForAllPlayers) AND the client path (HandleLobbyBeginRunMessage), and both are
    // patched with the host-send prefix. Only a real host may latch "local host" — otherwise a
    // client clobbers the value the host just sent it and the run diverges, which is the whole
    // bug this feature exists to prevent.
    private static void HostBroadcastRoleGate()
    {
        // A client must not broadcast, must not mark the run, and must not touch the latch.
        DifficultySync.Reset();
        DifficultySync.Latch(6, "host");
        Check("client does not broadcast",
            !DifficultySync.TryBeginHostBroadcast(NetGameType.Client, out _));
        Check("client leaves the host's latch intact", DifficultySync.TryGetHostEffectiveCount(out int kept));
        AreEqual("client keeps the host's value", 6, kept);

        // Singleplayer and replay must never be treated as multiplayer runs.
        DifficultySync.Reset();
        Check("singleplayer does not broadcast",
            !DifficultySync.TryBeginHostBroadcast(NetGameType.Singleplayer, out _));
        Check("singleplayer is not a multiplayer run", !DifficultySync.IsMultiplayerRun);
        Check("replay does not broadcast",
            !DifficultySync.TryBeginHostBroadcast(NetGameType.Replay, out _));
        Check("replay is not a multiplayer run", !DifficultySync.IsMultiplayerRun);

        // A host broadcasts exactly once per run, latching its own resolved value.
        DifficultySync.Reset();
        DifficultySync.LocalOverrideEnabled = () => true;
        DifficultySync.LocalPlayersOverride = () => 7;
        Check("host broadcasts", DifficultySync.TryBeginHostBroadcast(NetGameType.Host, out int sent));
        AreEqual("host broadcasts its resolved value", 7, sent);
        Check("host marks the run as multiplayer", DifficultySync.IsMultiplayerRun);
        Check("host latched its own value", DifficultySync.TryGetHostEffectiveCount(out int own));
        AreEqual("host latched the same value it sent", 7, own);
        Check("host does not broadcast twice in one run",
            !DifficultySync.TryBeginHostBroadcast(NetGameType.Host, out _));

        // A new run broadcasts again, so Reset() must clear the once-per-run guard.
        DifficultySync.Reset();
        Check("a new run broadcasts again",
            DifficultySync.TryBeginHostBroadcast(NetGameType.Host, out _));

        DifficultySync.LocalOverrideEnabled = () => false;
        DifficultySync.LocalPlayersOverride = () => 0;
    }
```

Add `using MegaCrit.Sts2.Core.Multiplayer.Game;` for `NetGameType` if it is not already present.

Run: `dotnet run --project tests/PatchTests`
Expected: all green, including the new role-gate assertions.

Prove the gate's first two assertions have teeth: temporarily change `if (type != NetGameType.Host) return false;` to `if (false) return false;`, re-run, and confirm "client keeps the host's value" FAILS. Restore it.

- [ ] **Step 4: Build the mod**

Run: `dotnet build --configuration ExportRelease`
Expected: `0 Error(s)`.

- [ ] **Step 5: Verify in-game — two peers, different values**

This is the task's real test; the spec's success criteria 1, 2 and 5 depend on it.

1. Deploy to two installs (set `STS2_MOD_DIR` and rebuild, or copy `release/` into `<game>/mods/`).
2. Host: Difficulty Scaling override **on**, value **6**. Client: override **on**, value **3**.
3. Start a run and finish the first fight.
4. Expected in both logs: `[DifficultySync] Host difficulty value latched: 6`, and no `StateDivergence`.
5. Repeat with the host's override **off** and the client's **on** — both should scale by the actual count.
6. Repeat solo with the singleplayer toggle on — the local value must still apply.

Confirm from the host log that the broadcast line precedes the vanilla begin-run traffic; if a client ever logs the latch *after* entering combat, ordering assumption A2 in the spec is wrong — record it and fall back to F7 (the `LobbyBeginRunMessage` tail) rather than adding retries.

- [ ] **Step 6: Commit**

```bash
git add DifficultySync.cs
git commit -m "feat: broadcast the host's difficulty value at run start"
```

---

### Task 5: Keep a mid-run rejoin in step

`ToughEgg.Hatch`, `TestSubject.Revive` and `DecimillipedeSegment.AfterAddedToRoom` scale HP *during* combat, so a player who rejoins without the host's value diverges on the next hatch or revive. The in-run lobby is `RunLobby`, which exposes its service only as a private field.

**Files:**
- Modify: `DifficultySync.cs`
- Test: manual in-game, plus the Task 3 patch-target assertions

**Interfaces:**
- Consumes: `ResolveLocalOverrideForBroadcast`, `TryPatch` from Tasks 2–3.
- Produces: `Postfix_RunLobbyRejoin(RunLobby __instance, ulong senderId)`.

- [ ] **Step 1: Write the rejoin seam**

Add to `DifficultySync.cs`:

```csharp
    private static readonly FieldInfo _runLobbyNetService =
        AccessTools.Field(typeof(RunLobby), "_netService");

    /// <summary>
    /// Decides whether to answer a rejoin and with what value — pure, parameterised by the role
    /// enum rather than the service, so both mandated rules below are unit-testable. Reading
    /// <c>netService.Type</c> needs an instance; deciding on the resulting enum does not, and
    /// <see cref="TryBeginHostBroadcast"/> is the same extraction one layer up.
    ///
    /// Rule 1 — only a host answers. Defence in depth, mirroring the run-start gate.
    /// ClientRejoinRequestMessage has ShouldBroadcast = false, so today only the host handles it and
    /// this check never fires. A future game build that broadcast it would otherwise have every
    /// client answer the rejoiner with its own local value — the clobbering bug the run-start gate
    /// exists to prevent.
    ///
    /// Rule 2 — re-send the value THIS RUN is using, not a freshly resolved one: the latch holds the
    /// authoritative number every other peer already has. The two coincide today because settings
    /// are locked while a run is in progress, but re-resolving would silently hand a rejoiner a
    /// different number if that ever stopped being true.
    /// </summary>
    internal static bool TryBuildRejoinResend(NetGameType type, out int value)
    {
        value = DifficultySyncMessage.NoOverride;

        if (type != NetGameType.Host) return false;

        value = TryGetHostEffectiveCount(out int latched)
            ? latched
            : ResolveLocalOverrideForBroadcast(LocalOverrideEnabled(), LocalPlayersOverride());
        return true;
    }

    /// <summary>
    /// A rejoining peer missed the run-start broadcast, so the host re-sends to that peer alone.
    /// Without this, the rejoiner scales the next ToughEgg.Hatch or TestSubject.Revive by a
    /// different player count and the run diverges mid-combat.
    ///
    /// RunLobby is the in-run lobby and keeps its service in a private field, unlike the two
    /// pre-run lobbies which expose a public NetService property.
    /// </summary>
    public static void Postfix_RunLobbyRejoin(RunLobby __instance, ulong senderId)
    {
        if (__instance == null) return;

        var netService = _runLobbyNetService?.GetValue(__instance) as INetGameService;
        if (netService == null)
        {
            LogSink(LogLevel.Warn,
                "[DifficultySync] Rejoin: could not reach RunLobby's net service, so the " +
                "rejoining peer was not sent the host's difficulty value and may diverge.");
            return;
        }

        if (!TryBuildRejoinResend(netService.Type, out int value)) return;

        try
        {
            netService.SendMessage(new DifficultySyncMessage(value), senderId);
            LogSink(LogLevel.Info,
                $"[DifficultySync] Rejoin: re-sent difficulty value {value} to peer {senderId}.");
        }
        catch (Exception e)
        {
            LogSink(LogLevel.Warn, $"[DifficultySync] Rejoin: re-send to {senderId} failed: {e.Message}");
        }
    }
```

- [ ] **Step 2: Register it**

In `DifficultySync.Apply`, before the teardown registration, add:

```csharp
        TryPatch(harmony, AccessTools.Method(typeof(RunLobby), "HandleClientRejoinRequestMessage"),
                 nameof(Postfix_RunLobbyRejoin), prefix: false,
                 "RunLobby.HandleClientRejoinRequestMessage (re-send difficulty value on rejoin)");
```

The postfix takes `senderId` by name, matching the vanilla parameter `(ClientRejoinRequestMessage, ulong senderId)`. If Harmony reports an unmatched parameter, confirm the real parameter name with `AccessTools.Method(typeof(RunLobby), "HandleClientRejoinRequestMessage").GetParameters()` and use that name.

- [ ] **Step 3: Run the tests and build**

Run: `dotnet run --project tests/PatchTests`
Expected: all green.

Run: `dotnet build --configuration ExportRelease`
Expected: `0 Error(s)`.

- [ ] **Step 4: Verify in-game — rejoin (spec success criteria 3 and 4)**

1. Host with the override on at **6**, one client with **3**; start a run.
2. Disconnect the client mid-run and rejoin.
3. Expected on the host: `Rejoin: re-sent difficulty value 6 to peer …`; on the client: `Host difficulty value latched: 6`.
4. Fight an encounter containing a Tough Egg or Test Subject so the mid-combat scaling path runs; expect no divergence.
5. Save, quit to menu, and reload the run as a multiplayer load — expect the `LoadRunLobby` broadcast line and no divergence.

- [ ] **Step 5: Commit**

```bash
git add DifficultySync.cs
git commit -m "feat: re-send the host's difficulty value on rejoin"
```

---

### Task 6: Show the host's value in Settings

A slider showing a number that is not being used is worse than no number. When a host value is latched and we are not the host, the Difficulty rows grey out and show the host's value, reusing the lock pattern already in the file.

**Files:**
- Modify: `SettingsMenuIntegration.cs` (around `ConfigureDifficultySlider` at line 282 and `LockRowIfRunInProgress` at line 404)
- Test: manual in-game

**Interfaces:**
- Consumes: `DifficultySync.TryGetHostEffectiveCount`, `DifficultySync.IsMultiplayerRun` from Task 2.
- Produces: no new public surface.

- [ ] **Step 1: Make the difficulty slider show the host's value when there is one**

Replace `ConfigureDifficultySlider` (line 282) with:

```csharp
    // In a multiplayer run the host's value is what the game actually scales by, so show that
    // and lock the row — see DifficultySync. Falls back to the local setting everywhere else.
    private static void ConfigureDifficultySlider(Node diffSliderRow, int currentDifficulty)
    {
        // hostValue is pre-initialised rather than declared in the `out`: when IsMultiplayerRun is
        // false the call never runs, and C#'s definite-assignment analysis cannot correlate
        // hostControls with hostValue at the ternary below (CS0165).
        int hostValue = DifficultySyncMessage.NoOverride;
        bool hostControls = DifficultySync.IsMultiplayerRun
                         && DifficultySync.TryGetHostEffectiveCount(out hostValue)
                         && hostValue != DifficultySyncMessage.NoOverride;

        int displayed = hostControls ? hostValue : currentDifficulty;
        string label  = hostControls ? "Difficulty Scaling (set by host)" : "Difficulty Scaling";

        ConfigureSlider(diffSliderRow, label, DIFFICULTY_MIN, DIFFICULTY_MAX, DIFFICULTY_OFFSET,
            displayed,
            value => { Sts2Unlimited.DifficultyPlayersOverride = value; SaveSettings(); });

        if (hostControls)
            LockRow(diffSliderRow, FindSliderControl(diffSliderRow));
    }
```

- [ ] **Step 2: Split the lock helper so it can be called for a reason other than a run being in progress**

`LockRowIfRunInProgress` currently decides *and* acts. Split the acting half out, keeping the existing entry point so its other two callers are unaffected. Replace the method at line 404 with:

```csharp
    private static void LockRowIfRunInProgress(Node row, Node interactiveControl)
    {
        if (!IsRunInProgress()) return;
        LockRow(row, interactiveControl);
    }

    private static void LockRow(Node row, Node interactiveControl)
    {
        if (GetStsGray() is Color gray)
            row.Set("modulate", gray); // cascades to Label + control since both are children of row

        if (interactiveControl == null) return;

        if (interactiveControl.HasMethod("Disable"))
            interactiveControl.Call("Disable"); // NClickableControl (tickbox)
        else if (interactiveControl is Control control) // NSlider has no Disabled flag of its own
        {
            control.MouseFilter = Control.MouseFilterEnum.Ignore;
            control.FocusMode   = Control.FocusModeEnum.None;
        }
    }
```

- [ ] **Step 3: Add the slider-control lookup the new call needs**

`ConfigureSlider` finds the control internally and does not hand it back. Add this helper next to `FindNodeByType`:

```csharp
    // ConfigureSlider resolves this internally; the host-controlled lock needs it too.
    // Reports a failed lookup like every other lookup in this file: returning null silently would
    // leave the row greyed but still draggable, with nothing in the log to explain it.
    private static Node FindSliderControl(Node row)
    {
        var masterType = Type.GetType(
            "MegaCrit.Sts2.Core.Nodes.Screens.Settings.NMasterVolumeSlider, sts2", false);
        if (masterType == null)
        {
            GD.PrintErr("[Sts2Unlimited] NMasterVolumeSlider type not found — difficulty row left unlocked.");
            return null;
        }

        var sliderControl = FindNodeByType(row, masterType);
        if (sliderControl == null)
        {
            GD.PrintErr("[Sts2Unlimited] NMasterVolumeSlider not found in the difficulty row — left unlocked.");
            return null;
        }

        var slider = sliderControl.GetNodeOrNull("Slider");
        if (slider == null)
            GD.PrintErr("[Sts2Unlimited] 'Slider' child not found in the difficulty row — left unlocked.");
        return slider;
    }
```

- [ ] **Step 4: Build and run the tests**

Run: `dotnet build --configuration ExportRelease`
Expected: `0 Error(s)`.

Run: `dotnet run --project tests/PatchTests`
Expected: all green. (`SettingsMenuIntegration.cs` is not compiled into the harness — it needs Godot runtime types — so this only guards against regressions elsewhere.)

- [ ] **Step 5: Verify in-game**

1. As a client in a multiplayer run where the host has an override, open Settings → General.
2. Expected: the Difficulty Scaling row is greyed out, reads "Difficulty Scaling (set by host)", and shows the host's number, not yours.
3. As the host, or in singleplayer, the row behaves exactly as before.
4. Check the rows are still reachable by scrolling — the v1.0.1 fix re-measures on content change, and the label text is now longer.

- [ ] **Step 6: Commit**

```bash
git add SettingsMenuIntegration.cs
git commit -m "feat: show the host's difficulty value in Settings"
```

---

### Task 7: Document the behaviour change

A non-host's Difficulty Scaling slider no longer affects multiplayer. That is a visible behaviour change and needs saying in all three places the mod documents itself.

**Files:**
- Modify: `README.md` (the "How It Works" section and the file list)
- Modify: `MOD_README.md`
- Modify: `workshop/workshop.json` (`description` and `changeNote`)
- Test: `tests/PatchTests/Program.cs` (the existing doc assertions must still pass)

**Interfaces:**
- Consumes: nothing.
- Produces: nothing.

- [ ] **Step 1: Add the two new files to the README file list**

In `README.md`, next to the existing `DifficultyPatch.cs` line (around line 100), add:

```
DifficultySyncMessage.cs    — Wire contract carrying the host's difficulty value to peers
DifficultySync.cs           — Makes the host's difficulty value authoritative in multiplayer
```

- [ ] **Step 2: Explain the behaviour in README.md**

Add to the "How It Works" section:

```markdown
### Difficulty scaling in multiplayer

Monster HP and block are not replicated — every peer computes them locally from the
player count and the game then compares checksums. So if two players use different
Difficulty Scaling values, their monsters differ and the run dies with a State
Divergence at the first fight.

The host's value is therefore authoritative for the whole lobby. It is broadcast at run
start (and re-sent to anyone who rejoins) as `DifficultySyncMessage`; a non-host's own
slider has no effect on a multiplayer run, and Settings shows the host's value instead.

If no host value arrives — the host is running a version older than this one — the run
scales by the actual player count and logs a warning rather than silently using a local
value that nobody else shares.
```

- [ ] **Step 3: Add the same note, shorter, to MOD_README.md**

```markdown
**Difficulty scaling in multiplayer is controlled by the host.** Everyone in the lobby
uses the host's value; your own slider only applies to singleplayer. All players need
the mod, and a host on an older version cannot send a value — that run falls back to
scaling by the real player count.
```

- [ ] **Step 4: Update the Workshop page**

In `workshop/workshop.json`, change the difficulty bullet in `description` from the current
"Optional: override the player count used for difficulty scaling…" to:

```
[*]Optional: override the player count used for difficulty scaling (1–16), independent of how many are actually connected — including an optional toggle to apply it in singleplayer as a challenge mode. In multiplayer the host's value applies to everyone.
```

and replace `changeNote` with:

```
Fixes a State Divergence disconnect when players had different Difficulty Scaling values.

Monster HP and block are worked out separately on each PC and then checksummed, so two players with different difficulty values built different monsters and the game dropped the whole lobby at the first fight. The host's value is now authoritative: it is sent to everyone at run start and re-sent to anyone who rejoins, a non-host's own slider no longer affects multiplayer, and Settings shows the host's value while a run is live.

Everyone should be on this version — a host running an older one cannot send its value, and those runs fall back to scaling by the actual player count with a warning in the log.
```

- [ ] **Step 5: Run the tests**

Run: `dotnet run --project tests/PatchTests`
Expected: all green, including the existing `DocumentedPlayerRangeMatchesTheCode` assertions that parse these three files. If a range assertion trips, the edit disturbed a "2-16" or "1-16" string — restore it.

- [ ] **Step 6: Commit**

```bash
git add README.md MOD_README.md workshop/workshop.json
git commit -m "docs: the host controls difficulty scaling in multiplayer"
```

---

## Verification Checklist

Against the spec's success criteria:

| # | Criterion | Covered by |
|---|---|---|
| 1 | Peers with different values agree on monster HP | Task 4, Step 5 (in-game) |
| 2 | Host override off + client on also agree | Task 4, Step 5 (in-game) |
| 3 | Mid-run rejoin stays in step through hatch/revive | Task 5, Step 4 (in-game) |
| 4 | Loaded multiplayer runs sync too | Task 5, Step 4 (in-game) |
| 5 | Singleplayer unchanged, challenge toggle included | Task 2 decision table + Task 4, Step 5 |
| 6 | Unsynced run scales by actual count, warns once | Task 2 decision table |
| 7 | Harness green incl. round trip and decision table | Tasks 1–3 |

Open risks carried from the spec, to confirm while doing the in-game steps: **A1** mod message ids are positional (watch for the dropped-sentinel warning when other message-adding mods are installed); **A2** our message must be handled before the first `CombatState.CreateCreature`; **A4** an old-version host cannot be fixed, only reported.
