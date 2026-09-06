using System;
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

        Console.WriteLine(_failures == 0 ? "\nALL TESTS PASSED" : $"\n{_failures} TEST(S) FAILED");
        return _failures == 0 ? 0 : 1;
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
