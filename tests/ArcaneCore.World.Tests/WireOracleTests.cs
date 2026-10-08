using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests;

public sealed class WireOracleTests
{
    [Fact]
    public void ServerOpcodeReaderAcceptsCompleteCharCreate()
    {
        var oracle = new WireOracle();
        oracle.Observe(WorldOpcode.SmsgCharCreate, new byte[] { 0x2f });
        oracle.AssertValid();
    }

    [Fact]
    public void ServerOpcodeReaderRejectsTrailingByte()
    {
        var oracle = new WireOracle();
        oracle.Observe(WorldOpcode.SmsgCharCreate, new byte[] { 0x2f, 0 });
        Assert.Contains("unread trailing bytes", Assert.ThrowsAny<Exception>(oracle.AssertValid).Message);
    }

    [Fact]
    public async Task WorldTestHostObservesAuthChallenge()
    {
        var oracle = new WireOracle();
        await using var host = WorldTestHost.Start(configureServices: services =>
            services.AddSingleton<IOutboundPacketObserver>(oracle));
        await using var client = await host.ConnectAsync();
        (WorldOpcode opcode, _) = await client.ReadAsync();
        Assert.Equal(WorldOpcode.SmsgAuthChallenge, opcode);
        // An unhooked oracle would pass AssertValid vacuously; prove the socket send path reached it.
        Assert.Equal(1, oracle.ObservedCount(WorldOpcode.SmsgAuthChallenge));
        oracle.AssertValid();
    }

    [Fact]
    public async Task ManagedSessionSendsReachTheOracle()
    {
        var oracle = new WireOracle();
        await using var host = WorldTestHost.Start(configureServices: services =>
            services.AddSingleton<IOutboundPacketObserver>(oracle));
        WorldSession session = await Playerbots.PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            Assert.True(oracle.ObservedCount(WorldOpcode.SmsgCharCreate) > 0, "the managed character-create reply must reach the oracle");
            oracle.AssertValid();
            session.Send(WorldOpcode.SmsgCharCreate, new byte[] { 0x2f, 0 });
            Assert.Contains("unread trailing bytes", Assert.ThrowsAny<Exception>(oracle.AssertValid).Message);
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public void ForceRootUsesVanillaPackedGuidAndCounter()
    {
        // vmangos Movement/MovementPacketSender.cpp:SendMovementFlagChangeToController:
        // build >1.8.4 packs the GUID; build >1.9.4 appends the u32 counter.
        var valid = new WireOracle();
        valid.Observe(WorldOpcode.SmsgForceMoveRoot, new byte[] { 1, 0x42, 1, 0, 0, 0 });
        valid.AssertValid();

        var malformed = new WireOracle();
        malformed.Observe(WorldOpcode.SmsgForceMoveRoot, new byte[] { 1, 0x42, 1, 0, 0 });
        Assert.Contains("packed GUID layout", Assert.ThrowsAny<Exception>(malformed.AssertValid).Message);
    }

    [Fact]
    public void ReputationListIdsUseFourBytes()
    {
        // vmangos Server/Packets/Misc.cpp:SetFactionVisible/SetFactionStanding::AppendBodyTo.
        var oracle = new WireOracle();
        oracle.Observe(WorldOpcode.SmsgSetFactionVisible, new byte[] { 7, 0, 0, 0 });
        oracle.Observe(WorldOpcode.SmsgSetFactionStanding,
            new byte[] { 1, 0, 0, 0, 7, 0, 0, 0, 100, 0, 0, 0 });
        oracle.AssertValid();

        var malformed = new WireOracle();
        malformed.Observe(WorldOpcode.SmsgSetFactionVisible, new byte[] { 7, 0 });
        Assert.Contains("one u32", Assert.ThrowsAny<Exception>(malformed.AssertValid).Message);
    }

    [Fact]
    public void TransferAbortAndCastResultFollowVmangosStatusLengths()
    {
        // vmangos Server/Packets/Misc.cpp:TransferAborted::AppendBodyTo;
        // Server/Packets/Spell.cpp:CastResult::AppendBodyTo.
        var oracle = new WireOracle();
        oracle.Observe(WorldOpcode.SmsgTransferAborted, new byte[] { 4 });
        oracle.Observe(WorldOpcode.SmsgCastResult, new byte[] { 1, 0, 0, 0, 0 });
        oracle.Observe(WorldOpcode.SmsgCastResult, new byte[] { 1, 0, 0, 0, 2, 12 });
        oracle.AssertValid();

        var malformed = new WireOracle();
        malformed.Observe(WorldOpcode.SmsgTransferAborted, new byte[] { 1, 0 });
        Assert.Contains("one u8", Assert.ThrowsAny<Exception>(malformed.AssertValid).Message);
    }

    [Fact]
    public void VariantLengthsStillGuardAllowlistedPackets()
    {
        // vmangos Server/Packets/Loot.cpp:operator<<(LootSlotItem) and
        // Movement/spline/MoveSplineInit.cpp:Launch stop form.
        var oracle = new WireOracle();
        byte[] loot = new byte[36];
        loot[8] = 1; // loot type
        loot[13] = 1; // one 22-byte item
        oracle.Observe(WorldOpcode.SmsgLootResponse, loot);
        byte[] stop = new byte[19];
        stop[0] = 1; stop[1] = 0x42; stop[18] = 1;
        oracle.Observe(WorldOpcode.SmsgMonsterMove, stop);

        // vmangos MovementInfo.h: 0x02000000 gates u64 GUID + four floats.
        byte[] transportHeartbeat = new byte[54];
        transportHeartbeat[0] = 1; transportHeartbeat[1] = 0x42;
        transportHeartbeat[5] = 2; // LE movement flags 0x02000000
        oracle.Observe(WorldOpcode.MsgMoveHeartbeat, transportHeartbeat);
        oracle.AssertValid();

        var malformed = new WireOracle();
        malformed.Observe(WorldOpcode.SmsgLootResponse, loot[..^1]);
        Assert.Contains("loot response expects", Assert.ThrowsAny<Exception>(malformed.AssertValid).Message);
    }

    [Fact]
    public void TransportCreateUsesVmangosMovementBlockLayout()
    {
        // vmangos Objects/Object.cpp:Object::BuildMovementUpdate and MovementInfo.h:
        // high transport flag, fixed u64 transport GUID and four float offsets.
        var packet = new PacketWriter(128);
        packet.WriteUInt32(1); // one update block
        packet.WriteByte(0); // has transport
        packet.WriteByte(2); // create object
        packet.WritePackedGuid(1);
        packet.WriteByte(4); // player object
        packet.WriteByte(0x20); // living movement block
        packet.WriteUInt32(0x02000000); // on transport
        packet.WriteUInt32(1); // movement time
        for (int i = 0; i < 4; i++) packet.WriteSingle(0); // x/y/z/orientation
        packet.WriteUInt64(2); // fixed transport GUID
        for (int i = 0; i < 4; i++) packet.WriteSingle(0); // transport offsets
        packet.WriteUInt32(0); // fall time
        for (int i = 0; i < 6; i++) packet.WriteSingle(1); // six movement speeds
        packet.WriteByte(1); // one update mask word
        packet.WriteUInt32(1); // field zero
        packet.WriteUInt32(1); // field value

        var oracle = new WireOracle();
        oracle.Observe(WorldOpcode.SmsgUpdateObject, packet.ToArray());
        oracle.AssertValid();

        var malformed = new WireOracle();
        malformed.Observe(WorldOpcode.SmsgUpdateObject, packet.ToArray()[..^1]);
        Assert.Contains("truncated vmangos update", Assert.ThrowsAny<Exception>(malformed.AssertValid).Message);
    }

    [Fact]
    public void CombinedUnitAndCorpseOrObjectTargetsCarryOnePackedGuid()
    {
        // vmangos Spells/SpellCastTargetsInfo.cpp:SpellCastTargets::write uses one
        // packed GUID for UNIT combined with CORPSE or GAMEOBJECT; wowm reads each flag.
        byte[] start = Convert.FromHexString("01010101CC1F0F0002006400000002800102");
        byte[] go = Convert.FromHexString("01010101CC1F0F0000010102000000000000000002800102");
        byte[] objectGo = Convert.FromHexString("01010101001E0F0000010104000000000000000002080104");
        byte[] reflectedGo = Convert.FromHexString("010201027F1F0F000001000101000000000000000B0002000101");
        var oracle = new WireOracle();
        oracle.Observe(WorldOpcode.SmsgSpellStart, start);
        oracle.Observe(WorldOpcode.SmsgSpellGo, go);
        oracle.Observe(WorldOpcode.SmsgSpellGo, objectGo);
        oracle.Observe(WorldOpcode.SmsgSpellGo, reflectedGo);
        oracle.AssertValid();

        var malformed = new WireOracle();
        malformed.Observe(WorldOpcode.SmsgSpellStart, start[..^1]);
        Assert.Contains("truncated combined", Assert.ThrowsAny<Exception>(malformed.AssertValid).Message);
    }
}
