using Godot;
using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;

namespace Sts2Unlimited;

// NSettingsSlider : Control
//   "Label"       → MegaRichTextLabel  (name; added by PARENT scene, missing from Duplicate(15))
//   "Slider"      → NSlider : Range    (handle position formula: _currentHandlePosition / MaxValue,
//                                        assumes MinValue=0; we use internal range [0,14] → display [2,16])
//   "SliderValue" → MegaLabel : Label  (value display)
//   "SelectionReticle" → NSelectionReticle
// NSlider._Ready: _handle = GetNode("%Handle")  ← scene-unique-name, breaks with Duplicate(7)
//   → use Duplicate(15) to keep NSlider working, copy "Label" from original manually.

public static class SettingsMenuIntegration
{
    // Internal NSlider range. Actual players = internalValue + PLAYER_OFFSET.
    private const int PLAYER_MIN    = 2;
    private const int PLAYER_MAX    = 16;
    private const int PLAYER_OFFSET = PLAYER_MIN;                   // 2
    private const int INTERNAL_MIN  = 0;
    private const int INTERNAL_MAX  = PLAYER_MAX - PLAYER_OFFSET;   // 14

    private static string SettingsPath => Path.Combine(
        Path.GetDirectoryName(typeof(Sts2Unlimited).Assembly.Location) ?? ".",
        "sts2unlimited.settings.json");

    public static void InitializeSettingsMenuUI(Harmony harmony)
    {
        try
        {
            var screenType = Type.GetType(
                "MegaCrit.Sts2.Core.Nodes.Screens.Settings.NSettingsScreen, sts2", false);
            if (screenType == null) { GD.PrintErr("[Sts2Unlimited] NSettingsScreen type not found."); return; }

            var readyMethod = screenType.GetMethod("_Ready",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (readyMethod == null) { GD.PrintErr("[Sts2Unlimited] NSettingsScreen._Ready not found."); return; }

            harmony.Patch(readyMethod, postfix: new HarmonyMethod(
                typeof(SettingsMenuIntegration).GetMethod(nameof(Patch_NSettingsScreen_Ready),
                    BindingFlags.NonPublic | BindingFlags.Static)));

            GD.Print("[Sts2Unlimited] Patched NSettingsScreen._Ready.");
        }
        catch (Exception e) { GD.PrintErr($"[Sts2Unlimited] Patch failed: {e.Message}"); }
    }

    private static void Patch_NSettingsScreen_Ready(object __instance)
    {
        if (__instance is not Node screen) return;
        try
        {
            var masterType = Type.GetType(
                "MegaCrit.Sts2.Core.Nodes.Screens.Settings.NMasterVolumeSlider, sts2", false);
            if (masterType == null) { GD.PrintErr("[Sts2Unlimited] NMasterVolumeSlider not found."); return; }

            // Use NMasterVolumeSlider (Sound tab) as a template for the row structure:
            //   MarginContainer (row) → MegaRichTextLabel 'Label' + NMasterVolumeSlider
            Node template    = FindNodeByType(screen, masterType);
            if (template == null) { GD.PrintErr("[Sts2Unlimited] NMasterVolumeSlider node not found."); return; }
            Node templateRow = template.GetParent(); // MarginContainer

            // Target: General tab, after the Modding divider
            // %ModdingDivider is a scene-unique-name node inside the General settings VBoxContainer.
            Node moddingDivider = screen.GetNodeOrNull("%ModdingDivider");
            if (moddingDivider == null)
            {
                GD.PrintErr("[Sts2Unlimited] %ModdingDivider not found — cannot place slider.");
                return;
            }
            Node targetParent = moddingDivider.GetParent(); // General settings VBoxContainer

            // Duplicate the whole row (gets Label + styled NMasterVolumeSlider)
            Node sliderRow = (Node)templateRow.Duplicate(15);
            sliderRow.Name = "Sts2UnlimitedMaxPlayersRow";
            Node slider = FindNodeByType(sliderRow, masterType);

            // Divider: clone %ModdingDivider (same tab, guaranteed same style)
            Node divider = (Node)moddingDivider.Duplicate();
            divider.Name = "Sts2UnlimitedMaxPlayersDivider";

            // Insert divider then sliderRow immediately after %Modding (the button),
            // not after %ModdingDivider (which comes before the button).
            Node moddingButton = screen.GetNodeOrNull("%Modding") ?? moddingDivider;
            int insertAt = moddingButton.GetIndex();
            targetParent.AddChild(divider);
            targetParent.AddChild(sliderRow);
            targetParent.MoveChild(divider,   insertAt + 1);
            targetParent.MoveChild(sliderRow, insertAt + 2);

            var tree = sliderRow.GetTree();
            if (tree == null) return;

            tree.Connect(SceneTree.SignalName.ProcessFrame, Callable.From(() =>
            {
                try { ConfigureMaxPlayersSlider(slider, sliderRow, Sts2Unlimited.MaxPlayersOverride); }
                catch (Exception e) { GD.PrintErr($"[Sts2Unlimited] Config error: {e.Message}\n{e.StackTrace}"); }
            }), (uint)GodotObject.ConnectFlags.OneShot);
        }
        catch (Exception e) { GD.PrintErr($"[Sts2Unlimited] Injection error: {e.Message}\n{e.StackTrace}"); }
    }

    private static void ConfigureMaxPlayersSlider(Node slider, Node sliderRow, int playerCount)
    {
        // ── 1. Name label — MegaRichTextLabel 'Label' inside the MarginContainer row ──
        var nameLabel = sliderRow.GetNodeOrNull("Label");
        if (nameLabel != null)
            nameLabel.Set("text", "Max Players");
        else
            GD.PrintErr("[Sts2Unlimited] 'Label' not found in sliderRow.");

        // ── 2. NSlider (Range) ───────────────────────────────────────────────
        var nslider = slider?.GetNodeOrNull("Slider") as Godot.Range;
        if (nslider == null) { GD.PrintErr("[Sts2Unlimited] 'Slider' child not found."); return; }

        // Disconnect existing handlers:
        //   NSettingsSlider.OnValueChanged  → formats label as "X%"
        //   NMasterVolumeSlider.OnValueChanged → modifies master audio volume  ← must remove!
        foreach (var conn in nslider.GetSignalConnectionList("value_changed"))
            nslider.Disconnect("value_changed", conn["callable"].As<Callable>());

        // NSlider.UpdateHandlePosition uses: _currentHandlePosition / MaxValue
        // This assumes MinValue=0. To have value=2 appear at the leftmost position,
        // use internal range [0, 14] and add PLAYER_OFFSET when reading/saving.
        int internalValue = Math.Clamp(playerCount - PLAYER_OFFSET, INTERNAL_MIN, INTERNAL_MAX);

        nslider.MinValue = INTERNAL_MIN;
        nslider.MaxValue = INTERNAL_MAX;
        nslider.Step     = 1;
        nslider.Value    = internalValue;
        // Snap the visual handle to the correct position immediately
        nslider.Call("SetValueWithoutAnimation", (double)internalValue);

        // Our handler: update MaxPlayersOverride and value display
        nslider.Connect(Godot.Range.SignalName.ValueChanged, Callable.From<double>(v =>
        {
            int players = (int)Math.Round(v) + PLAYER_OFFSET;
            Sts2Unlimited.MaxPlayersOverride = players;
            SaveSettings();
            slider.GetNodeOrNull("SliderValue")?.Set("text", $"{players}");
        }));

        // ── 3. Initial value display ─────────────────────────────────────────
        slider.GetNodeOrNull("SliderValue")?.Set("text", $"{playerCount}");

        GD.Print($"[Sts2Unlimited] Max Players slider configured: range [{PLAYER_MIN},{PLAYER_MAX}], current={playerCount}");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static Node FindSiblingDivider(Node parent, Node referenceNode)
    {
        var children = parent.GetChildren();
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i] != referenceNode) continue;
            if (i > 0 && LooksDivider(children[i - 1])) return children[i - 1];
            if (i < children.Count - 1 && LooksDivider(children[i + 1])) return children[i + 1];
        }
        foreach (var child in children)
            if (LooksDivider(child)) return child;
        return null;
    }

    private static bool LooksDivider(Node node)
    {
        if (node is HSeparator || node is VSeparator || node is ColorRect) return true;
        var typeName = node.GetType().Name;
        var nodeName = node.Name.ToString();
        return typeName.Contains("Separator") || typeName.Contains("Divider")
            || nodeName.Contains("Separator") || nodeName.Contains("Divider")
            || nodeName.StartsWith("Line");
    }

    private static Node FindAncestorBoxContainer(Node node)
    {
        var cur = node.GetParent();
        while (cur != null) { if (cur is BoxContainer) return cur; cur = cur.GetParent(); }
        return null;
    }

    private static Node FindNodeByType(Node root, Type targetType)
    {
        if (root.GetType() == targetType) return root;
        foreach (Node child in root.GetChildren(includeInternal: true))
        {
            var found = FindNodeByType(child, targetType);
            if (found != null) return found;
        }
        return null;
    }

    public static void SaveSettings()
    {
        try
        {
            string json = $"{{\"MaxPlayers\":{Sts2Unlimited.MaxPlayersOverride}," +
                          $"\"DifficultyEnabled\":{(Sts2Unlimited.DifficultyOverrideEnabled ? "true" : "false")}," +
                          $"\"DifficultyPlayers\":{Sts2Unlimited.DifficultyPlayersOverride}}}";
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception e) { GD.PrintErr($"[Sts2Unlimited] Failed to save settings: {e.Message}"); }
    }
}
