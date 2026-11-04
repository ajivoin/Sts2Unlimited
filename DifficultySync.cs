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
    }
}
