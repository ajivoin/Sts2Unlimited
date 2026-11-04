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

    /// <summary>Run teardown. The only place per-run state is cleared.</summary>
    public static void Postfix_RunLobbyDispose()
    {
        LogSink(LogLevel.Info, "[DifficultySync] Run lobby disposed — clearing synced state.");
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

        // Teardown.
        TryPatch(harmony, AccessTools.Method(typeof(RunLobby), "Dispose"),
                 nameof(Postfix_RunLobbyDispose), prefix: false,
                 "RunLobby.Dispose (clear synced state)");
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
