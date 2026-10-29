using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Duel.DuelRig;

namespace ArcaneCore.Game.Tests.GameObjects;

/// <summary>
/// A game object placed at runtime reaches nearby players as UPDATETYPE_CREATE_OBJECT2 (vmangos Map::Add sets IsNewObject around its
/// UpdateObjectVisibility, and the create block writes CREATE_OBJECT2 for a new object); once the tick is over it is an ordinary object.
/// </summary>
public sealed class SummonedCreateTypeTests
{
    private static byte[] Marker(ObjectUpdateType type, GameObject go)
    {
        var writer = new PacketWriter(16);
        writer.WriteByte((byte)type);
        writer.WritePackedGuid(go.Guid.Value);
        return writer.ToArray();
    }

    private static bool Carries(Duel.DuelRig rig, byte[] marker)
        => rig.SessionA.Sent.ToArray().Any(p => p.Opcode == WorldOpcode.SmsgUpdateObject && p.Payload.AsSpan().IndexOf(marker) >= 0);

    [Fact]
    public void ASummonedObject_IsCreatedWithCreateObject2_ForANearbyPlayer()
    {
        using var rig = new Duel.DuelRig();
        rig.World.Options.UpdateCompressionThreshold = 0; // plain SMSG_UPDATE_OBJECT, so the block header can be read
        rig.ClearPackets();

        GameObject go = rig.Objects.Summon(FlagEntry, 11, 10, rig.A.Z, 0f)!;
        rig.Tick();

        Assert.True(Carries(rig, Marker(ObjectUpdateType.CreateObject2, go)), "no CREATE_OBJECT2 block for the summoned object");
        Assert.False(Carries(rig, Marker(ObjectUpdateType.CreateObject, go)), "a plain CREATE_OBJECT block was sent for it");
    }
}
