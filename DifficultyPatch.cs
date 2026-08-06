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

        PatchHpScaling(harmony);

        // MultiplayerScalingModel — controls enemy block scaling
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

        }
        else
            Log.LogMessage(LogLevel.Warn, LogType.Generic,
                "[DifficultyPatch] MultiplayerScalingModel type not found.");

        PatchPowerScaling(harmony, transpiler);
    }

    // HP scaling funnels through one shared static helper — Creature.ScaleHpForMultiplayer(decimal
    // hp, EncounterModel encounter, int playerCount, int actIndex) — not just from
    // CombatState.CreateCreature (via the Creature.ScaleMonsterHpForMultiplayer instance wrapper)
    // but also called directly by several monsters' own phase-transition/respawn logic, each with
    // its own fresh Players.Count read: TestSubject.Revive (multi-phase boss respawns — this is
    // what caused Test Subject's 2nd/3rd form HP to ignore the override), DecimillipedeSegment.
    // AfterAddedToRoom, and ToughEgg.Hatch (confirmed via full assembly scan, including nested
    // async state-machine types). Chasing each call site with the transpiler (as the old
    // CombatState.CreateCreature-only patch did) misses any caller we haven't found — prefixing
    // the shared helper itself instead rewrites playerCount once, covering every current caller
    // and any future one automatically.
    private static void PatchHpScaling(Harmony harmony)
    {
        var creatureType = Type.GetType("MegaCrit.Sts2.Core.Entities.Creatures.Creature, sts2", false);
        if (creatureType == null)
        {
            Log.LogMessage(LogLevel.Warn, LogType.Generic, "[DifficultyPatch] Creature type not found.");
            return;
        }

        var scaleHp = creatureType.GetMethod("ScaleHpForMultiplayer", BindingFlags.Public | BindingFlags.Static);
        if (scaleHp == null)
        {
            Log.LogMessage(LogLevel.Warn, LogType.Generic, "[DifficultyPatch] Creature.ScaleHpForMultiplayer not found.");
            return;
        }

        var prefix = typeof(DifficultyPatch).GetMethod(
            nameof(Prefix_ScaleHpForMultiplayer), BindingFlags.NonPublic | BindingFlags.Static);
        harmony.Patch(scaleHp, prefix: new HarmonyMethod(prefix));
        Log.LogMessage(LogLevel.Info, LogType.Generic,
            "[DifficultyPatch] Patched Creature.ScaleHpForMultiplayer (HP scaling — covers initial " +
            "spawn plus all phase-transition/revive callers).");
    }

    private static void Prefix_ScaleHpForMultiplayer(ref int playerCount)
        => playerCount = Sts2Unlimited.GetEffectivePlayerCount(playerCount);

    // As of the current game build, MultiplayerScalingModel.ModifyPowerAmountGiven no longer
    // exists — power scaling was refactored into PowerModel.GetScaledAmountForMultiplayer
    // (virtual, reads Players.Count directly) gated by an overridable ShouldScaleInMultiplayer
    // flag. Several power classes (Artifact, Buffer, Plating, Skittish, Slippery) reimplement
    // their own Players.Count read rather than deferring to the base, so each needs its own
    // patch; everything else that opts into ShouldScaleInMultiplayer falls through to the base.
    //
    // NOTE: as of this writing, nothing in the game's compiled code actually calls
    // GetScaledAmountForMultiplayer or ShouldScaleInMultiplayer — verified via full assembly
    // scan, only a unit-test mock references them. Power-amount scaling therefore currently has
    // no effect in real gameplay regardless of this patch; it's applied anyway so the override
    // takes effect automatically once/if MegaCrit wires the hook into the live apply path.
    private static readonly string[] PowerScalingOverrideTypes =
    {
        "MegaCrit.Sts2.Core.Models.PowerModel",
        "MegaCrit.Sts2.Core.Models.Powers.ArtifactPower",
        "MegaCrit.Sts2.Core.Models.Powers.BufferPower",
        "MegaCrit.Sts2.Core.Models.Powers.PlatingPower",
        "MegaCrit.Sts2.Core.Models.Powers.SkittishPower",
        "MegaCrit.Sts2.Core.Models.Powers.SlipperyPower",
    };

    private static void PatchPowerScaling(Harmony harmony, HarmonyMethod transpiler)
    {
        int patchedCount = 0;
        foreach (var typeName in PowerScalingOverrideTypes)
        {
            var type = Type.GetType($"{typeName}, sts2", false);
            if (type == null)
            {
                Log.LogMessage(LogLevel.Warn, LogType.Generic,
                    $"[DifficultyPatch] {typeName} not found — skipping power scaling patch.");
                continue;
            }

            var method = type.GetMethod("GetScaledAmountForMultiplayer",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (method == null) continue; // expected for types relying on the base implementation

            harmony.Patch(method, transpiler: transpiler);
            patchedCount++;
        }

        Log.LogMessage(LogLevel.Info, LogType.Generic,
            $"[DifficultyPatch] Patched {patchedCount} GetScaledAmountForMultiplayer method(s) (power scaling). " +
            "Note: this hook has no known live caller in the current game build, so it has no gameplay " +
            "effect yet — patched for forward compatibility only.");
    }
}
