using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;

namespace Sts2Unlimited;

public static class DifficultyPatch
{
    private static readonly MethodInfo _getEffectivePlayerCount =
        typeof(Sts2Unlimited).GetMethod(nameof(Sts2Unlimited.GetEffectivePlayerCount),
            BindingFlags.Public | BindingFlags.Static)!;

    // Matches callvirt IReadOnlyCollection<Player>::get_Count() — the only .Count call
    // on a Player collection in each of the three target methods.
    private static bool IsPlayersCountCall(CodeInstruction ci)
    {
        if (ci.opcode != OpCodes.Callvirt) return false;
        if (ci.operand is not MethodInfo mi) return false;
        if (mi.Name != "get_Count") return false;
        var dt = mi.DeclaringType;
        if (dt == null || !dt.IsGenericType) return false;
        return dt.GetGenericArguments()[0].Name == "Player";
    }

    // Single transpiler shared across all three target methods.
    // Inserts `call GetEffectivePlayerCount(int)` after each Players.Count call,
    // so the int on the stack becomes the effective (possibly overridden) count.
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

        if (patched == 0)
            Log.LogMessage(LogLevel.Warn, LogType.Generic,
                $"[DifficultyPatch] No Players.Count call found in {original?.DeclaringType?.Name}.{original?.Name} — patch may be broken.");
        else
            Log.LogMessage(LogLevel.Debug, LogType.Generic,
                $"[DifficultyPatch] Patched {patched} Players.Count call(s) in {original?.DeclaringType?.Name}.{original?.Name}.");

        return codes;
    }

    public static void Apply(Harmony harmony)
    {
        var transpilerMethod = typeof(DifficultyPatch).GetMethod(
            nameof(Transpile_PlayerCount), BindingFlags.Public | BindingFlags.Static)!;
        var transpiler = new HarmonyMethod(transpilerMethod);

        // 1. CombatState.CreateCreature — controls enemy HP scaling
        var combatStateType = Type.GetType(
            "MegaCrit.Sts2.Core.Combat.CombatState, sts2", false);
        if (combatStateType != null)
        {
            var createCreature = combatStateType.GetMethod(
                "CreateCreature", BindingFlags.Public | BindingFlags.Instance);
            if (createCreature != null)
            {
                harmony.Patch(createCreature, transpiler: transpiler);
                Log.LogMessage(LogLevel.Info, LogType.Generic,
                    "[DifficultyPatch] Patched CombatState.CreateCreature (HP scaling).");
            }
            else
                Log.LogMessage(LogLevel.Warn, LogType.Generic,
                    "[DifficultyPatch] CombatState.CreateCreature not found.");
        }
        else
            Log.LogMessage(LogLevel.Warn, LogType.Generic,
                "[DifficultyPatch] CombatState type not found.");

        // 2 & 3. MultiplayerScalingModel — controls enemy block and power scaling
        var scalingModelType = Type.GetType(
            "MegaCrit.Sts2.Core.Models.Singleton.MultiplayerScalingModel, sts2", false);
        if (scalingModelType != null)
        {
            var modifyBlock = scalingModelType.GetMethod(
                "ModifyBlockMultiplicative", BindingFlags.Public | BindingFlags.Instance);
            if (modifyBlock != null)
            {
                harmony.Patch(modifyBlock, transpiler: transpiler);
                Log.LogMessage(LogLevel.Info, LogType.Generic,
                    "[DifficultyPatch] Patched MultiplayerScalingModel.ModifyBlockMultiplicative (block scaling).");
            }
            else
                Log.LogMessage(LogLevel.Warn, LogType.Generic,
                    "[DifficultyPatch] ModifyBlockMultiplicative not found.");

            var modifyPower = scalingModelType.GetMethod(
                "ModifyPowerAmountGiven", BindingFlags.Public | BindingFlags.Instance);
            if (modifyPower != null)
            {
                harmony.Patch(modifyPower, transpiler: transpiler);
                Log.LogMessage(LogLevel.Info, LogType.Generic,
                    "[DifficultyPatch] Patched MultiplayerScalingModel.ModifyPowerAmountGiven (power scaling).");
            }
            else
                Log.LogMessage(LogLevel.Warn, LogType.Generic,
                    "[DifficultyPatch] ModifyPowerAmountGiven not found.");
        }
        else
            Log.LogMessage(LogLevel.Warn, LogType.Generic,
                "[DifficultyPatch] MultiplayerScalingModel type not found.");
    }
}
