using System;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Saves;

namespace Sts2Unlimited;

public static class SaveDirPatch
{
    public static void Postfix_ConstructDefault() => RedirectPaths(SaveManager.Instance);
    public static void Postfix_InitProfileId(SaveManager __instance) => RedirectPaths(__instance ?? SaveManager.Instance);
    public static void Postfix_SwitchProfileId(SaveManager __instance) => RedirectPaths(__instance ?? SaveManager.Instance);
    public static void ReplaceInstance() => RedirectPaths(SaveManager.Instance);

    private static void RedirectPaths(SaveManager instance)
    {
        string saveDir = CommandLineHelper.GetValue("save-dir");
        if (string.IsNullOrWhiteSpace(saveDir))
            return;

        if (instance == null)
            return;

        try
        {
            // SaveManager._saveStore (ISaveStore, runtime type = CloudSaveStore)
            var saveStoreField = typeof(SaveManager).GetField(
                "_saveStore",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (saveStoreField == null) return;

            var saveStore = saveStoreField.GetValue(instance);
            if (saveStore == null) return;

            // CloudSaveStore.<LocalStore>k__BackingField (ISaveStore, runtime type = GodotFileIo)
            var localStoreField = saveStore.GetType().GetField(
                "<LocalStore>k__BackingField",
                BindingFlags.NonPublic | BindingFlags.Instance);

            if (localStoreField == null)
            {
                // Fallback: patch string fields directly on _saveStore
                PatchStringFields(saveStore, saveDir);
                return;
            }

            var localStore = localStoreField.GetValue(saveStore);
            if (localStore == null) return;

            // GodotFileIo.<SaveDir>k__BackingField (string)
            var saveDirField = localStore.GetType().GetField(
                "<SaveDir>k__BackingField",
                BindingFlags.NonPublic | BindingFlags.Instance);

            if (saveDirField == null)
            {
                PatchStringFields(localStore, saveDir);
                return;
            }

            saveDirField.SetValue(localStore, saveDir);
            Log.LogMessage(LogLevel.Info, LogType.Generic,
                $"[SaveDirPatch] Save directory redirected to '{saveDir}'");
        }
        catch (Exception e)
        {
            Log.LogMessage(LogLevel.Error, LogType.Generic,
                $"[SaveDirPatch] Exception during redirect: {e.Message}");
        }
    }

    private static void PatchStringFields(object obj, string saveDir)
    {
        const string SteamPrefix = "user://steam/";
        foreach (var f in obj.GetType().GetFields(
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
        {
            if (f.FieldType != typeof(string)) continue;
            var val = f.GetValue(obj) as string;
            if (val == null || !val.StartsWith(SteamPrefix)) continue;

            var afterPrefix = val.Substring(SteamPrefix.Length);
            var slashIdx = afterPrefix.IndexOf('/');
            var relative = slashIdx >= 0 ? afterPrefix.Substring(slashIdx) : "";
            f.SetValue(obj, saveDir + relative);
        }
    }
}
