using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;

namespace Sts2Unlimited;

/// <summary>
/// Fixes "List length N is too large to fit in bit size 3" for >7 player sessions.
///
/// The game hardcodes lengthBits=3 in Serialize/Deserialize methods, capping list
/// length at 7. We transpile every Serialize/Deserialize method to replace the
/// constant 3 (when passed to WriteList/ReadList) with a call to GetRequiredBits(),
/// which returns a FIXED bit width based on the absolute maximum player count (16).
///
/// Using a fixed value (not MaxPlayersOverride) is critical for cross-client
/// compatibility: if two clients have different local MaxPlayers settings they must
/// still use identical wire formats, otherwise deserialization fails and joining is
/// impossible. 5 bits supports lists of up to 31 elements.
/// </summary>
public static class PacketSizePatch
{
    // Maximum players the mod UI allows (must match SettingsMenuIntegration.PLAYER_MAX).
    private const int AbsoluteMaxPlayers = 16;

    public static int RequiredBits(int maxCount)
        => maxCount <= 1 ? 1 : (int)Math.Ceiling(Math.Log2(maxCount + 1));

    /// <summary>
    /// Returns the fixed bit width used on the wire — always based on AbsoluteMaxPlayers,
    /// never the local MaxPlayersOverride, so all clients agree on the format.
    /// </summary>
    public static int GetRequiredBits()
        => RequiredBits(AbsoluteMaxPlayers);

    private static readonly MethodInfo _getRequiredBitsMethod =
        typeof(PacketSizePatch).GetMethod(nameof(GetRequiredBits),
            BindingFlags.Public | BindingFlags.Static)!;

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

    public static IEnumerable<CodeInstruction> Transpile_SerializeDeserialize(
        IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);
        int patched = 0;

        for (int i = 1; i < codes.Count; i++)
        {
            // Find callvirt/call to WriteList or ReadList on PacketWriter/PacketReader
            if (codes[i].opcode != OpCodes.Callvirt && codes[i].opcode != OpCodes.Call)
                continue;
            if (codes[i].operand is not MethodInfo mi)
                continue;
            if (mi.DeclaringType?.Name != "PacketWriter" && mi.DeclaringType?.Name != "PacketReader")
                continue;
            if (mi.Name != "WriteList" && mi.Name != "ReadList")
                continue;

            // The instruction immediately before is the lengthBits argument.
            // Only replace the constant 3 — other bit widths are intentional.
            if (!IsVanillaListBitsConstant(codes[i - 1]))
                continue;

            codes[i - 1] = new CodeInstruction(OpCodes.Call, _getRequiredBitsMethod);
            patched++;

            // Record which list this was, so the leaf guard can cover the same instantiation.
            if (mi.IsGenericMethod)
                _rewrittenListElementTypes.Add(mi.GetGenericArguments()[0]);
        }

        return codes;
    }

    // The compiler may emit the constant 3 as ldc.i4.3, ldc.i4.s 3, or ldc.i4 3.
    private static bool IsVanillaListBitsConstant(CodeInstruction code)
    {
        if (code.opcode == OpCodes.Ldc_I4_3) return true;
        if (code.opcode == OpCodes.Ldc_I4_S || code.opcode == OpCodes.Ldc_I4)
            return code.operand != null && Convert.ToInt32(code.operand) == VanillaListBits;
        return false;
    }

    public static void Apply(Harmony harmony)
    {
        var writerType = typeof(PacketWriter);
        var readerType = typeof(PacketReader);
        var transpilerMethod = typeof(PacketSizePatch).GetMethod(
            nameof(Transpile_SerializeDeserialize), BindingFlags.Public | BindingFlags.Static)!;
        var transpiler = new HarmonyMethod(transpilerMethod);

        int patchedTypes = 0;
        int errors = 0;

        foreach (var type in AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(SafeGetTypes))
        {
            var serialize = type.GetMethod("Serialize",
                BindingFlags.Public | BindingFlags.Instance,
                null, new[] { writerType }, null);
            var deserialize = type.GetMethod("Deserialize",
                BindingFlags.Public | BindingFlags.Instance,
                null, new[] { readerType }, null);

            if (serialize == null && deserialize == null) continue;

            bool patched = false;
            try
            {
                if (serialize != null) { harmony.Patch(serialize, transpiler: transpiler); patched = true; }
                if (deserialize != null) { harmony.Patch(deserialize, transpiler: transpiler); patched = true; }
                if (patched)
                {
                    patchedTypes++;
                    Log.LogMessage(LogLevel.Debug, LogType.Generic,
                        $"[PacketSizePatch] Patched {type.FullName ?? type.Name}");
                }
            }
            catch (Exception e)
            {
                Log.LogMessage(LogLevel.Warn, LogType.Generic,
                    $"[PacketSizePatch] Failed to patch {type.Name}: {e.Message}");
                errors++;
            }
        }

        Log.LogMessage(LogLevel.Info, LogType.Generic,
            $"[PacketSizePatch] Patched {patchedTypes} types, {errors} errors. " +
            $"WireBits={GetRequiredBits()} (fixed for AbsoluteMax={AbsoluteMaxPlayers})");
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch { return Array.Empty<Type>(); }
    }
}
