using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;

namespace Sts2Unlimited;

public static class DifficultyPatch
{
    // The game's logger segfaults outside a game process; the harness swaps this out.
    internal static Action<LogLevel, string> LogSink =
        (level, message) => Log.LogMessage(level, LogType.Generic, message);

    // Sts2Unlimited.cs is not compiled into the harness, so the transpiled call goes through
    // a delegate that ModLoaded wires to Sts2Unlimited.GetEffectivePlayerCount.
    internal static Func<int, int> PlayerCountResolver = actual => actual;

    public static int EffectivePlayerCount(int actualCount) => PlayerCountResolver(actualCount);

    internal const string ScalingModelTypeName =
        "MegaCrit.Sts2.Core.Models.Singleton.MultiplayerScalingModel";
    internal const string LegacyScalingModelTypeName =
        "MegaCrit.Sts2.Core.Models.Singleton.MultiplayerScalingModel";

    internal static Type ResolveFirstType(params string[] fullNames)
    {
        foreach (var name in fullNames)
        {
            var type = Type.GetType($"{name}, sts2", false);
            if (type != null) return type;
        }
        return null;
    }

    internal static MethodInfo GetModifyBlockMethod()
        => ResolveFirstType(ScalingModelTypeName, LegacyScalingModelTypeName)
            ?.GetMethod("ModifyBlockMultiplicative", BindingFlags.Public | BindingFlags.Instance);

    internal static int LastTranspilePatchCount;

    private static readonly MethodInfo _getEffectivePlayerCount =
        typeof(DifficultyPatch).GetMethod(nameof(EffectivePlayerCount),
            BindingFlags.Public | BindingFlags.Static)!;

    private static bool IsPlayersCountCall(CodeInstruction ci)
    {
        if (ci.opcode != OpCodes.Callvirt) return false;
        if (ci.operand is not MethodInfo mi) return false;
        if (mi.Name != "get_Count") return false;
        var dt = mi.DeclaringType;
        if (dt == null || !dt.IsGenericType) return false;
        return dt.GetGenericArguments()[0].Name == "Player";
    }

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

        LastTranspilePatchCount = patched;

        if (patched == 0)
            LogSink(LogLevel.Warn,
                $"[DifficultyPatch] No Players.Count call found in {original?.DeclaringType?.Name}.{original?.Name} — patch may be broken.");
        else
            LogSink(LogLevel.Debug,
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
        var modifyBlock = GetModifyBlockMethod();
        if (modifyBlock != null)
        {
            harmony.Patch(modifyBlock, transpiler: transpiler);
            LogSink(LogLevel.Info,
                "[DifficultyPatch] Patched MultiplayerScalingModel.ModifyBlockMultiplicative (block scaling).");
        }
        else
            LogSink(LogLevel.Warn, "[DifficultyPatch] MultiplayerScalingModel type not found.");

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
            LogSink(LogLevel.Warn, "[DifficultyPatch] Creature type not found.");
            return;
        }

        var prefix = typeof(DifficultyPatch).GetMethod(
            nameof(Prefix_ScaleHpForMultiplayer), BindingFlags.NonPublic | BindingFlags.Static);
        var prefixMethod = new HarmonyMethod(prefix);

        var scaleHp = creatureType.GetMethod("ScaleHpForMultiplayer", BindingFlags.Public | BindingFlags.Static);
        if (scaleHp != null)
        {
            harmony.Patch(scaleHp, prefix: prefixMethod);
            LogSink(LogLevel.Info, "[DifficultyPatch] Patched Creature.ScaleHpForMultiplayer (HP scaling — covers " +
                "phase-transition/revive callers that read Players.Count directly).");
        }
        else
            LogSink(LogLevel.Warn, "[DifficultyPatch] Creature.ScaleHpForMultiplayer not found.");

        // ScaleMonsterHpForMultiplayer (the instance wrapper CreateCreature actually calls) has
        // its own early-return when playerCount == 1 — it never even calls the static helper
        // above in that case. That's why the singleplayer-scaling toggle had no effect: with the
        // raw actual count (1) going in, this wrapper bails out before our other prefix ever gets
        // a chance to run. Same prefix works here too since the parameter is also named
        // playerCount — rewriting it here, before the wrapper's own == 1 check, means an enabled
        // override reaches that check as a non-1 value and the wrapper proceeds normally.
        var scaleMonsterHp = creatureType.GetMethod("ScaleMonsterHpForMultiplayer", BindingFlags.Public | BindingFlags.Instance);
        if (scaleMonsterHp != null)
        {
            harmony.Patch(scaleMonsterHp, prefix: prefixMethod);
            LogSink(LogLevel.Info, "[DifficultyPatch] Patched Creature.ScaleMonsterHpForMultiplayer (HP scaling — covers " +
                "initial spawn via CombatState.CreateCreature, including the singleplayer case).");
        }
        else
            LogSink(LogLevel.Warn, "[DifficultyPatch] Creature.ScaleMonsterHpForMultiplayer not found.");
    }

    private static void Prefix_ScaleHpForMultiplayer(ref int playerCount)
    {
        int actual = playerCount;
        playerCount = EffectivePlayerCount(playerCount);
        LogSink(LogLevel.Debug,
            $"[DifficultyPatch] ScaleHpForMultiplayer: actual={actual} -> effective={playerCount}");
    }

    internal static readonly string[] PowerScalingOverrideTypes =
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
                LogSink(LogLevel.Warn, $"[DifficultyPatch] {typeName} not found — skipping power scaling patch.");
                continue;
            }

            var method = type.GetMethod("GetScaledAmountForMultiplayer",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (method == null) continue; // expected for types relying on the base implementation

            harmony.Patch(method, transpiler: transpiler);
            patchedCount++;
        }

        LogSink(LogLevel.Info, $"[DifficultyPatch] Patched {patchedCount} GetScaledAmountForMultiplayer method(s) (power scaling). " +
            "Note: this hook has no known live caller in the current game build, so it has no gameplay " +
            "effect yet — patched for forward compatibility only.");
    }
}
