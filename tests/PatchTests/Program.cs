using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.IO;
using System.Text.RegularExpressions;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Saves.Runs;
using Sts2Unlimited;

internal static class Tests
{
    private static readonly List<string> _log = new();
    private static int _failures;

    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine(ok ? $"PASS  {name}" : $"FAIL  {name}{(detail.Length > 0 ? " — " + detail : "")}");
        if (!ok) _failures++;
    }

    private static void AreEqual<T>(string name, T expected, T actual)
        => Check(name, Equals(expected, actual), $"expected {expected}, got {actual}");

    private static int Main()
    {
        // The game's logger segfaults outside a game process; capture instead.
        ListHeaderWidthPatch.LogSink = (level, message) => _log.Add($"{level}: {message}");

        RequiredBitsMath();
        TranspilerRewritesRealGameIl();
        PatchTargetsResolve();
        SettingsPanelResizeSeam();
        DocumentedPlayerRangeMatchesTheCode();
        TryWidenSemantics();
        // Must run before the guard is applied — afterwards a vanilla-width read is corrected.
        UnguardedWireFormat();
        GuardWidensVanillaWidthCalls();
        GuardWarnsWhenThereIsNothingToGuard();
        DifficultySyncMessageRoundTrips();
        DifficultyDecisionTable();
        HpFormulaStillMatchesTheBugReport();
        ReceivingHostValue();
        SyncPatchTargetsResolve();
        HostBroadcastRoleGate();

        Console.WriteLine(_failures == 0 ? "\nALL TESTS PASSED" : $"\n{_failures} TEST(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    // Runs the real transpiler over the real IL of the real game methods — the exact call
    // sites the bug report blames. Guards both halves of R1: the rewrite happens, and the
    // element type behind it is recorded. Asserts on the *count* rather than the type name,
    // because the name changes between game builds and the mod is meant to survive that.
    private static void TranspilerRewritesRealGameIl()
    {
        AssertTranspile("LobbyBeginRunMessage.Serialize",
            AccessTools.Method(typeof(LobbyBeginRunMessage), "Serialize", new[] { typeof(PacketWriter) }));
        AssertTranspile("LobbyBeginRunMessage.Deserialize",
            AccessTools.Method(typeof(LobbyBeginRunMessage), "Deserialize", new[] { typeof(PacketReader) }));
        AssertTranspile("ClientLobbyJoinResponseMessage.Serialize",
            AccessTools.Method(typeof(ClientLobbyJoinResponseMessage), "Serialize", new[] { typeof(PacketWriter) }));
        AssertTranspile("ClientLobbyJoinResponseMessage.Deserialize",
            AccessTools.Method(typeof(ClientLobbyJoinResponseMessage), "Deserialize", new[] { typeof(PacketReader) }));
    }

    private static void AssertTranspile(string label, MethodBase method)
    {
        if (method == null) { Check($"{label} resolves", false, "method not found on sts2.dll"); return; }

        PacketSizePatch.ResetRewrittenListElementTypes();
        var before = PatchProcessor.GetOriginalInstructions(method);
        var after = PacketSizePatch.Transpile_SerializeDeserialize(before).ToList();

        int getRequiredBitsCalls = after.Count(c =>
            c.opcode == OpCodes.Call && c.operand is MethodInfo mi && mi.Name == nameof(PacketSizePatch.GetRequiredBits));
        AreEqual($"{label}: GetRequiredBits() call sites", 1, getRequiredBitsCalls);

        int leftoverThrees = 0, untouchedWide = 0;
        for (int i = 1; i < after.Count; i++)
        {
            if (after[i].operand is not MethodInfo mi) continue;
            if (mi.Name != "WriteList" && mi.Name != "ReadList") continue;
            if (after[i - 1].opcode == OpCodes.Ldc_I4_3) leftoverThrees++;
            if (after[i - 1].opcode == OpCodes.Ldc_I4_S && Convert.ToInt32(after[i - 1].operand) == 32) untouchedWide++;
        }
        AreEqual($"{label}: no vanilla 3 left before a list call", 0, leftoverThrees);
        AreEqual($"{label}: 32-bit list header untouched", 1, untouchedWide);

        var recorded = PacketSizePatch.RewrittenListElementTypes.Select(t => t.Name).ToList();
        AreEqual($"{label}: element types recorded ({string.Join(",", recorded)})", 1, recorded.Count);
    }

    // The wire width must stay fixed at 5 for AbsoluteMaxPlayers = 16, independent of the
    // local MaxPlayers setting — peers with different settings must still agree.
    private static void RequiredBitsMath()
    {
        AreEqual("RequiredBits(1)", 1, PacketSizePatch.RequiredBits(1));
        AreEqual("RequiredBits(4)", 3, PacketSizePatch.RequiredBits(4));
        AreEqual("RequiredBits(7)", 3, PacketSizePatch.RequiredBits(7));
        AreEqual("RequiredBits(8)", 4, PacketSizePatch.RequiredBits(8));
        AreEqual("RequiredBits(16)", 5, PacketSizePatch.RequiredBits(16));
        AreEqual("GetRequiredBits()", 5, PacketSizePatch.GetRequiredBits());
    }

    // Apply() can only patch what AccessTools resolves, and the prefix only works while
    // lengthBits stays the parameter it is today. Both are game-version-sensitive.
    private static void PatchTargetsResolve()
    {
        foreach (var t in RecordVanillaWidthElementTypes())
        {
            var write = AccessTools.Method(typeof(PacketWriter), "WriteList", null, new[] { t });
            Check($"WriteList<{t.Name}> resolves", write != null);
            if (write != null)
                AreEqual($"WriteList<{t.Name}> arg 1", "lengthBits", write.GetParameters()[1].Name);

            var read = AccessTools.Method(typeof(PacketReader), "ReadList", null, new[] { t });
            Check($"ReadList<{t.Name}> resolves", read != null);
            if (read != null)
                AreEqual($"ReadList<{t.Name}> arg 0", "lengthBits", read.GetParameters()[0].Name);
        }
    }

    // SettingsMenuIntegration appends rows to the General panel from a postfix on
    // NSettingsScreen._Ready, which runs after the panel has already sized itself to the vanilla
    // rows — so it nudges the panel afterwards with Call("RefreshSize"). Metadata only: every
    // assertion here is game-version-sensitive, and none of it can touch a Godot node headlessly.
    private static void SettingsPanelResizeSeam()
    {
        var panelType = Type.GetType(
            "MegaCrit.Sts2.Core.Nodes.Screens.Settings.NSettingsPanel, sts2", false);
        Check("NSettingsPanel resolves", panelType != null);
        if (panelType == null) return;

        var refreshSize = panelType.GetMethod("RefreshSize",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            null, Type.EmptyTypes, null);
        Check("NSettingsPanel.RefreshSize() resolves", refreshSize != null);

        // RefreshSize is private in C#. Call() reaches it only because the Godot source generator
        // registered it — the generated MethodName field is that registration's fingerprint.
        var methodNames = panelType.GetNestedType("MethodName", BindingFlags.Public | BindingFlags.NonPublic);
        Check("RefreshSize is registered as a Godot method (reachable via Call)",
            methodNames?.GetField("RefreshSize", BindingFlags.Public | BindingFlags.Static) != null);

        // The container RefreshSize measures. Our rows go into the same node, found via
        // %ModdingDivider's parent rather than this property, so this guards the relationship.
        var content = panelType.GetProperty("Content", BindingFlags.Public | BindingFlags.Instance);
        AreEqual("NSettingsPanel.Content is the content container", "VBoxContainer", content?.PropertyType.Name);

        // The bug's precondition: the panel's only eager sizing pass is in _Ready. If a future
        // build grows one that reacts to added rows, this fails and the nudge can go away.
        var ready = panelType.GetMethod("_Ready", BindingFlags.Public | BindingFlags.Instance);
        if (ready == null) { Check("NSettingsPanel._Ready resolves", false); return; }
        bool readyCallsRefresh = false;
        try
        {
            readyCallsRefresh = PatchProcessor.GetOriginalInstructions(ready)
                .Any(c => c.operand is MethodInfo mi && mi.Name == "RefreshSize");
        }
        catch (Exception e) { Check("NSettingsPanel._Ready IL is readable", false, e.Message); }
        Check("NSettingsPanel sizes itself from _Ready", readyCallsRefresh);
    }

    // The Workshop page and both READMEs advertised a Max Players range of 2-256 for several
    // releases while the slider was 2-16 — and the config-file read is unclamped, so readers who
    // believed it got a silently broken lobby. These are plain-text claims that no compiler
    // checks, so check them here: every documented range must match the constant behind it.
    private static void DocumentedPlayerRangeMatchesTheCode()
    {
        var root = RepoRoot();
        if (root == null) { Check("repository root is locatable", false); return; }

        int playerMax     = ConstantFromSource(root, "SettingsMenuIntegration.cs", "PLAYER_MAX");
        int difficultyMax = ConstantFromSource(root, "SettingsMenuIntegration.cs", "DIFFICULTY_MAX");
        int absoluteMax   = ConstantFromSource(root, "PacketSizePatch.cs", "AbsoluteMaxPlayers");

        // The wire format is sized for AbsoluteMaxPlayers, so a slider that goes higher would
        // overflow the player-list header rather than merely feel ambitious.
        AreEqual("the slider's ceiling is the ceiling the wire format is sized for", absoluteMax, playerMax);

        // "2 to 16", "2–16", "2-16" — the upper bound of any player range stated in prose.
        var playerRange = new Regex(@"\b2\s*(?:to|–|-)\s*(\d+)\b");
        var difficultyRange = new Regex(@"\b1\s*(?:to|–|-)\s*(\d+)\b");
        int rangesChecked = 0;

        foreach (var relative in new[] { "README.md", "MOD_README.md", "workshop/workshop.json" })
        {
            var path = Path.Combine(root, relative);
            if (!File.Exists(path)) { Check($"{relative} exists", false); continue; }
            var prose = File.ReadAllText(path);

            foreach (Match m in playerRange.Matches(prose))
            {
                rangesChecked++;
                AreEqual($"{relative}: player range 2-{m.Groups[1].Value}", playerMax, int.Parse(m.Groups[1].Value));
            }
            foreach (Match m in difficultyRange.Matches(prose))
            {
                rangesChecked++;
                AreEqual($"{relative}: difficulty range 1-{m.Groups[1].Value}", difficultyMax, int.Parse(m.Groups[1].Value));
            }
        }

        // Without this the whole check goes quiet if every file is reworded to drop its ranges.
        Check("the docs state a player range at all", rangesChecked > 0,
            "no range found in README.md, MOD_README.md or workshop/workshop.json");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "sts2unlimited.csproj")))
            dir = dir.Parent;
        return dir?.FullName;
    }

    private static int ConstantFromSource(string root, string file, string name)
    {
        var source = File.ReadAllText(Path.Combine(root, file));
        var match = Regex.Match(source, $@"\b{Regex.Escape(name)}\s*=\s*(\d+)");
        if (!match.Success) { Check($"{name} found in {file}", false); return -1; }
        return int.Parse(match.Groups[1].Value);
    }

    // Mirrors PacketSizePatch.Apply's discovery across the whole game assembly, without calling
    // Apply itself (which logs, and the game logger segfaults here). If a future build adds
    // another vanilla-width list, the count assertion below names it as a failure.
    private static List<Type> RecordVanillaWidthElementTypes()
    {
        PacketSizePatch.ResetRewrittenListElementTypes();

        // Headless, ~41 game types fail to load (Steamworks.NET is absent), and GetTypes()
        // throws rather than returning the rest. None of them are serialization messages —
        // an IL-level scan of the assembly finds all four vanilla-width call sites in the two
        // lobby message types, both of which load fine.
        Type[] gameTypes;
        try { gameTypes = typeof(PacketWriter).Assembly.GetTypes(); }
        catch (ReflectionTypeLoadException e) { gameTypes = e.Types.Where(t => t != null).ToArray(); }

        int scanned = 0;
        foreach (var type in gameTypes)
        {
            foreach (var m in new[]
            {
                type.GetMethod("Serialize", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(PacketWriter) }, null),
                type.GetMethod("Deserialize", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(PacketReader) }, null),
            })
            {
                if (m == null) continue;
                try
                {
                    PacketSizePatch.Transpile_SerializeDeserialize(PatchProcessor.GetOriginalInstructions(m)).ToList();
                    scanned++;
                }
                catch { /* a method whose IL will not resolve cannot be patched in-game either */ }
            }
        }

        // 257 on the current build; a large drop means type loading broke, not that the game
        // shrank, and would make the count assertion below meaningless.
        Check("swept the game's serialization methods", scanned > 200, $"only {scanned} were readable");

        var types = PacketSizePatch.RewrittenListElementTypes.ToList();
        AreEqual($"vanilla-width list element types across sts2.dll ({string.Join(",", types.Select(t => t.Name))})",
            1, types.Count);
        return types;
    }

    // R2/R3: only the vanilla 3 moves. An already-transpiled call site passes 5 and must not be
    // widened twice; 4 and 32 belong to unrelated lists.
    private static void TryWidenSemantics()
    {
        int three = 3;
        Check("TryWiden(3) reports a change", ListHeaderWidthPatch.TryWiden(ref three));
        AreEqual("TryWiden(3) widens to the wire width", 5, three);

        foreach (int untouched in new[] { 4, 5, 32 })
        {
            int value = untouched;
            Check($"TryWiden({untouched}) reports no change", !ListHeaderWidthPatch.TryWiden(ref value));
            AreEqual($"TryWiden({untouched}) leaves the value alone", untouched, value);
        }
    }

    // The real wire format before any guard is applied: a widened header costs exactly 5 bits,
    // and a peer reading it at the vanilla width lands 2 bits short — the desync in the report.
    // Only an empty list works headlessly; the element type's Serialize needs game state.
    private static void UnguardedWireFormat()
    {
        var elementType = PacketSizePatch.RewrittenListElementTypes.First();

        var writer = WriteEmptyList(elementType, PacketSizePatch.GetRequiredBits());
        AreEqual("widened empty-list header costs 5 bits", 5, writer.BitPosition);

        var matched = new PacketReader();
        matched.Reset(writer.Buffer);
        AreEqual("matched read returns an empty list", 0, ReadList(matched, elementType, PacketSizePatch.GetRequiredBits()).Count);
        AreEqual("matched read consumes 5 bits", 5, matched.BitPosition);

        var mismatched = new PacketReader();
        mismatched.Reset(writer.Buffer);
        ReadList(mismatched, elementType, PacketSizePatch.VanillaListBits);
        AreEqual("vanilla-width read desyncs by 2 bits", 3, mismatched.BitPosition);
    }

    // The fix itself, end to end: after Apply, a caller passing the vanilla constant — exactly
    // what an inlined, un-transpiled Serialize does — still produces a widened header.
    private static void GuardWidensVanillaWidthCalls()
    {
        var elementType = PacketSizePatch.RewrittenListElementTypes.First();
        _log.Clear();
        ListHeaderWidthPatch.Apply(new Harmony("sts2unlimited.tests.listheader"));
        Check("Apply logs what it guarded", _log.Any(l => l.Contains("Guarded")), string.Join(" | ", _log));

        var writer = WriteEmptyList(elementType, PacketSizePatch.VanillaListBits);
        AreEqual($"guarded WriteList<{elementType.Name}> widens a vanilla-width call", 5, writer.BitPosition);

        var reader = new PacketReader();
        reader.Reset(writer.Buffer);
        AreEqual("guarded round trip returns an empty list", 0, ReadList(reader, elementType, PacketSizePatch.VanillaListBits).Count);
        AreEqual($"guarded ReadList<{elementType.Name}> widens a vanilla-width call", 5, reader.BitPosition);

        // The guard must not leak onto other element types. SerializableModifier lists are
        // written at 32 in the game, so a 3 here has to stay 3.
        var control = new PacketWriter();
        control.Reset();
        control.WriteList(new List<SerializableModifier>(), PacketSizePatch.VanillaListBits);
        AreEqual("an unguarded element type keeps the vanilla width", 3, control.BitPosition);
    }

    // R7: a future build where nothing matches must say so rather than fail silently.
    private static void GuardWarnsWhenThereIsNothingToGuard()
    {
        PacketSizePatch.ResetRewrittenListElementTypes();
        _log.Clear();
        ListHeaderWidthPatch.Apply(new Harmony("sts2unlimited.tests.empty"));
        Check("an empty type set logs a warning",
            _log.Any(l => l.StartsWith("Warn") && l.Contains("inactive")), string.Join(" | ", _log));
    }

    // The host's value has to survive the game's own bit-packing unchanged, and a packet that
    // did not come from this mod has to be recognisable as foreign. Mod-added message ids are
    // positional, so a peer running a different set of message-adding mods can route someone
    // else's message in here; the sentinel is the only thing standing between that and a
    // silently applied bogus player count.
    private static void DifficultySyncMessageRoundTrips()
    {
        foreach (int count in new[] { DifficultySyncMessage.NoOverride, 1, 4, 16, 255 })
        {
            var writer = new PacketWriter();
            writer.Reset();
            new DifficultySyncMessage(count).Serialize(writer);

            var reader = new PacketReader();
            reader.Reset(writer.Buffer);
            var received = new DifficultySyncMessage();
            received.Deserialize(reader);

            Check($"round trip {count} is valid", received.IsValid);
            AreEqual($"round trip {count} preserves the count", count, received.EffectiveCount);
            AreEqual($"round trip {count} costs 40 bits", 40, writer.BitPosition);
        }

        // A foreign payload: right length, wrong sentinel.
        var foreign = new PacketWriter();
        foreign.Reset();
        foreign.WriteInt(0x0BADF00D, 32);
        foreign.WriteInt(7, 8);

        var foreignReader = new PacketReader();
        foreignReader.Reset(foreign.Buffer);
        var rejected = new DifficultySyncMessage();
        rejected.Deserialize(foreignReader);

        Check("a foreign sentinel is not valid", !rejected.IsValid);
    }

    // The whole fix in one table. Each row is a peer deciding what to scale by.
    private static void DifficultyDecisionTable()
    {
        var captured = new List<string>();
        DifficultySync.LogSink = (level, message) => captured.Add($"{level}: {message}");

        // Singleplayer is untouched by any of this.
        DifficultySync.Reset();
        AreEqual("solo, override on, sp-scaling off -> actual", 1,
            DifficultySync.Resolve(1, overrideEnabled: true, playersOverride: 9, scaleSingleplayerEnabled: false));
        AreEqual("solo, override on, sp-scaling on -> override", 9,
            DifficultySync.Resolve(1, overrideEnabled: true, playersOverride: 9, scaleSingleplayerEnabled: true));
        AreEqual("solo, override off -> actual", 1,
            DifficultySync.Resolve(1, overrideEnabled: false, playersOverride: 9, scaleSingleplayerEnabled: true));

        // Multiplayer with the host's value latched: the host's number wins, whatever ours says.
        DifficultySync.Reset();
        DifficultySync.MarkMultiplayerRun();
        DifficultySync.Latch(6, "test");
        AreEqual("mp, host says 6, we want 9 -> 6", 6,
            DifficultySync.Resolve(3, overrideEnabled: true, playersOverride: 9, scaleSingleplayerEnabled: false));

        // Host with no override: everyone scales by the real count, even peers who have one.
        DifficultySync.Reset();
        DifficultySync.MarkMultiplayerRun();
        DifficultySync.Latch(DifficultySyncMessage.NoOverride, "test");
        AreEqual("mp, host has no override -> actual", 3,
            DifficultySync.Resolve(3, overrideEnabled: true, playersOverride: 9, scaleSingleplayerEnabled: false));

        // Nothing latched (old-version host): fall back to the real count and say so once.
        DifficultySync.Reset();
        DifficultySync.MarkMultiplayerRun();
        captured.Clear();
        AreEqual("mp, unsynced -> actual", 3,
            DifficultySync.Resolve(3, overrideEnabled: true, playersOverride: 9, scaleSingleplayerEnabled: false));
        AreEqual("mp, unsynced -> actual again", 3,
            DifficultySync.Resolve(3, overrideEnabled: true, playersOverride: 9, scaleSingleplayerEnabled: false));
        AreEqual("unsynced warns exactly once", 1, captured.Count(l => l.StartsWith("Warn")));

        // ...but the next run must warn again, so Reset() has to clear the once-only flag.
        // Without this case, deleting `_warnedUnsynced = false` from Reset() passes every assertion.
        DifficultySync.Reset();
        DifficultySync.MarkMultiplayerRun();
        captured.Clear();
        AreEqual("mp, unsynced after reset -> actual", 3,
            DifficultySync.Resolve(3, overrideEnabled: true, playersOverride: 9, scaleSingleplayerEnabled: false));
        AreEqual("a new unsynced run warns again", 1, captured.Count(l => l.StartsWith("Warn")));

        // A latch arriving before the client's begin-run seam must survive it.
        DifficultySync.Reset();
        DifficultySync.Latch(6, "test");
        DifficultySync.MarkMultiplayerRun();
        AreEqual("latch survives MarkMultiplayerRun", 6,
            DifficultySync.Resolve(3, overrideEnabled: true, playersOverride: 9, scaleSingleplayerEnabled: false));

        // What the host puts on the wire.
        AreEqual("broadcast value with override on", 6,
            DifficultySync.ResolveLocalOverrideForBroadcast(overrideEnabled: true, playersOverride: 6));
        AreEqual("broadcast value with override off", DifficultySyncMessage.NoOverride,
            DifficultySync.ResolveLocalOverrideForBroadcast(overrideEnabled: false, playersOverride: 6));

        // Teardown clears everything, so the next run cannot inherit a stale value.
        DifficultySync.Reset();
        Check("reset clears the latch", !DifficultySync.TryGetHostEffectiveCount(out _));
        Check("reset clears the multiplayer flag", !DifficultySync.IsMultiplayerRun);
    }

    // Characterises the real game formula against the numbers in the bug report, so a future
    // game build that changes the scaling maths fails here loudly instead of silently making
    // the spec's arithmetic wrong. Base HP 39 is in ShrinkerBeetle's 38-40 roll; act index 0
    // scales by 1.1; SetMaxHpInternal truncates.
    private static void HpFormulaStillMatchesTheBugReport()
    {
        AreEqual("reporter's Local HP (4 players)", 171,
            (int)Creature.ScaleHpForMultiplayer(39m, null, 4, 0));
        AreEqual("reporter's Remote HP (5 players)", 214,
            (int)Creature.ScaleHpForMultiplayer(39m, null, 5, 0));
        AreEqual("one player short-circuits unscaled", 39,
            (int)Creature.ScaleHpForMultiplayer(39m, null, 1, 0));
    }

    // Drives the leaves by reflection so no test names the element type.
    // The receive path, driven directly: a valid message latches, a foreign one is dropped and
    // reported, and an unrelated game message is ignored without noise.
    private static void ReceivingHostValue()
    {
        var captured = new List<string>();
        DifficultySync.LogSink = (level, message) => captured.Add($"{level}: {message}");

        DifficultySync.Reset();
        DifficultySync.Prefix_SendMessageToAllHandlers(new DifficultySyncMessage(7), 42UL);
        Check("a valid message latches", DifficultySync.TryGetHostEffectiveCount(out int latched));
        AreEqual("a valid message latches its count", 7, latched);

        // A foreign payload decoded into our type: sentinel absent because it never round-tripped.
        DifficultySync.Reset();
        captured.Clear();
        DifficultySync.Prefix_SendMessageToAllHandlers(new DifficultySyncMessage(), 42UL);
        Check("a message with no sentinel is dropped", !DifficultySync.TryGetHostEffectiveCount(out _));
        AreEqual("dropping a foreign message warns", 1, captured.Count(l => l.StartsWith("Warn")));

        // Someone else's message must pass straight through.
        DifficultySync.Reset();
        captured.Clear();
        DifficultySync.Prefix_SendMessageToAllHandlers(new LobbySeedChangedMessage(), 42UL);
        Check("an unrelated message does not latch", !DifficultySync.TryGetHostEffectiveCount(out _));
        AreEqual("an unrelated message logs nothing", 0, captured.Count);
    }

    // The patch targets this task and Tasks 4-5 hook, resolved against the real sts2.dll. These
    // are the signatures that silently move between game builds; a rename shows up here rather
    // than as a mod that loads fine and syncs nothing.
    private static void SyncPatchTargetsResolve()
    {
        Check("NetMessageBus.SendMessageToAllHandlers resolves",
            AccessTools.Method(typeof(NetMessageBus), "SendMessageToAllHandlers",
                new[] { typeof(INetMessage), typeof(ulong) }) != null);
        Check("StartRunLobby.BeginRunForAllPlayers resolves",
            AccessTools.Method(typeof(StartRunLobby), "BeginRunForAllPlayers") != null);
        Check("StartRunLobby.BeginRunLocally resolves",
            AccessTools.Method(typeof(StartRunLobby), "BeginRunLocally") != null);
        Check("StartRunLobby.HandleLobbyBeginRunMessage resolves",
            AccessTools.Method(typeof(StartRunLobby), "HandleLobbyBeginRunMessage") != null);
        Check("StartRunLobby.NetService resolves",
            AccessTools.Property(typeof(StartRunLobby), "NetService") != null);
        Check("LoadRunLobby.TryBeginRunForAllPlayers resolves",
            AccessTools.Method(typeof(LoadRunLobby), "TryBeginRunForAllPlayers") != null);
        Check("LoadRunLobby.BeginRunLocally resolves",
            AccessTools.Method(typeof(LoadRunLobby), "BeginRunLocally") != null);
        Check("LoadRunLobby.HandleLobbyBeginRunMessage resolves",
            AccessTools.Method(typeof(LoadRunLobby), "HandleLobbyBeginRunMessage") != null);
        Check("LoadRunLobby.NetService resolves",
            AccessTools.Property(typeof(LoadRunLobby), "NetService") != null);
        Check("RunLobby.HandleClientRejoinRequestMessage resolves",
            AccessTools.Method(typeof(RunLobby), "HandleClientRejoinRequestMessage") != null);
        Check("RunLobby._netService resolves",
            AccessTools.Field(typeof(RunLobby), "_netService") != null);
        Check("RunLobby.Dispose resolves",
            AccessTools.Method(typeof(RunLobby), "Dispose") != null);
    }

    // Guards the Finding-1 fix: StartRunLobby.BeginRunLocally is reached from BOTH the host path
    // (BeginRunForAllPlayers) and the client path (HandleLobbyBeginRunMessage). Without gating the
    // broadcast decision on the service's actual role, a client handling the host's begin-run
    // message would latch its own setting as "local host" and clobber the value it just received.
    private static void HostBroadcastRoleGate()
    {
        // A client must not broadcast, must not mark the run, and must not touch the latch.
        DifficultySync.Reset();
        DifficultySync.Latch(6, "host");
        Check("client does not broadcast",
            !DifficultySync.TryBeginHostBroadcast(NetGameType.Client, out _));
        Check("client leaves the host's latch intact", DifficultySync.TryGetHostEffectiveCount(out int kept));
        AreEqual("client keeps the host's value", 6, kept);

        // Singleplayer and replay must never be treated as multiplayer runs.
        DifficultySync.Reset();
        Check("singleplayer does not broadcast",
            !DifficultySync.TryBeginHostBroadcast(NetGameType.Singleplayer, out _));
        Check("singleplayer is not a multiplayer run", !DifficultySync.IsMultiplayerRun);
        Check("replay does not broadcast",
            !DifficultySync.TryBeginHostBroadcast(NetGameType.Replay, out _));
        Check("replay is not a multiplayer run", !DifficultySync.IsMultiplayerRun);

        // A host broadcasts exactly once per run, latching its own resolved value.
        DifficultySync.Reset();
        DifficultySync.LocalOverrideEnabled = () => true;
        DifficultySync.LocalPlayersOverride = () => 7;
        Check("host broadcasts", DifficultySync.TryBeginHostBroadcast(NetGameType.Host, out int sent));
        AreEqual("host broadcasts its resolved value", 7, sent);
        Check("host marks the run as multiplayer", DifficultySync.IsMultiplayerRun);
        Check("host latched its own value", DifficultySync.TryGetHostEffectiveCount(out int own));
        AreEqual("host latched the same value it sent", 7, own);
        Check("host does not broadcast twice in one run",
            !DifficultySync.TryBeginHostBroadcast(NetGameType.Host, out _));

        // A new run broadcasts again, so Reset() must clear the once-per-run guard.
        DifficultySync.Reset();
        Check("a new run broadcasts again",
            DifficultySync.TryBeginHostBroadcast(NetGameType.Host, out _));

        DifficultySync.LocalOverrideEnabled = () => false;
        DifficultySync.LocalPlayersOverride = () => 0;

        // A null StartRunLobby instance (the prefix's null-conditional __instance?.NetService)
        // means the role is unknown, so this must degrade gracefully: warn, don't throw, and
        // don't mark the run as multiplayer.
        DifficultySync.Reset();
        var nullServiceLog = new List<string>();
        DifficultySync.LogSink = (level, message) => nullServiceLog.Add($"{level}: {message}");
        DifficultySync.Prefix_StartRunLobbyBeginRun(null);
        Check("null net service warns", nullServiceLog.Any(l => l.StartsWith("Warn")));
        Check("null net service does not mark multiplayer", !DifficultySync.IsMultiplayerRun);
    }

    private static PacketWriter WriteEmptyList(Type elementType, int lengthBits)
    {
        var writer = new PacketWriter();
        writer.Reset();
        var list = Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType));
        AccessTools.Method(typeof(PacketWriter), "WriteList", null, new[] { elementType })
            .Invoke(writer, new object[] { list, lengthBits });
        return writer;
    }

    private static ICollection ReadList(PacketReader reader, Type elementType, int lengthBits)
        => (ICollection)AccessTools.Method(typeof(PacketReader), "ReadList", null, new[] { elementType })
            .Invoke(reader, new object[] { lengthBits });
}
