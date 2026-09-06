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
