using System;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;

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

        if (_warnedUnsynced)
        {
            // A fallback already scaled at least one monster by the actual player count before
            // this latch arrived. Every monster scaled before now used the wrong number and
            // every one scaled after will use this one — a real divergence, not just a late
            // sync, so this has to be as loud as the warning that preceded it.
            LogSink(LogLevel.Warn,
                $"[DifficultySync] Host difficulty value latched late: {effectiveCount} " +
                $"({(effectiveCount == DifficultySyncMessage.NoOverride ? "no override" : "override")}, " +
                $"from {source}). This run already scaled at least one monster by the actual player " +
                $"count before this arrived, so it may diverge.");
            return;
        }

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
        if (_isMultiplayerRun) return ResolveForMultiplayer(actualCount, scaleSingleplayerEnabled);

        if (!overrideEnabled) return actualCount;
        if (actualCount == 1 && !scaleSingleplayerEnabled) return actualCount;
        return playersOverride;
    }

    /// <summary>What this peer scales by during a multiplayer run.</summary>
    private static int ResolveForMultiplayer(int actualCount, bool scaleSingleplayerEnabled)
    {
        // actualCount comes from the replicated player list, so all peers agree on it, and
        // actualCount == 1 means exactly one peer is computing combat state. The local toggle
        // is that peer's own business — it never reaches another peer, so it cannot diverge.
        // Checked before the latch (and before the unsynced warning) so a solo hosted run
        // neither consumes nor warns about either.
        if (actualCount == 1 && !scaleSingleplayerEnabled) return actualCount;

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

    /// <summary>
    /// Every inbound message passes through here, on host and client alike, which is why this is
    /// the receive seam: no service reference to find, and no subscription lifecycle to leak.
    /// </summary>
    /// <returns>
    /// false — skip the original — for our own message type, true for everything else. Nothing
    /// registers a handler for DifficultySyncMessage (this prefix IS the handler), so letting the
    /// original run would reach its "no message handlers are registered for that type" branch and
    /// Log.Error on every inbound packet. Skipping also bypasses the bus's buffering path, which
    /// is strictly better here: the value is latched on arrival instead of being replayed later,
    /// and a latch that lands before the first monster is scaled is the whole point.
    /// </returns>
    public static bool Prefix_SendMessageToAllHandlers(INetMessage message, ulong senderId)
    {
        if (message is not DifficultySyncMessage sync) return true;

        if (!sync.IsValid)
        {
            LogSink(LogLevel.Warn,
                $"[DifficultySync] Dropped a difficulty-sync message with an unrecognised " +
                $"sentinel (0x{sync.Sentinel:X8}) from {senderId}. Mod-added message ids are " +
                $"positional, so this is most likely another mod's message decoded into ours — " +
                $"check that every peer has the same mods installed.");
            return false;
        }

        if (_broadcastThisRun)
        {
            // We broadcast this run, which only a true host does (see TryBeginHostBroadcast),
            // so our own value is already authoritative. A peer is not the host and has no
            // business overwriting it — accepting this would be host-side divergence, the
            // worst shape, since every other peer still has the value we actually sent.
            LogSink(LogLevel.Warn,
                $"[DifficultySync] Ignored a difficulty-sync message from {senderId}: this peer " +
                $"already broadcast as host this run, so an inbound value is not authoritative " +
                $"and was not latched.");
            return false;
        }

        Latch(sync.EffectiveCount, $"peer {senderId}");
        return false;
    }

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
    /// channel so peers latch the value before the first CombatState.CreateCreature.
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

    // Resolved lazily (see Postfix_RunLobbyRejoin) rather than in a static field initializer:
    // a typeof(RunLobby) reference in an initializer would run as part of this class's static
    // ctor, so a failed RunLobby type load would throw a TypeInitializationException out of
    // every later static member access — including Resolve(), called from
    // DifficultyPatch.Prefix_ScaleHpForMultiplayer, which has no try/catch. That would turn
    // "RunLobby failed to load" into "runs cannot start," in the one class meant to survive a
    // game update by logging a warning instead.
    private static FieldInfo _runLobbyNetService;
    private static bool _runLobbyNetServiceResolveAttempted;

    private static FieldInfo RunLobbyNetServiceField()
    {
        if (!_runLobbyNetServiceResolveAttempted)
        {
            _runLobbyNetServiceResolveAttempted = true;
            try
            {
                _runLobbyNetService = AccessTools.Field(typeof(RunLobby), "_netService");
            }
            catch (Exception e)
            {
                LogSink(LogLevel.Warn,
                    $"[DifficultySync] Could not resolve RunLobby._netService: {e.Message}");
            }
        }

        return _runLobbyNetService;
    }

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

        var netService = RunLobbyNetServiceField()?.GetValue(__instance) as INetGameService;
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

    /// <summary>Run teardown. The only place per-run state is cleared.</summary>
    public static void Postfix_RunLobbyDispose()
    {
        LogSink(LogLevel.Info, "[DifficultySync] Run lobby disposed — clearing synced state.");
        Reset();
    }

    /// <summary>
    /// Pre-run lobby entry. A client can latch the host's value at BeginRunForAllPlayers time
    /// and then never reach a RunLobby at all — the host drops, or the client backs out of the
    /// transition. Postfix_RunLobbyDispose only fires once a RunLobby exists, so nothing clears
    /// the latch in that case, and it would otherwise leak into whatever lobby this peer joins
    /// next: MarkMultiplayerRun fires there too, _hostEffectiveCount is still set from the dead
    /// lobby, and ResolveForMultiplayer applies a value from a different lobby while also
    /// suppressing the unsynced warning — exactly the silent divergence that fallback exists to
    /// prevent. Entering a pre-run lobby always precedes any sync message for it, so clearing
    /// state here cannot drop a latch that belongs to the lobby being entered.
    /// </summary>
    public static void Postfix_LobbyEntered()
    {
        LogSink(LogLevel.Info,
            "[DifficultySync] Pre-run lobby entered — clearing any stale sync state from a " +
            "previous lobby.");
        Reset();
    }

    public static void Apply(Harmony harmony)
    {
        TryPatch(harmony, AccessTools.Method(typeof(NetMessageBus), "SendMessageToAllHandlers",
                     new[] { typeof(INetMessage), typeof(ulong) }),
                 nameof(Prefix_SendMessageToAllHandlers), prefix: true,
                 "NetMessageBus.SendMessageToAllHandlers (receive host difficulty value)");

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

        TryPatch(harmony, AccessTools.Method(typeof(RunLobby), "HandleClientRejoinRequestMessage"),
                 nameof(Postfix_RunLobbyRejoin), prefix: false,
                 "RunLobby.HandleClientRejoinRequestMessage (re-send difficulty value on rejoin)");

        // Teardown.
        TryPatch(harmony, AccessTools.Method(typeof(RunLobby), "Dispose"),
                 nameof(Postfix_RunLobbyDispose), prefix: false,
                 "RunLobby.Dispose (clear synced state)");

        // Pre-run lobby entry: the earliest point that always precedes any sync message for a
        // given lobby, so it is safe to clear whatever a dead previous lobby left behind.
        // AccessTools.Constructor(type) with no parameter list targets a zero-arg constructor;
        // neither StartRunLobby nor LoadRunLobby has one (both have two overloads, all with
        // several parameters), so that call resolves to System.Object's parameterless
        // constructor instead of returning null — patching that would postfix *every* object
        // allocation in the process. Patching each declared constructor of the two lobby types
        // individually avoids that trap.
        TryPatchAllConstructors(harmony, typeof(StartRunLobby), nameof(Postfix_LobbyEntered),
            "StartRunLobby constructor (clear stale sync state on lobby entry)");
        TryPatchAllConstructors(harmony, typeof(LoadRunLobby), nameof(Postfix_LobbyEntered),
            "LoadRunLobby constructor (clear stale sync state on lobby entry)");
    }

    /// <summary>
    /// Patches every declared constructor of <paramref name="type"/> with a postfix, rather than
    /// a single resolved constructor — see the comment at the call sites in <see cref="Apply"/>
    /// for why AccessTools.Constructor(type) alone is not safe for these two types.
    /// </summary>
    private static void TryPatchAllConstructors(Harmony harmony, Type type, string patchName,
                                                 string description)
    {
        var ctors = AccessTools.GetDeclaredConstructors(type);
        if (ctors == null || ctors.Count == 0)
        {
            LogSink(LogLevel.Warn, $"[DifficultySync] {description}: no constructors found on " +
                                    $"{type.Name} — skipping.");
            return;
        }

        foreach (var ctor in ctors)
            TryPatch(harmony, ctor, patchName, prefix: false,
                $"{description} ({ctor.GetParameters().Length} args)");
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
}
