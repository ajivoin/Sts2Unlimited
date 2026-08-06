using Godot;
using System;
using System.Collections.Generic;
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

    // Internal NSlider range for difficulty override. Actual value = internalValue + DIFFICULTY_OFFSET.
    private const int DIFFICULTY_MIN    = 1;
    private const int DIFFICULTY_MAX    = 16;
    private const int DIFFICULTY_OFFSET = DIFFICULTY_MIN;                        // 1
    private const int DIFF_INTERNAL_MIN = 0;
    private const int DIFF_INTERNAL_MAX = DIFFICULTY_MAX - DIFFICULTY_OFFSET;   // 15

    private static string SettingsPath => Path.Combine(
        Path.GetDirectoryName(typeof(Sts2Unlimited).Assembly.Location) ?? ".",
        "sts2unlimited.settings.json");

    // NSettingsTickbox is never instantiated directly by the game — every tickbox in the
    // settings screen is one of its subclasses (NFullscreenTickbox, NFastModeTickbox, ...).
    // We duplicate NMuteInBackgroundTickbox as our template because its OnTick/OnUntick only
    // write a single PrefsSave bool (no extra signal wiring like NFullscreenTickbox's
    // NGame.WindowChange listener). OnTick/OnUntick fire directly from NTickbox.OnPress() on
    // click — NOT via the disconnectable "Toggled" signal — so they still run on our duplicate
    // unless suppressed. We Harmony-prefix them and skip only for our own duplicated instances
    // (we have two now: the main difficulty toggle and the singleplayer sub-toggle), leaving
    // the real "Mute In Background" tickbox elsewhere unaffected.
    private const string TickboxTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.Settings.NMuteInBackgroundTickbox";
    private static readonly HashSet<GodotObject> _ourTickboxInstances = new();

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

            PatchTickboxInstanceGuard(harmony);
        }
        catch (Exception e) { GD.PrintErr($"[Sts2Unlimited] Patch failed: {e.Message}"); }
    }

    // Suppresses NMuteInBackgroundTickbox.OnTick/OnUntick's real side effect (writing
    // PrefsSave.MuteInBackground + showing a toast) specifically for our duplicated instance,
    // since those methods run unconditionally on click regardless of signal connections.
    private static void PatchTickboxInstanceGuard(Harmony harmony)
    {
        var tickboxType = Type.GetType($"{TickboxTypeName}, sts2", false);
        if (tickboxType == null)
        {
            GD.PrintErr("[Sts2Unlimited] NMuteInBackgroundTickbox type not found — cannot install instance guard.");
            return;
        }

        var suppressMethod = typeof(SettingsMenuIntegration).GetMethod(nameof(Prefix_SuppressForOurInstance),
            BindingFlags.NonPublic | BindingFlags.Static);
        var prefix = new HarmonyMethod(suppressMethod);

        foreach (var name in new[] { "OnTick", "OnUntick" })
        {
            var method = tickboxType.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (method == null) { GD.PrintErr($"[Sts2Unlimited] NMuteInBackgroundTickbox.{name} not found."); continue; }
            harmony.Patch(method, prefix: prefix);
        }

        GD.Print("[Sts2Unlimited] Patched NMuteInBackgroundTickbox.OnTick/OnUntick instance guard.");
    }

    private static bool Prefix_SuppressForOurInstance(object __instance)
        => __instance is not GodotObject go || !_ourTickboxInstances.Contains(go);

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

            // ── Difficulty scaling override ───────────────────────────────────────────
            var tickboxType = Type.GetType($"{TickboxTypeName}, sts2", false);
            if (tickboxType == null)
            {
                GD.PrintErr("[Sts2Unlimited] NMuteInBackgroundTickbox type not found — difficulty toggle skipped.");
                return;
            }
            Node tickboxTemplate = FindNodeByType(screen, tickboxType);
            if (tickboxTemplate == null)
            {
                GD.PrintErr("[Sts2Unlimited] NMuteInBackgroundTickbox node not found — difficulty toggle skipped.");
                return;
            }

            Node toggleRow      = (Node)tickboxTemplate.GetParent().Duplicate(15);
            Node diffSliderRow  = (Node)templateRow.Duplicate(15);
            Node spToggleRow    = (Node)tickboxTemplate.GetParent().Duplicate(15);
            toggleRow.Name      = "Sts2UnlimitedDifficultyToggleRow";
            diffSliderRow.Name  = "Sts2UnlimitedDifficultySliderRow";
            spToggleRow.Name    = "Sts2UnlimitedDifficultySingleplayerRow";

            // Separator lines between each of our own rows (matches the divider already
            // placed above Max Players, which only separated our whole block from Modding).
            Node dividerBeforeToggle     = (Node)moddingDivider.Duplicate();
            Node dividerBeforeDiffSlider = (Node)moddingDivider.Duplicate();
            Node dividerBeforeSpToggle   = (Node)moddingDivider.Duplicate();
            dividerBeforeToggle.Name     = "Sts2UnlimitedDifficultyToggleDivider";
            dividerBeforeDiffSlider.Name = "Sts2UnlimitedDifficultySliderDivider";
            dividerBeforeSpToggle.Name   = "Sts2UnlimitedDifficultySingleplayerDivider";

            targetParent.AddChild(dividerBeforeToggle);
            targetParent.AddChild(toggleRow);
            targetParent.AddChild(dividerBeforeDiffSlider);
            targetParent.AddChild(diffSliderRow);
            targetParent.AddChild(dividerBeforeSpToggle);
            targetParent.AddChild(spToggleRow);
            targetParent.MoveChild(dividerBeforeToggle,     insertAt + 3);
            targetParent.MoveChild(toggleRow,               insertAt + 4);
            targetParent.MoveChild(dividerBeforeDiffSlider, insertAt + 5);
            targetParent.MoveChild(diffSliderRow,           insertAt + 6);
            targetParent.MoveChild(dividerBeforeSpToggle,   insertAt + 7);
            targetParent.MoveChild(spToggleRow,             insertAt + 8);

            // Visibility of the difficulty slider and the singleplayer sub-toggle (plus each
            // one's own leading divider, so hiding a row doesn't leave two dividers back-to-back
            // with nothing between them) mirrors the main toggle state.
            Node[] dependentRows = { diffSliderRow, dividerBeforeDiffSlider, spToggleRow, dividerBeforeSpToggle };
            foreach (var n in dependentRows) n.Set("visible", Sts2Unlimited.DifficultyOverrideEnabled);

            tree.Connect(SceneTree.SignalName.ProcessFrame, Callable.From(() =>
            {
                try
                {
                    ConfigureDifficultyToggle(tickboxType, toggleRow, dependentRows);
                    ConfigureDifficultySlider(slider, diffSliderRow, Sts2Unlimited.DifficultyPlayersOverride);
                    ConfigureSingleplayerToggle(tickboxType, spToggleRow);
                }
                catch (Exception e)
                {
                    GD.PrintErr($"[Sts2Unlimited] Difficulty UI config error: {e.Message}\n{e.StackTrace}");
                }
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

        LockRowIfRunInProgress(sliderRow, nslider);

        GD.Print($"[Sts2Unlimited] Max Players slider configured: range [{PLAYER_MIN},{PLAYER_MAX}], current={playerCount}");
    }

    private static void ConfigureDifficultyToggle(Type tickboxType, Node toggleRow, Node[] dependentRows)
    {
        // Set the row label
        var nameLabel = toggleRow.GetNodeOrNull("Label");
        if (nameLabel != null)
            nameLabel.Set("text", "Adjust Multiplayer Difficulty");
        else
            GD.PrintErr("[Sts2Unlimited] 'Label' not found in toggleRow.");

        // Find the tickbox control inside the duplicated row
        Node tickbox = FindNodeByType(toggleRow, tickboxType);
        if (tickbox == null)
        {
            GD.PrintErr("[Sts2Unlimited] NMuteInBackgroundTickbox not found in toggleRow.");
            return;
        }

        // Register this instance with the Harmony guard so its OnTick/OnUntick (which fire
        // unconditionally on click, not just via the "Toggled" signal) don't write the real
        // MuteInBackground pref — see PatchTickboxInstanceGuard.
        if (tickbox is GodotObject go) _ourTickboxInstances.Add(go);

        // Disconnect any existing Toggled handlers from the duplicate
        DisconnectExistingToggledHandlers(tickbox);

        // Reflect initial ticked state
        tickbox.Set("IsTicked", Sts2Unlimited.DifficultyOverrideEnabled);

        // Wire our handler: toggle the override and show/hide the slider + singleplayer sub-toggle
        tickbox.Connect("Toggled", Callable.From<GodotObject>(sender =>
        {
            bool ticked = (bool)sender.Get("IsTicked");
            Sts2Unlimited.DifficultyOverrideEnabled = ticked;
            foreach (var n in dependentRows) n.Set("visible", ticked);
            SaveSettings();
        }));

        LockRowIfRunInProgress(toggleRow, tickbox);

        GD.Print("[Sts2Unlimited] Difficulty toggle configured.");
    }

    private static void ConfigureDifficultySlider(Node sliderTemplate, Node diffSliderRow, int currentDifficulty)
    {
        // Set the row label
        var nameLabel = diffSliderRow.GetNodeOrNull("Label");
        if (nameLabel != null)
            nameLabel.Set("text", "Difficulty Scaling");
        else
            GD.PrintErr("[Sts2Unlimited] 'Label' not found in diffSliderRow.");

        // Find the NMasterVolumeSlider and then its inner NSlider Range
        var masterType = Type.GetType(
            "MegaCrit.Sts2.Core.Nodes.Screens.Settings.NMasterVolumeSlider, sts2", false);
        Node diffSlider = FindNodeByType(diffSliderRow, masterType);
        if (diffSlider == null)
        {
            GD.PrintErr("[Sts2Unlimited] NMasterVolumeSlider not found in diffSliderRow.");
            return;
        }

        var nslider = diffSlider?.GetNodeOrNull("Slider") as Godot.Range;
        if (nslider == null)
        {
            GD.PrintErr("[Sts2Unlimited] 'Slider' child not found in difficulty slider.");
            return;
        }

        // Disconnect existing handlers (carries over from NMasterVolumeSlider template)
        foreach (var conn in nslider.GetSignalConnectionList("value_changed"))
            nslider.Disconnect("value_changed", conn["callable"].As<Callable>());

        int internalValue = Math.Clamp(currentDifficulty - DIFFICULTY_OFFSET, DIFF_INTERNAL_MIN, DIFF_INTERNAL_MAX);

        nslider.MinValue = DIFF_INTERNAL_MIN;
        nslider.MaxValue = DIFF_INTERNAL_MAX;
        nslider.Step     = 1;
        nslider.Value    = internalValue;
        nslider.Call("SetValueWithoutAnimation", (double)internalValue);

        nslider.Connect(Godot.Range.SignalName.ValueChanged, Callable.From<double>(v =>
        {
            int difficulty = (int)Math.Round(v) + DIFFICULTY_OFFSET;
            Sts2Unlimited.DifficultyPlayersOverride = difficulty;
            SaveSettings();
            diffSlider.GetNodeOrNull("SliderValue")?.Set("text", $"{difficulty}");
        }));

        // Initial value display
        diffSlider.GetNodeOrNull("SliderValue")?.Set("text", $"{currentDifficulty}");

        LockRowIfRunInProgress(diffSliderRow, nslider);

        GD.Print($"[Sts2Unlimited] Difficulty slider configured: range [{DIFFICULTY_MIN},{DIFFICULTY_MAX}], current={currentDifficulty}");
    }

    // Sub-toggle of the main difficulty override: by default the override only applies once
    // real player count > 1 (see Sts2Unlimited.GetEffectivePlayerCount), so a solo run keeps
    // normal 1-player balance. This lets a player opt into the override applying to solo play
    // too, e.g. as a self-imposed challenge mode. Row visibility (shown only while the main
    // toggle is on) is handled by the caller via ConfigureDifficultyToggle's dependentRows.
    private static void ConfigureSingleplayerToggle(Type tickboxType, Node spToggleRow)
    {
        var nameLabel = spToggleRow.GetNodeOrNull("Label");
        if (nameLabel != null)
            nameLabel.Set("text", "Also Scale in Singleplayer");
        else
            GD.PrintErr("[Sts2Unlimited] 'Label' not found in spToggleRow.");

        Node tickbox = FindNodeByType(spToggleRow, tickboxType);
        if (tickbox == null)
        {
            GD.PrintErr("[Sts2Unlimited] NMuteInBackgroundTickbox not found in spToggleRow.");
            return;
        }

        if (tickbox is GodotObject go) _ourTickboxInstances.Add(go);

        DisconnectExistingToggledHandlers(tickbox);

        tickbox.Set("IsTicked", Sts2Unlimited.DifficultyScaleSingleplayerEnabled);

        tickbox.Connect("Toggled", Callable.From<GodotObject>(sender =>
        {
            bool ticked = (bool)sender.Get("IsTicked");
            Sts2Unlimited.DifficultyScaleSingleplayerEnabled = ticked;
            SaveSettings();
        }));

        LockRowIfRunInProgress(spToggleRow, tickbox);

        GD.Print("[Sts2Unlimited] Singleplayer scaling toggle configured.");
    }

    // ── Lock settings while a run is active ─────────────────────────────────
    // Mirrors the native pattern in NSettingsScreen._Ready, which grays out and disables
    // %LanguageLine/%LanguageDropdown/%ModdingButton while RunManager.IsInProgress: gray out
    // the row (modulate cascades to all children) and disable input on the interactive control.
    // Changing player count or difficulty scaling mid-run wouldn't retroactively rescale
    // anything already in play, so it's locked to avoid a misleading "nothing happened" UX.

    private static Color? _stsGray;

    private static Color? GetStsGray()
    {
        if (_stsGray != null) return _stsGray;
        var colorsType = Type.GetType("MegaCrit.Sts2.Core.Helpers.StsColors, sts2", false);
        var field = colorsType?.GetField("gray", BindingFlags.Public | BindingFlags.Static);
        if (field?.GetValue(null) is Color c) _stsGray = c;
        return _stsGray;
    }

    private static bool IsRunInProgress()
    {
        var runManagerType = Type.GetType("MegaCrit.Sts2.Core.Runs.RunManager, sts2", false);
        var instance = runManagerType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        if (instance == null) return false;
        return instance.GetType().GetProperty("IsInProgress")?.GetValue(instance) as bool? ?? false;
    }

    private static void LockRowIfRunInProgress(Node row, Node interactiveControl)
    {
        if (!IsRunInProgress()) return;

        if (GetStsGray() is Color gray)
            row.Set("modulate", gray); // cascades to Label + control since both are children of row

        if (interactiveControl == null) return;

        if (interactiveControl.HasMethod("Disable"))
            interactiveControl.Call("Disable"); // NClickableControl (tickbox)
        else if (interactiveControl is Control control) // NSlider has no Disabled flag of its own
        {
            control.MouseFilter = Control.MouseFilterEnum.Ignore;
            control.FocusMode   = Control.FocusModeEnum.None;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    // Strips any Toggled listeners carried over onto the duplicate by Duplicate(15)'s
    // DUPLICATE_SIGNALS flag (e.g. the real tickbox's own listener, whose target isn't part of
    // the duplicated subtree and doesn't get cleanly remapped). GetSignalConnectionList can
    // return entries that Disconnect no longer considers live by the time we get to them —
    // guard with IsConnected so that shows up as a silent skip instead of a Godot engine ERROR.
    private static void DisconnectExistingToggledHandlers(Node tickbox)
    {
        foreach (var conn in tickbox.GetSignalConnectionList("Toggled"))
        {
            var callable = conn["callable"].As<Callable>();
            if (tickbox.IsConnected("Toggled", callable))
                tickbox.Disconnect("Toggled", callable);
        }
    }

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
                          $"\"DifficultyPlayers\":{Sts2Unlimited.DifficultyPlayersOverride}," +
                          $"\"DifficultySingleplayer\":{(Sts2Unlimited.DifficultyScaleSingleplayerEnabled ? "true" : "false")}}}";
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception e) { GD.PrintErr($"[Sts2Unlimited] Failed to save settings: {e.Message}"); }
    }
}
