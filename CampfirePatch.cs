using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.RestSite;

namespace Sts2Unlimited;

public static class CampfirePatch
{
    /// <summary>
    /// Whether the transpiler actually found its IL site. A transpiler that
    /// matches nothing installs cleanly and does nothing, so this is the only
    /// honest signal that the fix is live.
    /// </summary>
    public static bool Applied { get; private set; }

    private static readonly MethodInfo _safeAddMethod =
        typeof(CampfirePatch).GetMethod(nameof(SafeAddToContainer),
            BindingFlags.Public | BindingFlags.Static)!;

    /// <summary>
    /// Bounds-safe replacement for _characterContainers[i].AddChildSafely(character).
    /// Players beyond the 4 hardcoded scene slots are added to the last valid container so
    /// that their scene nodes are initialised and callbacks (ShowSelectedRestSiteOption, etc.)
    /// do not throw NullReferenceException.  They will overlap visually in that slot.
    /// </summary>
    public static void SafeAddToContainer(List<Control> containers, int index, NRestSiteCharacter character)
    {
        int clampedIndex = Math.Min(index, containers.Count - 1);
        containers[clampedIndex].AddChildSafely(character);
    }

    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Transpile_Ready(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);

        // Target pattern in IL:
        //   ldfld     _characterContainers        ← load the list field (preceded by ldarg.0)
        //   ldloc     <i>                          ← load loop index
        //   callvirt  List<Control>.get_Item       ← _characterContainers[i]  (crashes when i >= 4)
        //   ldloc     <nRestSiteCharacter>         ← load the character
        //   call      AddChildSafely               ← extension method call
        //
        // Replacement:
        //   ldfld     _characterContainers
        //   ldloc     <i>
        //   ldloc     <nRestSiteCharacter>
        //   call      CampfirePatch.SafeAddToContainer   ← bounds-checked helper

        var listGetItem = typeof(List<Control>).GetMethod("get_Item");
        var characterContainersField = typeof(NRestSiteRoom)
            .GetField("_characterContainers", BindingFlags.NonPublic | BindingFlags.Instance);

        if (characterContainersField == null)
        {
            Log.LogMessage(LogLevel.Error, LogType.Generic,
                "[CampfirePatch] Could not find _characterContainers field — campfire patch skipped");
            Applied = false;
            return codes;
        }

        bool patched = false;
        for (int i = 0; i < codes.Count - 4; i++)
        {
            if (codes[i].opcode == OpCodes.Ldfld &&
                codes[i].operand is FieldInfo f && f == characterContainersField &&
                codes[i + 2].opcode == OpCodes.Callvirt &&
                codes[i + 2].operand is MethodInfo m && m == listGetItem)
            {
                // Remove callvirt get_Item at i+2; shifts i+3 → i+2, i+4 → i+3
                codes.RemoveAt(i + 2);

                // Replace the original AddChildSafely call (now at i+3) with our helper
                codes[i + 3] = new CodeInstruction(OpCodes.Call, _safeAddMethod);

                patched = true;
                break; // only one such site in _Ready
            }
        }

        Applied = patched;
        if (!patched)
            Log.LogMessage(LogLevel.Warn, LogType.Generic,
                "[CampfirePatch] Pattern not found in NRestSiteRoom._Ready — campfire patch may be ineffective");

        return codes;
    }
}
