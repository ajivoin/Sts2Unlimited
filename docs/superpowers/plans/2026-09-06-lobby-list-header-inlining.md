# Lobby list-header inlining fix — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the widened lobby player-list header survive another mod inlining vanilla `Serialize`/`Deserialize` IL, so clients can join when STS2-RitsuLib is also installed.

**Architecture:** The existing `PacketSizePatch` transpiler keeps rewriting the vanilla 3-bit constant at every `WriteList`/`ReadList` call site, but now *records the generic element type* each time it rewrites one. A new `ListHeaderWidthPatch` then prefix-patches the leaves — `PacketWriter.WriteList<T>` and `PacketReader.ReadList<T>` for exactly those recorded `T` — and widens any `lengthBits == 3` that still reaches them. Leaves are never inlined, so the inlined copy of the caller and the transpiled copy funnel through the same guard and cannot disagree.

**Tech Stack:** C# / .NET 9, Godot.NET.Sdk 4.5.1, Lib.Harmony 2.4.2, `sts2.dll` reference assembly.

**Spec:** `docs/superpowers/specs/2026-09-06-lobby-list-header-inlining.md` — read it first. It carries the reporter's evidence, the four findings from verifying it against the current `sts2.dll`, and the environment facts that shape the tests.

## Global Constraints

- Wire width always comes from `PacketSizePatch.GetRequiredBits()` (currently 5, derived from `AbsoluteMaxPlayers = 16`), **never** from `MaxPlayersOverride`. Peers with different local settings must agree on the format.
- Only the vanilla constant **3** is ever rewritten. Widths 4 (`MapDrawingMessage`) and 32 (everything else) are intentional and must pass through untouched.
- **No production code may name the element type.** It is `StartRunLobbyPlayer` in the current build and was `LobbyPlayer` in the previous one — MegaCrit renamed it. Types are discovered by the transpiler at runtime. Tests may print the name but must not assert on it.
- **The game's logger segfaults headless.** `Log.LogMessage` calls into Godot natives; outside a game process it kills the harness with SIGSEGV (exit 139), no exception. All logging in new code goes through `ListHeaderWidthPatch.LogSink` so the harness can capture it. The harness must never call `PacketSizePatch.Apply` (which logs directly).
- `sts2.dll` was refreshed from the live install and is gitignored, as are `Sentry.Godot.dll` and `Sentry.dll` — the harness needs those two *referenced* because `sts2.dll` runs `SentryAutoInit` in a module initializer on first type access. All three are already in place at the repo root; the `.gitignore` entry is already committed to the working tree.
- Every patch site follows the file's existing style: try/catch, `[Prefix] message` log lines, skip-and-warn on failure, never throw out of `ApplyHarmonyPatches`.
- Branch: `fix/list-header-width-inlining` (already created). Conventional commits — CI derives the version bump from them, so do **not** hand-edit `Sts2Unlimited.json`.
- Mod build check: `dotnet build --configuration ExportRelease` from the repo root.
- Test command: `dotnet run --project tests/PatchTests`.

## File Structure

| File | Responsibility |
|---|---|
| `sts2unlimited.csproj` (modify) | Bump `Lib.Harmony` to 2.4.2; exclude `tests/**` from the Godot SDK compile glob. |
| `PacketSizePatch.cs` (modify) | Transpiles constant 3 → `GetRequiredBits()`; **new:** records which generic element types it rewrote, and exposes `VanillaListBits`. |
| `ListHeaderWidthPatch.cs` (create) | Leaf guard. `TryWiden` decision function, the two Harmony prefixes, a `LogSink` seam, and `Apply`. |
| `Sts2Unlimited.cs` (modify) | One call: `ListHeaderWidthPatch.Apply(harmony)` immediately after `PacketSizePatch.Apply(harmony)`. |
| `tests/PatchTests/PatchTests.csproj` (create) | Headless harness — references `sts2.dll`, the two Sentry DLLs, GodotSharp and Harmony, and compiles the patch source files directly. Not in `sts2unlimited.sln`, so CI is unaffected. |
| `tests/PatchTests/Program.cs` (create) | The tests. Plain console asserts, exit code 1 on any failure — no test framework dependency. |
| `README.md` (modify) | Mod-compatibility note in "How It Works". |

---

### Task 1: Bump Harmony to the version the game ships

The game ships `0Harmony.dll` 2.4.2 in `data_sts2_windows_x86_64/`, while the mod references and bundles 2.3.3. 2.3.3 cannot patch *anything* on .NET 9 or 10 — verified, it throws `MemberAccessException` on `LocalBuilder` even for a trivial method — so in-game the mod has been binding to the game's copy. Matching versions removes that ambiguity and is a prerequisite for the harness, which cannot patch on 2.3.3.

**Files:**
- Modify: `sts2unlimited.csproj:11`

**Interfaces:**
- Consumes: nothing.
- Produces: `Lib.Harmony 2.4.2` on the compile and bundle path for every later task.

- [ ] **Step 1: Bump the package reference**

In `sts2unlimited.csproj`, change:

```xml
    <PackageReference Include="Lib.Harmony" Version="2.3.3" />
```

to:

```xml
    <PackageReference Include="Lib.Harmony" Version="2.4.2" />
```

- [ ] **Step 2: Build and confirm nothing broke**

Run: `dotnet build --configuration ExportRelease`
Expected: `0 Error(s)`. The mod's Harmony surface (`Harmony`, `HarmonyMethod`, `AccessTools`, `CodeInstruction`, `harmony.Patch(prefix/postfix/transpiler)`) is unchanged between 2.3.3 and 2.4.2, so no source edits should be needed. If any call fails to compile, stop and report rather than working around it.

- [ ] **Step 3: Confirm the bundled Harmony is the new one**

Run: `strings release/0Harmony.dll | grep -oE '2\.[0-9]+\.[0-9]+\.[0-9]+' | sort -u | head -1`
Expected: `2.4.2.0`

- [ ] **Step 4: Commit**

```bash
git add sts2unlimited.csproj .gitignore
git commit -m "$(cat <<'EOF'
chore: bump Lib.Harmony to 2.4.2 to match the game's bundled copy

Slay the Spire 2 ships 0Harmony.dll 2.4.2 in data_sts2_windows_x86_64. Harmony
2.3.3 cannot patch on .NET 9 or 10 at all (MemberAccessException on LocalBuilder),
so the mod was already binding to the game's copy at runtime.

Also gitignores the Sentry reference assemblies the headless tests need.

EOF
)"
```

---

### Task 2: Headless test harness

Nothing else in this plan is checkable without this, and the Godot SDK's compile glob breaks the mod build the moment `tests/` exists — so the exclusion belongs here too.

**Files:**
- Create: `tests/PatchTests/PatchTests.csproj`
- Create: `tests/PatchTests/Program.cs`
- Modify: `sts2unlimited.csproj`

**Interfaces:**
- Consumes: `PacketSizePatch.RequiredBits(int)`, `PacketSizePatch.GetRequiredBits()` (both exist, public static).
- Produces: `Tests.Check(string, bool, string)` and `Tests.AreEqual<T>(string, T, T)` assert helpers plus a `Main` returning 0/1, used by every later task.

- [ ] **Step 1: Write the failing test**

Create `tests/PatchTests/Program.cs`:

```csharp
using System;
using Sts2Unlimited;

internal static class Tests
{
    private static int _failures;

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine(ok ? $"PASS  {name}" : $"FAIL  {name}{(detail.Length > 0 ? " — " + detail : "")}");
        if (!ok) _failures++;
    }

    private static void AreEqual<T>(string name, T expected, T actual)
        => Check(name, Equals(expected, actual), $"expected {expected}, got {actual}");

    private static int Main()
    {
        RequiredBitsMath();

        Console.WriteLine(_failures == 0 ? "\nALL TESTS PASSED" : $"\n{_failures} TEST(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    // The wire width must stay fixed at 5 for AbsoluteMaxPlayers = 16, independent of the
    // local MaxPlayers setting — peers with different settings must still agree.
    private static void RequiredBitsMath()
    {
        AreEqual("RequiredBits(1)", 1, PacketSizePatch.RequiredBits(1));
        AreEqual("RequiredBits(4)", 3, PacketSizePatch.RequiredBits(4));
        AreEqual("RequiredBits(7)", 3, PacketSizePatch.RequiredBits(7));
        AreEqual("RequiredBits(8)", 4, PacketSizePatch.RequiredBits(8));
        AreEqual("RequiredBits(16)", 5, PacketSizePatch.RequiredBits(16));
        AreEqual("GetRequiredBits()", 5, PacketSizePatch.GetRequiredBits());
    }
}
```

- [ ] **Step 2: Run it to make sure it fails**

Run: `dotnet run --project tests/PatchTests`
Expected: FAIL — `MSB1009: Project file does not exist`.

- [ ] **Step 3: Create the harness project**

Create `tests/PatchTests/PatchTests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>disable</Nullable>
    <AssemblyName>PatchTests</AssemblyName>
    <RootNamespace>PatchTests</RootNamespace>
    <!-- sts2.dll targets .NET 9; only .NET 10 is installed system-wide. -->
    <RollForward>Major</RollForward>
  </PropertyGroup>

  <ItemGroup>
    <!-- Compiled into the test assembly rather than referenced, so tests can reach the
         internal test seams and no Godot SDK project reference is needed. -->
    <Compile Include="../../PacketSizePatch.cs" Link="PacketSizePatch.cs" />
  </ItemGroup>

  <ItemGroup>
    <Reference Include="../../sts2.dll" />
    <!-- sts2.dll runs SentryAutoInit in a module initializer on first type access.
         These must be *referenced*, not just present: .NET probes only deps.json. -->
    <Reference Include="../../Sentry.Godot.dll" />
    <Reference Include="../../Sentry.dll" />
    <PackageReference Include="GodotSharp" Version="4.5.1" />
    <PackageReference Include="Lib.Harmony" Version="2.4.2" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Stop the mod build from swallowing the harness**

Godot.NET.Sdk globs `**/*.cs`, so without this the mod project compiles `tests/PatchTests/Program.cs` (a second `Main`) and a second copy of `PacketSizePatch.cs`. Add to `sts2unlimited.csproj`, after the existing `<ItemGroup>` holding the references:

```xml
  <ItemGroup>
    <!-- tests/ is a standalone headless harness with its own entry point;
         Godot.NET.Sdk's default **/*.cs glob would otherwise compile it into the mod. -->
    <Compile Remove="tests/**/*.cs" />
  </ItemGroup>
```

- [ ] **Step 5: Run the tests and make sure they pass**

Run: `dotnet run --project tests/PatchTests`
Expected: a `SentryGodotInitializer: Sentry GDExtension not loaded in this process; skipping` line (harmless and expected), then 6 `PASS` lines and `ALL TESTS PASSED`, exit 0.

If it exits 139 with no output, something called the game's logger — see the Global Constraints.

- [ ] **Step 6: Verify the mod itself still builds**

Run: `dotnet build --configuration ExportRelease`
Expected: `0 Error(s)`. `CS0017` (more than one entry point) or a duplicate `PacketSizePatch` means Step 4 did not take.

- [ ] **Step 7: Commit**

```bash
git add tests/PatchTests/PatchTests.csproj tests/PatchTests/Program.cs sts2unlimited.csproj
git commit -m "$(cat <<'EOF'
test: add headless patch harness running against the real game assembly

Excluded from the Godot SDK compile glob so the mod build is unaffected.

EOF
)"
```

---

### Task 3: Transpiler records the element types it rewrites

Requirement **R1**. This is what lets the leaf guard cover exactly what the transpiler covered without naming a game type that changes between builds — it was `LobbyPlayer` last build and is `StartRunLobbyPlayer` now.

**Files:**
- Modify: `PacketSizePatch.cs`
- Modify: `tests/PatchTests/Program.cs`

**Interfaces:**
- Consumes: `PacketSizePatch.Transpile_SerializeDeserialize(IEnumerable<CodeInstruction>)` (exists, public static).
- Produces:
  - `public const int PacketSizePatch.VanillaListBits = 3;`
  - `public static IReadOnlyCollection<Type> PacketSizePatch.RewrittenListElementTypes { get; }`
  - `internal static void PacketSizePatch.ResetRewrittenListElementTypes()` — test seam only.

- [ ] **Step 1: Write the failing test**

Add these usings to the top of `tests/PatchTests/Program.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
```

Add `TranspilerRewritesRealGameIl();` to `Main` directly after `RequiredBitsMath();`, and add:

```csharp
    // Runs the real transpiler over the real IL of the real game methods — the exact call
    // sites the bug report blames. Guards both halves of R1: the rewrite happens, and the
    // element type behind it is recorded. Asserts on the *count* rather than the type name,
    // because the name changes between game builds and the mod is meant to survive that.
    private static void TranspilerRewritesRealGameIl()
    {
        AssertTranspile("LobbyBeginRunMessage.Serialize",
            AccessTools.Method(typeof(LobbyBeginRunMessage), "Serialize", new[] { typeof(PacketWriter) }));
        AssertTranspile("LobbyBeginRunMessage.Deserialize",
            AccessTools.Method(typeof(LobbyBeginRunMessage), "Deserialize", new[] { typeof(PacketReader) }));
        AssertTranspile("ClientLobbyJoinResponseMessage.Serialize",
            AccessTools.Method(typeof(ClientLobbyJoinResponseMessage), "Serialize", new[] { typeof(PacketWriter) }));
        AssertTranspile("ClientLobbyJoinResponseMessage.Deserialize",
            AccessTools.Method(typeof(ClientLobbyJoinResponseMessage), "Deserialize", new[] { typeof(PacketReader) }));
    }

    private static void AssertTranspile(string label, MethodBase method)
    {
        if (method == null) { Check($"{label} resolves", false, "method not found on sts2.dll"); return; }

        PacketSizePatch.ResetRewrittenListElementTypes();
        var before = PatchProcessor.GetOriginalInstructions(method);
        var after = PacketSizePatch.Transpile_SerializeDeserialize(before).ToList();

        int getRequiredBitsCalls = after.Count(c =>
            c.opcode == OpCodes.Call && c.operand is MethodInfo mi && mi.Name == nameof(PacketSizePatch.GetRequiredBits));
        AreEqual($"{label}: GetRequiredBits() call sites", 1, getRequiredBitsCalls);

        int leftoverThrees = 0, untouchedWide = 0;
        for (int i = 1; i < after.Count; i++)
        {
            if (after[i].operand is not MethodInfo mi) continue;
            if (mi.Name != "WriteList" && mi.Name != "ReadList") continue;
            if (after[i - 1].opcode == OpCodes.Ldc_I4_3) leftoverThrees++;
            if (after[i - 1].opcode == OpCodes.Ldc_I4_S && Convert.ToInt32(after[i - 1].operand) == 32) untouchedWide++;
        }
        AreEqual($"{label}: no vanilla 3 left before a list call", 0, leftoverThrees);
        AreEqual($"{label}: 32-bit list header untouched", 1, untouchedWide);

        var recorded = PacketSizePatch.RewrittenListElementTypes.Select(t => t.Name).ToList();
        AreEqual($"{label}: element types recorded ({string.Join(",", recorded)})", 1, recorded.Count);
    }
```

- [ ] **Step 2: Run it to make sure it fails**

Run: `dotnet run --project tests/PatchTests`
Expected: FAIL to compile — `'PacketSizePatch' does not contain a definition for 'ResetRewrittenListElementTypes'` and `...for 'RewrittenListElementTypes'`.

- [ ] **Step 3: Implement the collection in `PacketSizePatch.cs`**

Add these members immediately after the `_getRequiredBitsMethod` field:

```csharp
    /// <summary>
    /// Vanilla's hardcoded player-list header width. The only constant this transpiler
    /// rewrites, and the sentinel <see cref="ListHeaderWidthPatch"/> looks for at the leaf.
    /// </summary>
    public const int VanillaListBits = 3;

    private static readonly HashSet<Type> _rewrittenListElementTypes = new();

    /// <summary>
    /// The T of every WriteList&lt;T&gt;/ReadList&lt;T&gt; call whose vanilla 3-bit header this
    /// transpiler rewrote. Populated during <see cref="Apply"/>; read afterwards by
    /// <see cref="ListHeaderWidthPatch"/> to decide which generic instantiations need a leaf
    /// guard. Collected rather than hardcoded because the game renames this type between
    /// builds — it was LobbyPlayer, it is now StartRunLobbyPlayer.
    /// </summary>
    public static IReadOnlyCollection<Type> RewrittenListElementTypes => _rewrittenListElementTypes;

    /// <summary>Test seam: lets a test assert on one method's IL in isolation.</summary>
    internal static void ResetRewrittenListElementTypes() => _rewrittenListElementTypes.Clear();
```

Replace the constant-matching block inside `Transpile_SerializeDeserialize` — the `if (codes[i - 1].opcode == OpCodes.Ldc_I4_3) { ... } else if (...Ldc_I4_S...) { ... }` pair — with:

```csharp
            if (!IsVanillaListBitsConstant(codes[i - 1]))
                continue;

            codes[i - 1] = new CodeInstruction(OpCodes.Call, _getRequiredBitsMethod);
            patched++;

            // Record which list this was, so the leaf guard can cover the same instantiation.
            if (mi.IsGenericMethod)
                _rewrittenListElementTypes.Add(mi.GetGenericArguments()[0]);
```

Add this helper below `Transpile_SerializeDeserialize`:

```csharp
    // The compiler may emit the constant 3 as ldc.i4.3, ldc.i4.s 3, or ldc.i4 3.
    private static bool IsVanillaListBitsConstant(CodeInstruction code)
    {
        if (code.opcode == OpCodes.Ldc_I4_3) return true;
        if (code.opcode == OpCodes.Ldc_I4_S || code.opcode == OpCodes.Ldc_I4)
            return code.operand != null && Convert.ToInt32(code.operand) == VanillaListBits;
        return false;
    }
```

- [ ] **Step 4: Run the tests and make sure they pass**

Run: `dotnet run --project tests/PatchTests`
Expected: `ALL TESTS PASSED`, with each `element types recorded (...)` line naming `StartRunLobbyPlayer`.

- [ ] **Step 5: Commit**

```bash
git add PacketSizePatch.cs tests/PatchTests/Program.cs
git commit -m "$(cat <<'EOF'
refactor: record element types rewritten by the packet-size transpiler

The leaf guard needs to know which generic list instantiations carry a widened
header. Collecting them as the transpiler rewrites keeps the two in lockstep and
avoids naming a game type that the last update already renamed.

EOF
)"
```

---

### Task 4: `ListHeaderWidthPatch` — the leaf guard

Requirements **R2**, **R3**, **R6**, **R7**. On Harmony 2.4.2 this is testable end to end headlessly: the tests apply the guard for real and drive the leaves through reflection.

**Files:**
- Create: `ListHeaderWidthPatch.cs`
- Modify: `tests/PatchTests/PatchTests.csproj`
- Modify: `tests/PatchTests/Program.cs`

**Interfaces:**
- Consumes: `PacketSizePatch.VanillaListBits`, `PacketSizePatch.GetRequiredBits()`, `PacketSizePatch.RewrittenListElementTypes`, `PacketSizePatch.ResetRewrittenListElementTypes()` (Task 3).
- Produces:
  - `public static bool ListHeaderWidthPatch.TryWiden(ref int lengthBits)`
  - `public static void ListHeaderWidthPatch.Prefix_WriteList(ref int lengthBits)`
  - `public static void ListHeaderWidthPatch.Prefix_ReadList(ref int lengthBits)`
  - `public static void ListHeaderWidthPatch.Apply(Harmony harmony)`
  - `internal static Action<LogLevel, string> ListHeaderWidthPatch.LogSink`

- [ ] **Step 1: Create `ListHeaderWidthPatch.cs`**

Written before its tests because the tests need `LogSink` installed to run at all — the game logger segfaults headless.

```csharp
using System;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;

namespace Sts2Unlimited;

/// <summary>
/// Second line of defence for the widened list-length header (see <see cref="PacketSizePatch"/>).
///
/// PacketSizePatch transpiles LobbyBeginRunMessage.Serialize/Deserialize, but another mod can
/// make the JIT inline the *unpatched* IL of those methods into a Harmony replacement it builds
/// first: STS2-RitsuLib patches NetMessageBus.SerializeMessage&lt;LobbyBeginRunMessage&gt;, which
/// is generated before our initializer runs. Inlining copies IL, so a transpile applied
/// afterwards can never reach that copy. The host then writes a 3-bit header while the client
/// reads a 5-bit one, every following field is 2 bits out of alignment, and the client dies
/// deserializing a character model id.
///
/// PacketWriter.WriteList and PacketReader.ReadList are never inlined — they appear on the stack
/// even in the failing case — so guarding those leaves catches the inlined copy and the
/// transpiled one alike. The prefix rewrites only the vanilla constant 3, the same value the
/// transpiler targets, and takes the width from GetRequiredBits() so both paths stay in step.
/// </summary>
public static class ListHeaderWidthPatch
{
    /// <summary>
    /// Logging indirection. The game's logger calls into Godot natives and segfaults outside a
    /// game process, so the headless test harness swaps this for a capturing sink.
    /// </summary>
    internal static Action<LogLevel, string> LogSink =
        (level, message) => Log.LogMessage(level, LogType.Generic, message);

    private static bool _loggedWrite;
    private static bool _loggedRead;

    /// <summary>
    /// Widens a vanilla-width list header to the mod's wire width, returning true when it
    /// changed the value. Anything else is left alone: a transpiled call site already passes the
    /// widened value, and other widths (4, 32) belong to unrelated lists.
    /// </summary>
    public static bool TryWiden(ref int lengthBits)
    {
        if (lengthBits != PacketSizePatch.VanillaListBits) return false;

        int widened = PacketSizePatch.GetRequiredBits();
        if (widened == lengthBits) return false;

        lengthBits = widened;
        return true;
    }

    // Logged once per direction: this sits in the packet hot path, and one line is enough to
    // tell a bug reporter the inlining guard is doing something.
    public static void Prefix_WriteList(ref int lengthBits)
    {
        if (!TryWiden(ref lengthBits) || _loggedWrite) return;
        _loggedWrite = true;
        LogSink(LogLevel.Info,
            $"[ListHeaderWidthPatch] Corrected an inlined WriteList header {PacketSizePatch.VanillaListBits} -> {lengthBits}");
    }

    public static void Prefix_ReadList(ref int lengthBits)
    {
        if (!TryWiden(ref lengthBits) || _loggedRead) return;
        _loggedRead = true;
        LogSink(LogLevel.Info,
            $"[ListHeaderWidthPatch] Corrected an inlined ReadList header {PacketSizePatch.VanillaListBits} -> {lengthBits}");
    }

    /// <summary>
    /// Must run after <see cref="PacketSizePatch.Apply"/>: Harmony applies transpilers eagerly,
    /// so the element-type set is complete by the time that call returns.
    /// </summary>
    public static void Apply(Harmony harmony)
    {
        var writePrefix = new HarmonyMethod(typeof(ListHeaderWidthPatch).GetMethod(
            nameof(Prefix_WriteList), BindingFlags.Public | BindingFlags.Static)!);
        var readPrefix = new HarmonyMethod(typeof(ListHeaderWidthPatch).GetMethod(
            nameof(Prefix_ReadList), BindingFlags.Public | BindingFlags.Static)!);

        int patched = 0;
        foreach (var elementType in PacketSizePatch.RewrittenListElementTypes)
        {
            if (TryPatch(harmony, typeof(PacketWriter), "WriteList", elementType, writePrefix)) patched++;
            if (TryPatch(harmony, typeof(PacketReader), "ReadList", elementType, readPrefix)) patched++;
        }

        if (patched == 0)
            LogSink(LogLevel.Warn,
                "[ListHeaderWidthPatch] No vanilla-width list call sites were found — the inlining guard is inactive. " +
                "Joining may fail if another mod patches a lobby message's SerializeMessage.");
        else
            LogSink(LogLevel.Info,
                $"[ListHeaderWidthPatch] Guarded {patched} list methods at WireBits={PacketSizePatch.GetRequiredBits()}");
    }

    private static bool TryPatch(Harmony harmony, Type declaringType, string methodName, Type elementType, HarmonyMethod prefix)
    {
        try
        {
            // Generic instantiations must be patched individually — there is no single
            // definition to hook that covers every T.
            var target = AccessTools.Method(declaringType, methodName, null, new[] { elementType });
            if (target == null)
            {
                LogSink(LogLevel.Warn,
                    $"[ListHeaderWidthPatch] {declaringType.Name}.{methodName}<{elementType.Name}> not found — skipped");
                return false;
            }

            harmony.Patch(target, prefix: prefix);
            LogSink(LogLevel.Debug,
                $"[ListHeaderWidthPatch] Patched {declaringType.Name}.{methodName}<{elementType.Name}>");
            return true;
        }
        catch (Exception e)
        {
            LogSink(LogLevel.Warn,
                $"[ListHeaderWidthPatch] Failed to patch {declaringType.Name}.{methodName}<{elementType.Name}>: {e.Message}");
            return false;
        }
    }
}
```

- [ ] **Step 2: Compile it into the harness**

In `tests/PatchTests/PatchTests.csproj`, add below the existing `PacketSizePatch.cs` line:

```xml
    <Compile Include="../../ListHeaderWidthPatch.cs" Link="ListHeaderWidthPatch.cs" />
```

- [ ] **Step 3: Write the tests**

Add these usings to `tests/PatchTests/Program.cs`:

```csharp
using System.Collections;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Saves.Runs;
```

Add the capture list as the first field of the class, next to `_failures`:

```csharp
    private static readonly List<string> _log = new();
```

Replace the body of `Main` with this — **the ordering matters and is load-bearing**:

```csharp
    private static int Main()
    {
        // The game's logger segfaults outside a game process; capture instead.
        ListHeaderWidthPatch.LogSink = (level, message) => _log.Add($"{level}: {message}");

        RequiredBitsMath();
        TranspilerRewritesRealGameIl();
        PatchTargetsResolve();
        TryWidenSemantics();
        // Must run before the guard is applied — afterwards a vanilla-width read is corrected.
        UnguardedWireFormat();
        GuardWidensVanillaWidthCalls();
        GuardWarnsWhenThereIsNothingToGuard();

        Console.WriteLine(_failures == 0 ? "\nALL TESTS PASSED" : $"\n{_failures} TEST(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }
```

Then add:

```csharp
    // Apply() can only patch what AccessTools resolves, and the prefix only works while
    // lengthBits stays the parameter it is today. Both are game-version-sensitive.
    private static void PatchTargetsResolve()
    {
        foreach (var t in RecordVanillaWidthElementTypes())
        {
            var write = AccessTools.Method(typeof(PacketWriter), "WriteList", null, new[] { t });
            Check($"WriteList<{t.Name}> resolves", write != null);
            if (write != null)
                AreEqual($"WriteList<{t.Name}> arg 1", "lengthBits", write.GetParameters()[1].Name);

            var read = AccessTools.Method(typeof(PacketReader), "ReadList", null, new[] { t });
            Check($"ReadList<{t.Name}> resolves", read != null);
            if (read != null)
                AreEqual($"ReadList<{t.Name}> arg 0", "lengthBits", read.GetParameters()[0].Name);
        }
    }

    // Mirrors PacketSizePatch.Apply's discovery across the whole game assembly, without calling
    // Apply itself (which logs, and the game logger segfaults here). If a future build adds
    // another vanilla-width list, the count assertion below names it as a failure.
    private static List<Type> RecordVanillaWidthElementTypes()
    {
        PacketSizePatch.ResetRewrittenListElementTypes();

        // Headless, ~41 game types fail to load (Steamworks.NET is absent), and GetTypes()
        // throws rather than returning the rest. None of them are serialization messages —
        // an IL-level scan of the assembly finds all four vanilla-width call sites in the two
        // lobby message types, both of which load fine.
        Type[] gameTypes;
        try { gameTypes = typeof(PacketWriter).Assembly.GetTypes(); }
        catch (ReflectionTypeLoadException e) { gameTypes = e.Types.Where(t => t != null).ToArray(); }

        int scanned = 0;
        foreach (var type in gameTypes)
        {
            foreach (var m in new[]
            {
                type.GetMethod("Serialize", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(PacketWriter) }, null),
                type.GetMethod("Deserialize", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(PacketReader) }, null),
            })
            {
                if (m == null) continue;
                try
                {
                    PacketSizePatch.Transpile_SerializeDeserialize(PatchProcessor.GetOriginalInstructions(m)).ToList();
                    scanned++;
                }
                catch { /* a method whose IL will not resolve cannot be patched in-game either */ }
            }
        }

        // 257 on the current build; a large drop means type loading broke, not that the game
        // shrank, and would make the count assertion below meaningless.
        Check("swept the game's serialization methods", scanned > 200, $"only {scanned} were readable");

        var types = PacketSizePatch.RewrittenListElementTypes.ToList();
        AreEqual($"vanilla-width list element types across sts2.dll ({string.Join(",", types.Select(t => t.Name))})",
            1, types.Count);
        return types;
    }

    // R2/R3: only the vanilla 3 moves. An already-transpiled call site passes 5 and must not be
    // widened twice; 4 and 32 belong to unrelated lists.
    private static void TryWidenSemantics()
    {
        int three = 3;
        Check("TryWiden(3) reports a change", ListHeaderWidthPatch.TryWiden(ref three));
        AreEqual("TryWiden(3) widens to the wire width", 5, three);

        foreach (int untouched in new[] { 4, 5, 32 })
        {
            int value = untouched;
            Check($"TryWiden({untouched}) reports no change", !ListHeaderWidthPatch.TryWiden(ref value));
            AreEqual($"TryWiden({untouched}) leaves the value alone", untouched, value);
        }
    }

    // The real wire format before any guard is applied: a widened header costs exactly 5 bits,
    // and a peer reading it at the vanilla width lands 2 bits short — the desync in the report.
    // Only an empty list works headlessly; the element type's Serialize needs game state.
    private static void UnguardedWireFormat()
    {
        var elementType = PacketSizePatch.RewrittenListElementTypes.First();

        var writer = WriteEmptyList(elementType, PacketSizePatch.GetRequiredBits());
        AreEqual("widened empty-list header costs 5 bits", 5, writer.BitPosition);

        var matched = new PacketReader();
        matched.Reset(writer.Buffer);
        AreEqual("matched read returns an empty list", 0, ReadList(matched, elementType, PacketSizePatch.GetRequiredBits()).Count);
        AreEqual("matched read consumes 5 bits", 5, matched.BitPosition);

        var mismatched = new PacketReader();
        mismatched.Reset(writer.Buffer);
        ReadList(mismatched, elementType, PacketSizePatch.VanillaListBits);
        AreEqual("vanilla-width read desyncs by 2 bits", 3, mismatched.BitPosition);
    }

    // The fix itself, end to end: after Apply, a caller passing the vanilla constant — exactly
    // what an inlined, un-transpiled Serialize does — still produces a widened header.
    private static void GuardWidensVanillaWidthCalls()
    {
        var elementType = PacketSizePatch.RewrittenListElementTypes.First();
        _log.Clear();
        ListHeaderWidthPatch.Apply(new Harmony("sts2unlimited.tests.listheader"));
        Check("Apply logs what it guarded", _log.Any(l => l.Contains("Guarded")), string.Join(" | ", _log));

        var writer = WriteEmptyList(elementType, PacketSizePatch.VanillaListBits);
        AreEqual($"guarded WriteList<{elementType.Name}> widens a vanilla-width call", 5, writer.BitPosition);

        var reader = new PacketReader();
        reader.Reset(writer.Buffer);
        AreEqual("guarded round trip returns an empty list", 0, ReadList(reader, elementType, PacketSizePatch.VanillaListBits).Count);
        AreEqual($"guarded ReadList<{elementType.Name}> widens a vanilla-width call", 5, reader.BitPosition);

        // The guard must not leak onto other element types. SerializableModifier lists are
        // written at 32 in the game, so a 3 here has to stay 3.
        var control = new PacketWriter();
        control.Reset();
        control.WriteList(new List<SerializableModifier>(), PacketSizePatch.VanillaListBits);
        AreEqual("an unguarded element type keeps the vanilla width", 3, control.BitPosition);
    }

    // R7: a future build where nothing matches must say so rather than fail silently.
    private static void GuardWarnsWhenThereIsNothingToGuard()
    {
        PacketSizePatch.ResetRewrittenListElementTypes();
        _log.Clear();
        ListHeaderWidthPatch.Apply(new Harmony("sts2unlimited.tests.empty"));
        Check("an empty type set logs a warning",
            _log.Any(l => l.StartsWith("Warn") && l.Contains("inactive")), string.Join(" | ", _log));
    }

    // Drives the leaves by reflection so no test names the element type.
    private static PacketWriter WriteEmptyList(Type elementType, int lengthBits)
    {
        var writer = new PacketWriter();
        writer.Reset();
        var list = Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType));
        AccessTools.Method(typeof(PacketWriter), "WriteList", null, new[] { elementType })
            .Invoke(writer, new object[] { list, lengthBits });
        return writer;
    }

    private static ICollection ReadList(PacketReader reader, Type elementType, int lengthBits)
        => (ICollection)AccessTools.Method(typeof(PacketReader), "ReadList", null, new[] { elementType })
            .Invoke(reader, new object[] { lengthBits });
```

- [ ] **Step 4: Run the tests and make sure they pass**

Run: `dotnet run --project tests/PatchTests`
Expected: `ALL TESTS PASSED`. In particular `guarded WriteList<StartRunLobbyPlayer> widens a vanilla-width call` and `an unguarded element type keeps the vanilla width` must both pass — together they are the fix and its blast radius.

Exit 139 with no output means something reached the game logger; check that `Main` installs `LogSink` before anything else.

- [ ] **Step 5: Verify the mod still builds**

Run: `dotnet build --configuration ExportRelease`
Expected: `0 Error(s)`.

- [ ] **Step 6: Commit**

```bash
git add ListHeaderWidthPatch.cs tests/PatchTests/PatchTests.csproj tests/PatchTests/Program.cs
git commit -m "$(cat <<'EOF'
fix: guard list header width at the leaf, not the call sites

WriteList/ReadList are never inlined, so a prefix there catches both the copy a
peer mod inlined before our transpile ran and the transpiled original.

EOF
)"
```

---

### Task 5: Wire the guard into mod startup

Requirement **R5**. The guard exists and is tested but nothing calls it yet.

**Files:**
- Modify: `Sts2Unlimited.cs:294`

**Interfaces:**
- Consumes: `ListHeaderWidthPatch.Apply(Harmony)` from Task 4.
- Produces: nothing — this is the last wiring step.

- [ ] **Step 1: Call the guard from `ApplyHarmonyPatches`**

In `Sts2Unlimited.cs`, replace the `PacketSizePatch.Apply(harmony);` line and its comment with:

```csharp
			// Patch packet list serialization to support more than 7 items (3-bit limit)
			PacketSizePatch.Apply(harmony);

			// Guard the same headers at PacketWriter.WriteList / PacketReader.ReadList. Another
			// mod (STS2-RitsuLib) patches NetMessageBus.SerializeMessage<LobbyBeginRunMessage>
			// before we load, which makes the JIT inline the unpatched Serialize into its
			// replacement — a copy the transpiler above can never reach. Must run after
			// PacketSizePatch.Apply, which is what populates RewrittenListElementTypes.
			ListHeaderWidthPatch.Apply(harmony);
```

- [ ] **Step 2: Verify the ordering is real**

Confirm by reading `Sts2Unlimited.cs` that `ListHeaderWidthPatch.Apply` is called after `PacketSizePatch.Apply` and inside the same `try` block. If it is outside the `try`, a throw would escape `ApplyHarmonyPatches` — move it inside.

- [ ] **Step 3: Run the tests and build the mod**

Run: `dotnet run --project tests/PatchTests`
Expected: `ALL TESTS PASSED`.

Run: `dotnet build --configuration ExportRelease`
Expected: `0 Error(s)`.

- [ ] **Step 4: Commit**

```bash
git add Sts2Unlimited.cs
git commit -m "$(cat <<'EOF'
fix: apply the list-header leaf guard at mod startup

Runs after PacketSizePatch.Apply so the collected element types are complete.

EOF
)"
```

---

### Task 6: Document, verify in-game, open the PR

The harness proves the mechanism; only the game proves the RitsuLib interaction.

**Files:**
- Modify: `README.md` (the "How It Works" section, after the patch table)

**Interfaces:**
- Consumes: nothing.
- Produces: nothing.

- [ ] **Step 1: Add the compatibility note**

In `README.md`, insert after the patched-method table and before the "Settings are persisted…" line:

```markdown
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
```

- [ ] **Step 2: Build the release bundle**

Run: `dotnet build --configuration ExportRelease`
Expected: `0 Error(s)` and `✓ Bundled mod files to: .../release`.

- [ ] **Step 3: Verify in-game — Sts2Unlimited alone**

Install the `release/` output into the game's mods folder, launch, host a lobby, join with a second client.
Expected: the client joins and starts a run. The log contains `[PacketSizePatch] Patched N types` and `[ListHeaderWidthPatch] Guarded 2 list methods at WireBits=5`.
Expected: **no** `Corrected an inlined ... header` line — nothing is inlining without RitsuLib, so the guard should stay quiet.

- [ ] **Step 4: Verify in-game — with STS2-RitsuLib installed**

Install STS2-RitsuLib alongside, host, and join with a client.
Expected: the client joins and plays. The log contains `[ListHeaderWidthPatch] Corrected an inlined WriteList header 3 -> 5`, and zero `ModelNotFoundException`.

- [ ] **Step 5: Verify in-game — more than 7 players**

Set Max Players above 8, seat 8+ players in a lobby, and begin the run.
Expected: the run starts for everyone — the original >7-player fix is unregressed.

- [ ] **Step 6: Commit**

```bash
git add README.md
git commit -m "$(cat <<'EOF'
docs: explain the packet header width and the inlining guard
EOF
)"
```

- [ ] **Step 7: Open the PR**

Only after Steps 3–5 have actually been run. If any in-game check could not be run, say so plainly in the PR body rather than implying it passed.

```bash
git push -u origin fix/list-header-width-inlining
gh pr create --title "fix: clients can't join when STS2-RitsuLib is also installed" --body "$(cat <<'EOF'
## Problem

With Sts2Unlimited and STS2-RitsuLib both installed, no client can join: the host
writes a 3-bit lobby player-list header while the client reads a 5-bit one, so every
field after it is 2 bits out of alignment and the client dies on
`ModelNotFoundException` decoding a character model id.

RitsuLib patches `NetMessageBus.SerializeMessage<LobbyBeginRunMessage>` before this mod
loads. The JIT inlines the still-unpatched `LobbyBeginRunMessage.Serialize` into that
replacement, and inlining copies IL — so our transpile can never reach that copy.

## Fix

Guard the leaves. `PacketWriter.WriteList` / `PacketReader.ReadList` are never inlined,
so a prefix there covers the inlined copy and the transpiled original alike. The element
types to guard are collected by the existing transpiler as it rewrites, so the two can
never cover different sets and no code names a game type — the game renamed
`LobbyPlayer` to `StartRunLobbyPlayer` between builds, which is exactly the failure mode
a hardcoded name would have hit.

Also bumps Lib.Harmony to 2.4.2 to match the `0Harmony.dll` the game ships; 2.3.3 cannot
patch at all on .NET 9 or 10.

## Verification

- Headless harness (`dotnet run --project tests/PatchTests`): transpiler behaviour
  against the real IL of all four vanilla call sites, an assembly-wide sweep, patch
  target resolution, wire-format header widths, and the guard applied for real —
  including a control asserting it does not widen other element types.
- In-game: joins with RitsuLib installed, joins without it, and 8+ player lobbies.

See `docs/superpowers/specs/2026-09-06-lobby-list-header-inlining.md` for the full
diagnosis and the reporter's evidence.
EOF
)"
```

---

## Notes for the executor

- **Never hardcode the element type in production code.** It was `LobbyPlayer` in the March build and is `StartRunLobbyPlayer` now. Tests may print the name; they must not assert on it.
- **Never let the harness reach `Log.LogMessage`.** It segfaults (exit 139, no stack). That means: install `ListHeaderWidthPatch.LogSink` first thing in `Main`, and never call `PacketSizePatch.Apply` from a test.
- **Do not remove the transpiler.** It is still the primary mechanism and the source of the element-type set; the leaf guard is a backstop for the inlined path only.
- **Do not bump `Sts2Unlimited.json`.** The release workflow derives the version from conventional commit messages and commits the bump itself.
- If `dotnet run --project tests/PatchTests` cannot find .NET 9, confirm `<RollForward>Major</RollForward>` is in the test csproj.
- If it fails to load `Sentry.Godot`, confirm both Sentry DLLs are at the repo root and `<Reference>`d — merely present is not enough, .NET probes only what `deps.json` lists.
