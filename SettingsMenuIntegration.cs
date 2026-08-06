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
    // Displayed range for the Max Players slider. ConfigureSlider derives the underlying
    // NSlider's internal [0, PLAYER_MAX-PLAYER_OFFSET] range from these at configure time.
    private const int PLAYER_MIN    = 2;
    private const int PLAYER_MAX    = 16;
    private const int PLAYER_OFFSET = PLAYER_MIN;                   // 2

    // Displayed range for the Difficulty Scaling slider. Exposed (internal, not private) so
    // Sts2Unlimited.LoadConfig can clamp a loaded value to the same range instead of duplicating
    // the bounds as separate literals.
    internal const int DIFFICULTY_MIN    = 1;
    internal const int DIFFICULTY_MAX    = 16;
    private  const int DIFFICULTY_OFFSET = DIFFICULTY_MIN;          // 1

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
            // Fresh screen instance means fresh duplicate rows below — anything registered from
            // a previous open is guaranteed stale (its Node has already been discarded).
            _ourTickboxInstances.Clear();

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
                try { ConfigureMaxPlayersSlider(sliderRow, Sts2Unlimited.MaxPlayersOverride); }
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
                    ConfigureDifficultySlider(diffSliderRow, Sts2Unlimited.DifficultyPlayersOverride);
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

    private static void ConfigureMaxPlayersSlider(Node sliderRow, int playerCount)
        => ConfigureSlider(sliderRow, "Max Players", PLAYER_MIN, PLAYER_MAX, PLAYER_OFFSET, playerCount,
            value => { Sts2Unlimited.MaxPlayersOverride = value; SaveSettings(); });

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
        DisconnectExistingHandlers(tickbox, "Toggled");

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

        GD.Print($"[Sts2Unlimited] Difficulty toggle configured: enabled={Sts2Unlimited.DifficultyOverrideEnabled}.");
    }

    private static void ConfigureDifficultySlider(Node diffSliderRow, int currentDifficulty)
        => ConfigureSlider(diffSliderRow, "Difficulty Scaling", DIFFICULTY_MIN, DIFFICULTY_MAX, DIFFICULTY_OFFSET, currentDifficulty,
            value => { Sts2Unlimited.DifficultyPlayersOverride = value; SaveSettings(); });

    // Shared by the Max Players and Difficulty Scaling rows — both duplicate an NMasterVolumeSlider
    // template and only differ in label text, value range/offset, and what the chosen value gets
    // written to. min/max/offset define the *displayed* range; the underlying NSlider always uses
    // an internal [0, max-offset] range (NSlider.UpdateHandlePosition assumes MinValue=0), so the
    // offset gets added back when reading a value out and subtracted when setting one in.
    private static void ConfigureSlider(Node row, string label, int min, int max, int offset,
        int currentValue, Action<int> onValueChanged)
    {
        var nameLabel = row.GetNodeOrNull("Label");
        if (nameLabel != null)
            nameLabel.Set("text", label);
        else
            GD.PrintErr($"[Sts2Unlimited] 'Label' not found in row for '{label}'.");

        var masterType = Type.GetType(
            "MegaCrit.Sts2.Core.Nodes.Screens.Settings.NMasterVolumeSlider, sts2", false);
        Node sliderControl = FindNodeByType(row, masterType);
        if (sliderControl == null)
        {
            GD.PrintErr($"[Sts2Unlimited] NMasterVolumeSlider not found in row for '{label}'.");
            return;
        }

        var nslider = sliderControl.GetNodeOrNull("Slider") as Godot.Range;
        if (nslider == null)
        {
            GD.PrintErr($"[Sts2Unlimited] 'Slider' child not found for '{label}'.");
            return;
        }

        // Disconnect existing handlers carried over from the template (e.g. NMasterVolumeSlider's
        // own OnValueChanged, which would otherwise modify master audio volume).
        DisconnectExistingHandlers(nslider, "value_changed");

        int internalMin = 0;
        int internalMax = max - offset;
        int internalValue = Math.Clamp(currentValue - offset, internalMin, internalMax);

        nslider.MinValue = internalMin;
        nslider.MaxValue = internalMax;
        nslider.Step     = 1;
        nslider.Value    = internalValue;
        // Snap the visual handle to the correct position immediately
        nslider.Call("SetValueWithoutAnimation", (double)internalValue);

        nslider.Connect(Godot.Range.SignalName.ValueChanged, Callable.From<double>(v =>
        {
            int value = (int)Math.Round(v) + offset;
            onValueChanged(value);
            sliderControl.GetNodeOrNull("SliderValue")?.Set("text", $"{value}");
        }));

        // Initial value display
        sliderControl.GetNodeOrNull("SliderValue")?.Set("text", $"{currentValue}");

        LockRowIfRunInProgress(row, nslider);

        GD.Print($"[Sts2Unlimited] {label} slider configured: range [{min},{max}], current={currentValue}");
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

        DisconnectExistingHandlers(tickbox, "Toggled");

        tickbox.Set("IsTicked", Sts2Unlimited.DifficultyScaleSingleplayerEnabled);

        tickbox.Connect("Toggled", Callable.From<GodotObject>(sender =>
        {
            bool ticked = (bool)sender.Get("IsTicked");
            Sts2Unlimited.DifficultyScaleSingleplayerEnabled = ticked;
            SaveSettings();
        }));

        LockRowIfRunInProgress(spToggleRow, tickbox);

        GD.Print($"[Sts2Unlimited] Singleplayer scaling toggle configured: enabled={Sts2Unlimited.DifficultyScaleSingleplayerEnabled}.");
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

    // Strips any listeners carried over onto a duplicate by Duplicate(15)'s DUPLICATE_SIGNALS
    // flag (e.g. the real tickbox's/slider's own listener, whose target isn't part of the
    // duplicated subtree and doesn't get cleanly remapped). GetSignalConnectionList can return
    // entries that Disconnect no longer considers live by the time we get to them — guard with
    // IsConnected so that shows up as a silent skip instead of a Godot engine ERROR.
    private static void DisconnectExistingHandlers(GodotObject node, string signal)
    {
        foreach (var conn in node.GetSignalConnectionList(signal))
        {
            var callable = conn["callable"].As<Callable>();
            if (node.IsConnected(signal, callable))
                node.Disconnect(signal, callable);
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
