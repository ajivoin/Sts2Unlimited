using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
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
        TryWidenSemantics();
        // Must run before the guard is applied — afterwards a vanilla-width read is corrected.
        UnguardedWireFormat();
        GuardWidensVanillaWidthCalls();
        GuardWarnsWhenThereIsNothingToGuard();

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

    // Drives the leaves by reflection so no test names the element type.
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
