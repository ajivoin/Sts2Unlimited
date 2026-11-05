# Spec: the Difficulty Scaling override is client-local and desyncs multiplayer

**Date:** 2026-11-04
**Status:** accepted
**Origin:** user bug report — "each player's Difficulty Scaling value stays local and isn't synced"

## Problem

With the Difficulty Scaling override enabled, each player's value stays on their own
PC. If two peers hold different values, monsters get different HP on each machine and
the game disconnects the whole lobby with a State Divergence at the first fight.

Reported against Unlimited v1.0.1 on game v0.107.1 (also loaded: BaseLib v3.4.7,
Limitless v1.1.7, BetterSpire2, CampfireTrade, Big Deck Optimization v0.12.0 — none
of which are needed to reproduce).

```
[Sts2Unlimited] Difficulty Scaling slider configured: range [1,16], current=4
[ERROR] State divergence message received for player <friend> checksum ID 1! (We are Client <me>)
Context: After player turn start. Local: 4289309800. Remote: 2782095496.

LOCAL STATE DUMP
Creature with monster ID: MONSTER.SHRINKER_BEETLE player ID:
Current HP: 171 Max HP: 171 Block: 0 Power count: 0

REMOTE STATE DUMP
Creature with monster ID: MONSTER.SHRINKER_BEETLE player ID:
Current HP: 214 Max HP: 214 Block: 0 Power count: 0

[INFO] [RunLobby] Disconnected. Reason: StateDivergence
```

The reporter asked for the host's value to apply to everyone, or failing that, a
warning when values differ.

## Cause

The override is a local preference with no network transport anywhere in its path:

- `Sts2Unlimited.GetEffectivePlayerCount` (`Sts2Unlimited.cs:35`) returns
  `DifficultyPlayersOverride`.
- That value is loaded from each PC's own `sts2unlimited.settings.json`
  (`Sts2Unlimited.cs:70`) and written by that PC's own slider
  (`SettingsMenuIntegration.cs:282`).
- It reaches exactly two live gameplay paths, both of which land in checksummed
  combat state:
  1. `DifficultyPatch.Prefix_ScaleHpForMultiplayer` (`DifficultyPatch.cs:143`)
     rewrites the `playerCount` argument of `Creature.ScaleHpForMultiplayer` and
     `Creature.ScaleMonsterHpForMultiplayer` → monster Max/Current HP.
  2. the `Players.Count` transpiler applied to
     `MultiplayerScalingModel.ModifyBlockMultiplicative` (`DifficultyPatch.cs:70-73`)
     → monster Block.
- `grep` for sync/rpc/serialize across the difficulty path returns nothing.

This is structural, not a missed call site. Every caller of the HP helpers computes
locally on every peer — verified by scanning the IL of `sts2.dll` for callers:

```
CombatState.CreateCreature                    -> ScaleMonsterHpForMultiplayer
Creature.ScaleMonsterHpForMultiplayer         -> ScaleHpForMultiplayer
DecimillipedeSegment.AfterAddedToRoom         -> ScaleHpForMultiplayer
TestSubject.Revive                            -> ScaleHpForMultiplayer
ToughEgg.Hatch                                -> ScaleHpForMultiplayer
BattlewornDummy.GenerateInitialOptions        -> ScaleHpForMultiplayer
```

The game never replicates monster HP. It relies on every peer deriving the same
number from the same replicated player list, and gates that with `ChecksumTracker`.
Feeding that math a per-client preference desyncs by construction.

### Second divergence vector beyond the report

Monster **Block** diverges the same way, through
`MultiplayerScalingModel.ModifyBlockMultiplicative`. The reporter's dump happened to
show `Block: 0`, so it went unnoticed. Both vectors are fixed by the same change —
both read the override through `GetEffectivePlayerCount`.

### Not affected

- **Max Players** — host-side only, and the wire width is fixed at 16 for all peers.
- **`ChestPatch`** — reads the real `RunState.Players.Count`, never the override.
- **Power scaling** (`GetScaledAmountForMultiplayer`) — patched for forward
  compatibility; no live caller in this build.

## Findings verified against `sts2.dll`

Verified against the current reference assembly (9757184 bytes, md5
`a91f9e7be785f504205313f3aff55854`) — the same build the 2026-09-06 spec recorded.
Everything in this section was read out of that assembly's IL, not assumed.

### F1. The HP scaling formula

```
Creature.ScaleHpForMultiplayer(decimal hp, EncounterModel encounter,
                               int playerCount, int actIndex)      // static
    playerCount <= 1  ->  hp
    otherwise         ->  hp * playerCount * GetMultiplayerScaling(encounter, actIndex)

MultiplayerScalingModel.GetMultiplayerScaling(encounter, actIndex)  // static
    actIndex == 0  ->  1.1
    actIndex == 1  ->  1.2
    actIndex == 2  ->  encounter?.RoomType == 3 ? 1.3 : 1.2
    otherwise      ->  throw ArgumentOutOfRangeException("actIndex", ...)
```

`Creature.SetMaxHpInternal` converts the decimal with an **explicit cast**, i.e. it
truncates toward zero, then clamps to 999999999.
`ScaleMonsterHpForMultiplayer` early-returns when `playerCount == 1` — which is why
the mod's prefix, which rewrites `playerCount` *before* that check, is what makes the
singleplayer challenge mode work at all.

### F2. The reported numbers are reproduced exactly

`ShrinkerBeetle.MinInitialHp`/`MaxInitialHp` are 38–40 (40–42 at ascension ≥ 8), so
both peers rolled the same base HP from the synced seed and then multiplied by
different player counts:

| base HP | playerCount | act 0 scaling | product | truncated | matches |
|---|---|---|---|---|---|
| 39 | 4 | 1.1 | 171.6 | **171** | reporter's Local |
| 39 | 5 | 1.1 | 214.5 | **214** | reporter's Remote |
| 38 | 4 / 5 | 1.1 | 167.2 / 209.0 | 167 / 209 | — |
| 40 | 4 / 5 | 1.1 | 176.0 / 220.0 | 176 / 220 | — |

No other base-and-count pair in range yields both reported numbers, and no act-1 or
act-2 multiplier can (171 / 1.2 = 142.5 is not an integer product). So the divergence
was: reporter's effective count **4** (matching their logged `current=4`), friend's
effective count **5**, in **Act 1**, consistent with "right at the start of the first
fight."

### F3. The game supports mod-defined network messages as a first-class seam

```
MessageTypes.Initialize()
    types  = INetMessageSubtypes.All
    types += ReflectionHelper.GetSubtypesInMods()
    _cache = new NetTypeCache<INetMessage>(types)

NetTypeCache<T>..ctor(List<Type> types)
    ContentSorter<string>.Sort(types, getId, affectsGameplayAtEnd: true)
```

So a mod-defined `INetMessage` is picked up by the game's id registry automatically.

### F4. Send and receive are available to a mod

```
INetGameService.SendMessage<T>(T message)                 // broadcast
INetGameService.SendMessage<T>(T message, ulong playerId) // targeted
INetGameService.RegisterMessageHandler<T>(MessageHandlerDelegate<T>)
NetMessageBus.SendMessageToAllHandlers(INetMessage message, ulong senderId)
```

`StartRunLobby` and `LoadRunLobby` each expose the live service as a **public
`NetService` property**, reachable from a Harmony patch via `__instance`.

### F4b. An unknown message id is tolerated when modded, not fatal

`NetMessageBus.TryDeserializeMessage` reads the message id as a **single byte** (so there is a
hard ceiling of 256 message types across the game and every loaded mod), and when
`MessageTypes.TryGetMessageType` fails it checks `ModManager.IsRunningModded()`. For a modded
peer with an out-of-range id it logs once —

> "Received message with length … and first byte … that is outside the bounds of our known
> messages (…). Since we are modded, we are assuming this is a message that does not affect
> gameplay and will not warn about this again."

— and returns false. It does **not** disconnect. This materially reduces risk A1: a peer that
does not know our message simply ignores it and lands in the designed fallback path rather than
dropping the lobby.

The instance itself is created with `Activator.CreateInstance(type)` and then deserialized
through `IPacketSerializable.Deserialize` on the resulting box.

### F5. The `INetMessage` contract to mirror

`LobbySeedChangedMessage` implements `INetMessage` + `IPacketSerializable` and returns
`ShouldBroadcast = true`, `Mode = NetTransferMode.Reliable (2)`, `ShouldBuffer = true`,
`LogLevel = 0` (i.e. `VeryDebug`). `LobbyBeginRunMessage` returns the same four values.

Every vanilla message is a **sealed struct**, which together with F4b's
`Activator.CreateInstance` + interface-dispatched `Deserialize` is why `DifficultySyncMessage` is
declared a struct: the box the handlers receive is the one `Deserialize` wrote into.

### F6. Run-start and rejoin entry points

```
StartRunLobby.BeginRunForAllPlayers(string seed, List<> modifiers)   // host, new run
StartRunLobby.BeginRunLocally(string seed, List<> modifiers)         // host, local
StartRunLobby.HandleLobbyBeginRunMessage(LobbyBeginRunMessage, ulong)        // client
LoadRunLobby.TryBeginRunForAllPlayers() / BeginRunLocally()          // host, saved run
LoadRunLobby.HandleLobbyBeginRunMessage(LobbyBeginLoadedRunMessage, ulong)   // client
RunLobby.HandleClientRejoinRequestMessage(ClientRejoinRequestMessage, ulong) // host, mid-run
RunLobby.OnConnectedToClientAsHost(ulong)                            // host, peer attached
```

`HandleClientRejoinRequestMessage` and `OnConnectedToClientAsHost` exist in the
assembly but no reachable flow drives them: the game offers no way to rejoin a run in
progress. Reconnection is the host leaving the run and re-inviting, which is the saved-run
path. The seams below are listed as they appear in the assembly, not as live entry points.

Three lobby classes, not two, and the distinction matters for the rejoin seam:
`StartRunLobby` is the pre-run lobby for a fresh run, `LoadRunLobby` the pre-run lobby
for a saved run, and **`RunLobby` the in-run lobby** — it owns `AbandonRun`,
`PlayerRejoined`, and the rejoin handler that fires while a run is live. Both pre-run
classes expose the service as a public `NetService` property; `RunLobby` exposes it
only as the **private field `_netService`**, so that patch must reach it with
`AccessTools.Field` rather than `__instance.NetService`.

### F7. `PacketReader` can bound a tail read

`PacketReader` exposes settable `BitPosition` and `Buffer`, so appending an optional
tail to a vanilla message *could* be made backward compatible. Recorded because it is
the fallback if F3/F4 disappoint in-game — not the chosen design.

## Design

**The host's value becomes authoritative for the whole lobby, carried by a
mod-defined network message, with vanilla scaling as the fallback when no host value
arrives.**

### Chosen approach, and what was rejected

| Approach | Verdict |
|---|---|
| Mod-defined `INetMessage` broadcast at run start | **Chosen.** Touches no vanilla message, changes no bit layout, uses the seams in F3/F4. |
| Optional tail appended to `LobbyBeginRunMessage` | Rejected. Mutates a vanilla message other mods also patch — `ListHeaderWidthPatch` documents RitsuLib patching exactly this one — and relies on old readers tolerating trailing bits. Same outcome, strictly more risk. Kept as the documented fallback (F7). |
| Carry the value as a run modifier (`SerializableModifier`) | Rejected. Needs content registration, localization and an icon, and model ids encode against registered-content counts. A large blast radius to carry one integer. |

### Components

**`DifficultySyncMessage.cs` (create)** — the wire contract. Implements `INetMessage`
+ `IPacketSerializable` with the F5 property values. Payload is two ints:

- `sentinel` — a fixed magic constant, validated on receipt.
- `effectiveCount` — the host's resolved player count for scaling; `0` means the host
  has no override active.

The sentinel guards the one real hazard of this approach: mod-added message ids are
positional (F3), so a peer running a *different* set of message-adding mods could
route a foreign message into our handler. A wrong sentinel means drop and log, never
apply.

**`DifficultySync.cs` (create)** — the latch and the wiring. Holds
`int? _hostEffectiveCount` and `bool _isMultiplayerRun`, and owns four seams:

| Seam | Patch | Purpose |
|---|---|---|
| Send, new run | prefix `StartRunLobby.BeginRunForAllPlayers` | Host latches its own value and broadcasts via `__instance.NetService.SendMessage(msg)` **before** the vanilla begin-run message, on the same `Reliable` channel, so it is processed first. |
| Send, saved run | prefix `LoadRunLobby.TryBeginRunForAllPlayers` / `BeginRunLocally` | Same, for loaded runs, which never pass through `StartRunLobby`. |
| Send, rejoin | postfix `RunLobby.HandleClientRejoinRequestMessage` | Re-send to that peer with `SendMessage(msg, playerId)`. Its service is the private `_netService` field. **Dead code on the current build**, kept as forward compatibility only: `ClientRejoinRequestMessage.ShouldBroadcast` is `false`, and the game has no mid-run rejoin flow at all — a dropped player returns by the host backing out and bringing them back in, which re-enters through `LoadRunLobby` and its begin-run seams. Do not count this seam as covering reconnection. |
| Receive | prefix `NetMessageBus.SendMessageToAllHandlers` | Sees every inbound message with no service reference and no register/unregister lifecycle to get wrong. |

Also resets the latch when a run ends or the peer disconnects, so a stale value cannot
leak into the next run.

**`Sts2Unlimited.GetEffectivePlayerCount` (modify)** — the single decision point:

```
not a multiplayer run    -> unchanged: override if enabled, gated by the
                            singleplayer toggle
multiplayer, host value  -> that value (0 -> actualCount)
multiplayer, no value    -> actualCount, and warn once
```

The host latches its own resolved value when it sends, so host and clients take the
identical branch and the scaling path needs no "am I the host" test.

### The multiplayer-run flag must not depend on our own message

If `_isMultiplayerRun` were inferred from having received a `DifficultySyncMessage`,
the fallback case would silently reproduce the bug: against an old-version host no
message ever arrives, the client would conclude "singleplayer", apply its own
override, and desync exactly as today.

So the flag is set from four **vanilla** seams that fire regardless of any peer's mod
version (F6): `StartRunLobby.HandleLobbyBeginRunMessage` and
`LoadRunLobby.HandleLobbyBeginRunMessage` on clients, `BeginRunForAllPlayers` and
`BeginRunLocally` on the host.

### Fallback behaviour (host sent nothing)

A client only ever applies an override the host sent it. With no host value it scales
by the real player count. This diverges in strictly fewer cases than keeping the local
value: it matches a vanilla-scaling host and an old-version host with the override off,
which is the default. The accepted side effect is that **a non-host's own slider stops
affecting multiplayer** — which is what "use the host's value" means.

Two user-visible signals:

- One WARN log at the first scaling call of an unsynced multiplayer run, naming the
  actual count used and that the host may be running an older version of the mod. This
  is what makes a future report diagnosable the way this one was.
- In Settings, when a host value is latched and we are not the host, the Difficulty
  rows grey out and the slider label shows the host's value — reusing the existing
  `LockRowIfRunInProgress` pattern at `SettingsMenuIntegration.cs:384`. A number that
  is not being used is worse than no number.

### Out of scope

- **An in-game popup.** After this change the only divergence left is "host runs an
  older mod version"; the mod has no existing toast surface to reuse, and building one
  is a larger UI job than this fix justifies. Revisit if reports continue.
- **A lobby-time echo** of the host's value (before Start), mirroring
  `LobbySeedChangedMessage`. Correctness does not need it; it is a second message type
  plus lobby-screen UI.
- **"Warn when values differ."** The reporter's second ask stops being meaningful
  between synced peers — there is nothing left to differ.

## Risks and assumptions

- **A1 — mod message ids are positional.** Verified that mod types are collected
  (F3) and that `ContentSorter.Sort` is called with `affectsGameplayAtEnd: true`. The
  exact ordering guarantee could **not** be verified headlessly: `ContentSorter<T>.Sort`
  is an open generic Harmony cannot read, and its mod-detection depends on live
  `ModManager` state. Peers with different sets of message-adding mods may therefore
  disagree on ids. Mitigated, not eliminated, by the sentinel: a mis-routed message is
  dropped and logged rather than applied. F4b softens the worst case — a peer that does not
  know the id ignores the message instead of disconnecting — so the realistic failure is
  "no value latched, fall back and warn", not a broken lobby. Still to be confirmed in-game,
  ideally with another message-adding mod installed.
- **A2 — message ordering.** Our message is sent before the vanilla begin-run message
  on the same `Reliable` channel, so it should be handled first. `ShouldBuffer = true`
  matches the vanilla lobby messages. Needs an in-game check that the value is latched
  before the first `CombatState.CreateCreature`.
- **A3 — all peers must run the mod.** Already true and already documented; this
  change does not widen that requirement. A peer *without* the mod cannot be made
  consistent and is out of scope.
- **A4 — an old-version host cannot be fixed, only reported.** If the host runs
  v1.0.1 with the override on, divergence is unavoidable; the fallback and the warning
  are the whole remedy.

## Success criteria

1. Two peers with *different* local Difficulty Scaling values, host override on,
   complete the first fight with identical monster HP and no State Divergence.
2. Host with the override **off** and a client with it **on** likewise agree.
3. The latched value survives a mid-combat rescale — a `ToughEgg.Hatch` or
   `TestSubject.Revive`, which reach `ScaleHpForMultiplayer` directly rather than the
   spawn-time `ScaleMonsterHpForMultiplayer`.
4. A loaded/saved multiplayer run syncs the same way as a fresh one. **This is the
   priority criterion, not an edge case:** the game has no mid-run rejoin, so the only
   way a dropped player returns is the host backing out and bringing them back in,
   which re-enters through `LoadRunLobby`. Every reconnect in the wild takes this path.
   It cannot be exercised under `-fastmp` — those sessions write synthetic net ids
   (host 1, client 1001) and `RunManager.CanonicalizeSave` validates against the real
   platform id, so a `-fastmp` save can never be reloaded. Needs two real Steam peers.
5. Singleplayer behaviour, including the singleplayer challenge toggle, is unchanged.
6. An unsynced multiplayer run scales by the actual player count and logs one WARN.
7. `dotnet run --project tests/PatchTests` passes, including a round-trip test for
   `DifficultySyncMessage` and a decision table for `GetEffectivePlayerCount`.
