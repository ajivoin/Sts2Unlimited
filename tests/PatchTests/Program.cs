using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using Sts2Unlimited;

internal static class Tests
{
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
        RequiredBitsMath();
        TranspilerRewritesRealGameIl();

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
}
