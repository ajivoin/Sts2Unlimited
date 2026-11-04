using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

namespace Sts2Unlimited;

/// <summary>
/// Carries the host's difficulty-scaling player count to every peer at run start.
///
/// Monster HP and block are derived state: the game never replicates them, it relies on every
/// peer computing the same number from the same replicated player list and gates that with
/// ChecksumTracker. The difficulty override feeds that math, so unless every peer uses the same
/// value the run dies with a State Divergence at the first fight.
///
/// The game's message-id registry picks this type up on its own — MessageTypes.Initialize()
/// folds in ReflectionHelper.GetSubtypesInMods() — but those ids are *positional*, so a peer
/// running a different set of message-adding mods can route a foreign message into our handler.
/// Hence the sentinel: a payload that does not start with it is dropped, never applied.
/// </summary>
/// <remarks>
/// A struct, like every vanilla message: the bus instantiates an inbound message with
/// Activator.CreateInstance and then calls Deserialize through IPacketSerializable, so the box
/// handed to the handlers is the one Deserialize wrote into. The implicit parameterless
/// constructor leaves Sentinel at 0, which is exactly the "not from us" state IsValid rejects.
/// </remarks>
public struct DifficultySyncMessage : INetMessage, IPacketSerializable
{
    /// <summary>ASCII "DIF1". Change this if the payload layout ever changes.</summary>
    public const int SentinelV1 = 0x44494631;

    /// <summary>The host has no override active, so peers scale by the real player count.</summary>
    public const int NoOverride = 0;

    private const int SentinelBits = 32;
    private const int CountBits    = 8;

    public int Sentinel       { get; private set; }
    public int EffectiveCount { get; private set; }

    public DifficultySyncMessage(int effectiveCount) : this()
    {
        Sentinel       = SentinelV1;
        EffectiveCount = effectiveCount;
    }

    public bool IsValid => Sentinel == SentinelV1;

    // Mirrors LobbySeedChangedMessage and LobbyBeginRunMessage.
    // LogLevel must be fully qualified: the property name shadows the enum type.
    public bool            ShouldBroadcast => true;
    public NetTransferMode Mode            => NetTransferMode.Reliable;
    public LogLevel        LogLevel        => MegaCrit.Sts2.Core.Logging.LogLevel.VeryDebug;
    public bool            ShouldBuffer    => true;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteInt(Sentinel, SentinelBits);
        writer.WriteInt(EffectiveCount, CountBits);
    }

    public void Deserialize(PacketReader reader)
    {
        Sentinel       = reader.ReadInt(SentinelBits);
        EffectiveCount = reader.ReadInt(CountBits);
    }
}
