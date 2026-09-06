# Spec: lobby list-header width survives another mod inlining vanilla IL

**Date:** 2026-09-06
**Status:** accepted
**Origin:** user bug report — "Clients can't join when STS2-RitsuLib is also installed"

## Problem

With Sts2Unlimited **and** STS2-RitsuLib both installed, no client can join a
multiplayer game. The client throws in the lobby and never enters the run:

```
ModelNotFoundException: Model id=CHARACTER.SLIMEBOSS-FORM_OF_WALL not found
  at StartRunLobbyPlayer.Deserialize_Patch1
  at PacketReader.ReadList[T]
  at LobbyBeginRunMessage.Deserialize_Patch1
```

The host had sent `CHARACTER.AUTOMATON-AUTOMATON`. Either mod on its own works fine.

## Cause (as diagnosed by the reporter)

`PacketSizePatch` widens the player-list length header from 3 bits to 5 by
transpiling `LobbyBeginRunMessage.Serialize` and `.Deserialize`.

RitsuLib separately patches `NetMessageBus.SerializeMessage<LobbyBeginRunMessage>`
to append its own data. That makes Harmony build a replacement method for that
generic instantiation at RitsuLib's load time, which is *before* our initializer
runs. When the JIT compiles that replacement it inlines the still-unpatched
`LobbyBeginRunMessage.Serialize` into it. Inlining copies IL, so the transpile we
apply afterwards can never reach that copy.

The host ends up writing a 3-bit header while the client reads a 5-bit one. Every
field after the header is 2 bits out of alignment, so the character model id decodes
to an unrelated entry and the client dies.

### Reporter's evidence

Logging `lengthBits` and the caller chain on both peers:

```
HOST    WriteList lengthBits=3
        <- NetMessageBus.SerializeMessage_Patch1
        <- NetHostGameService.SendMessage
        <- StartRunLobby.BeginRunForAllPlayers_Patch2
        LobbyBeginRunMessage.Serialize is ABSENT from the stack (inlined)

CLIENT  ReadList  lengthBits=5
        <- LobbyBeginRunMessage.Deserialize_Patch2
        <- NetMessageBus.TryDeserializeMessage_Patch1
        Deserialize IS in the stack, so the patched version runs
```

Ruled out: both peers' `ModelIdSerializationCache` are byte-identical (3609 entries,
hash 2067741535); the packet is byte-identical leaving the host and arriving at the
client (len 469, FNV 5C996CBE); the client starts the body read at the correct bit 72.
Nothing is corrupt — the two sides simply disagree on the header width.

`ClientLobbyJoinResponseMessage` carries the same player list and works correctly in
the same session, because RitsuLib does not patch *its* `SerializeMessage`. Only the
message RitsuLib hooks is affected.

## Findings verified against `sts2.dll` (2026-09-06)

The repo's reference `sts2.dll` was stale (a March build). It has been refreshed from
the live install
(`<steam>/steamapps/common/Slay the Spire 2/data_sts2_windows_x86_64/sts2.dll`,
9757184 bytes, md5 `a91f9e7be785f504205313f3aff55854`). Everything below is verified
against that current assembly. The mod builds against it with `0 Error(s)` — no other
patch site in the mod was broken by the game update.

The reporter's diagnosis holds in full. Four findings shape the implementation:

1. **The element type is `StartRunLobbyPlayer` — and it used to be `LobbyPlayer`.**
   The stale March assembly had no `StartRunLobbyPlayer` at all; the current one has no
   `LobbyPlayer`. MegaCrit renamed the type between builds. The reporter was right, and
   the previous reference DLL was simply out of date.

   → This is the strongest possible argument for the design: **nothing may hardcode the
   element type.** A patch naming either type breaks on the other build. The transpiler
   records whatever `T` it rewrote a 3 for, and the leaf guard covers exactly that.

2. **There are exactly four 3-bit list call sites**, all carrying
   `List<StartRunLobbyPlayer>`:

   | lengthBits | call | in |
   |---|---|---|
   | 3 | `WriteList<StartRunLobbyPlayer>` | `LobbyBeginRunMessage.Serialize` |
   | 3 | `ReadList<StartRunLobbyPlayer>` | `LobbyBeginRunMessage.Deserialize` |
   | 3 | `WriteList<StartRunLobbyPlayer>` | `ClientLobbyJoinResponseMessage.Serialize` |
   | 3 | `ReadList<StartRunLobbyPlayer>` | `ClientLobbyJoinResponseMessage.Deserialize` |

   Each of those four methods also has exactly one `ldc.i4.s 32` before a
   `WriteList`/`ReadList` of `SerializableModifier`. Every other list site in the
   assembly passes 32, except `MapDrawingMessage` which passes 4. So widening "the
   constant 3" is unambiguous.

3. **`StartRunLobbyPlayer` is a struct** (`valueType=True`). Generic instantiations
   over value types get their own JIT'd code, so patching
   `WriteList<StartRunLobbyPlayer>` is precisely scoped — it cannot leak onto other
   element types the way a reference-type instantiation (shared `__Canon` code) would.
   Verified empirically: with the leaf patched, a `List<SerializableModifier>` written
   at `lengthBits: 3` still emits a 3-bit header and the prefix does not fire.

4. **The game ships Harmony 2.4.2**, in
   `data_sts2_windows_x86_64/0Harmony.dll`, while the mod referenced and bundled 2.3.3.
   Harmony 2.3.3 cannot patch *anything* on .NET 9 or 10 — `harmony.Patch` throws
   `MemberAccessException: Cannot create an instance of
   System.Reflection.Emit.LocalBuilder because it is an abstract class` — so in-game
   the mod has almost certainly been binding to the game's 2.4.2 all along. The mod is
   bumped to `Lib.Harmony 2.4.2` to match what actually runs. This also makes the leaf
   guard testable headlessly (see below).

Method signatures, confirmed from the assembly:

```
public void PacketWriter.WriteList<T>(IReadOnlyList<T> list, int lengthBits)
public List<T> PacketReader.ReadList<T>(int lengthBits)
```

## Fix

Patch the leaf rather than the callers. `WriteList` and `ReadList` are never inlined
— they appear in the stack even in the failing case — so both the inlined copy and
the transpiled copy funnel through them and cannot disagree.

A Harmony prefix on `PacketWriter.WriteList<T>` and `PacketReader.ReadList<T>`
rewrites `lengthBits == 3` to `PacketSizePatch.GetRequiredBits()`. It only rewrites
the vanilla 3, the same constant the existing transpiler targets, and takes the width
from `GetRequiredBits()` rather than hardcoding 5, so it stays in step with whatever
the setting is. An already-transpiled call site passes 5 and is left untouched.

### Requirements

- **R1** — The element types to guard are collected by the existing transpiler as it
  rewrites, not hardcoded. Whatever `T` the transpiler rewrote a 3 for gets a leaf
  prefix. This survives game updates that rename or add player-list types, and it
  guarantees the leaf guard and the transpiler can never cover different sets.
- **R2** — The prefix rewrites only the vanilla constant 3. Widths 4 and 32 are
  intentional and must pass through unchanged.
- **R3** — The prefix is idempotent with the transpiler: on a transpiled call site it
  sees the already-widened value and does nothing.
- **R4** — The wire width still comes from `PacketSizePatch.GetRequiredBits()` (fixed
  at 5, derived from `AbsoluteMaxPlayers = 16`), never from the local
  `MaxPlayersOverride` — peers with different local settings must agree on the format.
- **R5** — `ListHeaderWidthPatch.Apply` runs after `PacketSizePatch.Apply`, so the
  collected type set is complete. Harmony applies transpilers eagerly at `Patch()`
  time, which is what makes the ordering sufficient.
- **R6** — Failure to resolve or patch any single instantiation is logged and
  skipped, never thrown — consistent with every other patch site in this mod.
- **R7** — If the collected set comes back empty (a future game build with different
  constants), log a warning: the guard is inactive and the RitsuLib interaction would
  regress silently otherwise.
- **R8** — The mod references `Lib.Harmony 2.4.2`, matching the `0Harmony.dll` the game
  ships. 2.3.3 cannot patch on the runtime the game uses.

### Known limits

- Generic instantiations must be patched individually. This covers exactly the
  element types the transpiler rewrote — `List<StartRunLobbyPlayer>` in this build,
  which is what the lobby handshake carries.
- A mod loaded *after* our initializer that introduces a new message with a 3-bit
  list header gets neither the transpile nor the leaf guard. Nothing can be done
  about that from here.
- The fix does not depend on RitsuLib being present, and does nothing when it is not.

## Verification environment

All verified by prototype before this spec was written:

- **On Harmony 2.4.2 the whole fix is testable headlessly**, including live patching.
  Patching `WriteList<StartRunLobbyPlayer>` and writing a list at `lengthBits: 3`
  produces a 5-bit header, and the same call on `List<SerializableModifier>` is
  untouched.
- On Harmony 2.3.3 nothing can be patched on either installed runtime (.NET 9.0.19,
  10.0.11) — `MemberAccessException` on `LocalBuilder`, reproducible even for a trivial
  local method. This is why the mod is being bumped to 2.4.2 (finding 4 above).
- Also works headlessly against the real `sts2.dll`:
  - `PatchProcessor.GetOriginalInstructions(method)` — reads real game IL, so the
    transpiler is testable against the actual call sites.
  - `AccessTools.Method(type, name, null, new[] { elementType })` — resolves the closed
    generic, so patch-target resolution is testable.
  - `new PacketWriter()` / `new PacketReader()` with an **empty** list — so header
    widths are testable on the real wire format.
- **`sts2.dll` now has a module initializer that runs `SentryAutoInit.Init()` on first
  type access.** Loading it headless therefore needs `Sentry.Godot.dll` and
  `Sentry.dll` — *referenced*, not merely present, since .NET probes only what is in
  `deps.json`. With them referenced it prints
  `SentryGodotInitializer: Sentry GDExtension not loaded in this process; skipping` and
  carries on. Both are copied next to `sts2.dll` and gitignored alongside it.
- Test projects must set `<RollForward>Major</RollForward>`: `sts2.dll` forces a
  `net9.0` target and only .NET 10 is installed system-wide.
- A **non-empty** `List<StartRunLobbyPlayer>` cannot be serialized headlessly —
  `StartRunLobbyPlayer.Serialize` throws `NullReferenceException` without game state
  loaded. Do not try to build a full-payload round-trip test.

## Acceptance

- Host and client both use a 5-bit player-list header with RitsuLib installed; a
  client joins and plays. `ModelNotFoundException` count: 0.
- Sts2Unlimited alone still works, including >7 player lobbies.
- Vanilla (no Sts2Unlimited) behaviour is untouched.
