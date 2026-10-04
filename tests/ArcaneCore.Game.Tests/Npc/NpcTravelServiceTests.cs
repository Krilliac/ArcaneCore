using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Npc;

/// <summary>Innkeepers and flight masters (vmangos HandleBinderActivateOpcode, TaxiHandler.cpp, FlightPathMovementGenerator).</summary>
public sealed class NpcTravelServiceTests
{
    private const uint Gryphon = 3837;
    private const uint GryphonDisplay = 1147;
    private const float Z = 83.5f;

    private sealed class FakeMaps(bool instanceable, uint area) : IMapInfo
    {
        public bool IsInstanceable(uint mapId) => instanceable;

        public uint GetAreaId(uint mapId, float x, float y, float z) => area;
    }

    // ---- innkeeper ----------------------------------------------------------------------------

    [Fact]
    public void BinderActivate_MovesTheHomeBindAndAnswers()
    {
        using var kit = new NpcServiceKit(NpcFlags.Innkeeper | NpcFlags.Gossip, extra: new QuestNpcDependencies(Maps: new FakeMaps(false, 12)));
        kit.Services.BinderActivate(kit.Player, kit.Npc.Guid);
        Assert.Equal(new HomeBind(0, 12, kit.Player.X, kit.Player.Y, kit.Player.Z), kit.Player.Home);
        Assert.True(kit.Sink.CharacterChanges > 0);
        var sent = kit.Drain();
        var update = new PacketReader(Assert.Single(sent, p => p.Opcode == WorldOpcode.SmsgBindpointupdate).Payload);
        Assert.Equal(kit.Player.X, update.ReadSingle());
        Assert.Equal(kit.Player.Y, update.ReadSingle());
        Assert.Equal(kit.Player.Z, update.ReadSingle());
        Assert.Equal(0u, update.ReadUInt32());
        Assert.Equal(12u, update.ReadUInt32());
        Assert.Equal(0, update.Remaining);
        var bound = new PacketReader(Assert.Single(sent, p => p.Opcode == WorldOpcode.SmsgPlayerbound).Payload);
        Assert.Equal(kit.Npc.Guid.Value, bound.ReadUInt64());
        Assert.Equal(12u, bound.ReadUInt32());
        Assert.Contains(sent, p => p.Opcode == WorldOpcode.SmsgGossipComplete);
    }

    [Fact]
    public void BinderActivate_InAnInstanceDeadOrOutOfRange_DoesNothing()
    {
        HomeBind before;
        using (var instance = new NpcServiceKit(NpcFlags.Innkeeper, extra: new QuestNpcDependencies(Maps: new FakeMaps(true, 12))))
        {
            before = instance.Player.Home;
            instance.Services.BinderActivate(instance.Player, instance.Npc.Guid);
            Assert.Equal(before, instance.Player.Home);
            Assert.False(instance.Sent(WorldOpcode.SmsgPlayerbound));
        }

        using (var dead = new NpcServiceKit(NpcFlags.Innkeeper))
        {
            before = dead.Player.Home;
            dead.Player.Health = 0;
            dead.Services.BinderActivate(dead.Player, dead.Npc.Guid);
            Assert.Equal(before, dead.Player.Home);
            Assert.Empty(dead.Drain());
        }

        using (var far = new NpcServiceKit(NpcFlags.Innkeeper, npcDistance: 8))
        {
            before = far.Player.Home;
            far.Services.BinderActivate(far.Player, far.Npc.Guid);
            Assert.Equal(before, far.Player.Home);
            Assert.Empty(far.Drain());
        }

        using var notInnkeeper = new NpcServiceKit(NpcFlags.Vendor);
        before = notInnkeeper.Player.Home;
        notInnkeeper.Services.BinderActivate(notInnkeeper.Player, notInnkeeper.Npc.Guid);
        Assert.Equal(before, notInnkeeper.Player.Home);
    }

    // ---- flight masters -----------------------------------------------------------------------

    private static NpcContent TaxiContent() => NpcContent.Empty with
    {
        TaxiNodes =
        [
            new TaxiNode { Id = 1, MapId = 0, X = 0, Y = 0, Z = Z, MountAlliance = Gryphon, MountHorde = 2224 },
            new TaxiNode { Id = 2, MapId = 0, X = 64, Y = 0, Z = Z, MountAlliance = Gryphon, MountHorde = 2224 },
            new TaxiNode { Id = 3, MapId = 0, X = 64, Y = 64, Z = Z, MountAlliance = Gryphon, MountHorde = 2224 },
            new TaxiNode { Id = 4, MapId = 1, X = 10, Y = 10, Z = Z, MountAlliance = Gryphon, MountHorde = 2224 },
            new TaxiNode { Id = 5, MapId = 0, X = 500, Y = 0, Z = Z, MountAlliance = Gryphon, MountHorde = 2224 },
            new TaxiNode { Id = 6, MapId = 0, X = -3, Y = 0, Z = Z, MountAlliance = 9999, MountHorde = 9999 },
        ],
        TaxiPaths =
        [
            new TaxiPath { Id = 10, FromNode = 1, ToNode = 2, Price = 100 },
            new TaxiPath { Id = 11, FromNode = 2, ToNode = 3, Price = 50 },
            new TaxiPath { Id = 12, FromNode = 1, ToNode = 4, Price = 10 },
            new TaxiPath { Id = 13, FromNode = 5, ToNode = 1, Price = 10 },
            new TaxiPath { Id = 14, FromNode = 6, ToNode = 2, Price = 10 },
        ],
    };

    private static readonly TaxiPathNodeCatalog PathNodes = new(
    [
        new TaxiPathNodeRecord(1, 10, 0, 0, 0, 0, Z, 0, 0),
        new TaxiPathNodeRecord(2, 10, 1, 0, 32, 0, 90, 0, 0),
        new TaxiPathNodeRecord(3, 10, 2, 0, 64, 0, Z, 0, 0),
        new TaxiPathNodeRecord(4, 11, 0, 0, 64, 0, Z, 0, 0),
        new TaxiPathNodeRecord(5, 11, 1, 0, 64, 64, Z, 0, 0),
    ]);

    private sealed class TaxiRig : IDisposable
    {
        public TaxiRig(float discount = 1, bool learnAll = true)
        {
            NpcContent content = TaxiContent();
            Flights = new TaxiFlightSystem(new NpcStore(content), PathNodes, e => e == Gryphon ? GryphonDisplay : 0u, () => 0u);
            Flights.Landed += (_, node) => Landings.Add(node);
            Kit = new NpcServiceKit(NpcFlags.FlightMaster | NpcFlags.Gossip, content,
                new QuestNpcDependencies(Flights: Flights, Reputation: new NpcVendorServiceTests.FixedReputation(discount)));
            if (learnAll)
            {
                Kit.State.TaxiMask[0] = 0b111111;
            }

            Map = Kit.World.GetMap(0);
        }

        public NpcServiceKit Kit { get; }

        public TaxiFlightSystem Flights { get; }

        public List<uint> Landings { get; } = [];

        public Map Map { get; }

        public Player Player => Kit.Player;

        public void Dispose() => Kit.Dispose();
    }

    private static uint Reply(NpcServiceKit kit) => BitConverter.ToUInt32(kit.Single(WorldOpcode.SmsgActivatetaxireply));

    [Fact]
    public void TaxiQueryAvailableNodes_LearnsTheNearestNodeFirstThenShowsTheMap()
    {
        using var rig = new TaxiRig(learnAll: false);
        NpcServiceKit kit = rig.Kit;
        kit.Services.TaxiQueryAvailableNodes(kit.Player, kit.Npc.Guid);
        var sent = kit.Drain();
        Assert.Contains(sent, p => p.Opcode == WorldOpcode.SmsgNewTaxiPath);
        byte[] status = Assert.Single(sent, p => p.Opcode == WorldOpcode.SmsgTaxinodeStatus).Payload;
        Assert.Equal([.. BitConverter.GetBytes(kit.Npc.Guid.Value), (byte)1], status);
        Assert.Equal(1u, kit.State.TaxiMask[0]);
        Assert.Equal(1u, kit.Sink.TaxiMasks[^1][0]);

        kit.Services.TaxiQueryAvailableNodes(kit.Player, kit.Npc.Guid);
        var r = new PacketReader(kit.Single(WorldOpcode.SmsgShowtaxinodes));
        Assert.Equal(1u, r.ReadUInt32());
        Assert.Equal(kit.Npc.Guid.Value, r.ReadUInt64());
        Assert.Equal(1u, r.ReadUInt32());
        Assert.Equal(1u, r.ReadUInt32());
        for (int i = 1; i < NpcStore.TaxiMaskSize; i++)
        {
            Assert.Equal(0u, r.ReadUInt32());
        }

        Assert.Equal(0, r.Remaining);

        kit.Services.TaxiNodeStatusQuery(kit.Player, kit.Npc.Guid);
        Assert.Equal(1, kit.Single(WorldOpcode.SmsgTaxinodeStatus)[8]);
    }

    [Fact]
    public void TaxiQueryAvailableNodes_OutOfRangeOrDead_SendsNothing()
    {
        using var rig = new TaxiRig(learnAll: false);
        rig.Kit.Npc = rig.Kit.Npc with { X = 30 };
        rig.Kit.Services.TaxiQueryAvailableNodes(rig.Player, rig.Kit.Npc.Guid);
        Assert.Empty(rig.Kit.Drain());
        rig.Kit.Npc = rig.Kit.Npc with { X = 1 };
        rig.Player.Health = 0;
        rig.Kit.Services.TaxiQueryAvailableNodes(rig.Player, rig.Kit.Npc.Guid);
        Assert.Empty(rig.Kit.Drain());
        Assert.Equal(0u, rig.Kit.State.TaxiMask[0]);
    }

    [Fact]
    public void ActivateTaxi_ChargesMountsAndFliesThePathToTheDestination()
    {
        using var rig = new TaxiRig();
        NpcServiceKit kit = rig.Kit;
        kit.Player.Money = 150;
        kit.Services.ActivateTaxi(kit.Player, kit.Npc.Guid, 1, 2);
        Assert.Equal(50u, kit.Player.Money);
        Assert.True(rig.Flights.IsFlying(kit.Player));
        Assert.Equal(GryphonDisplay, kit.Player.GetUInt32(UpdateFields.UnitFieldMountdisplayid));
        Assert.Equal(UnitFlags.RemoveClientControl | UnitFlags.TaxiFlight,
            kit.Player.UnitFlags & (UnitFlags.RemoveClientControl | UnitFlags.TaxiFlight));

        var sent = kit.Drain();
        Assert.Equal(0u, BitConverter.ToUInt32(Assert.Single(sent, p => p.Opcode == WorldOpcode.SmsgActivatetaxireply).Payload));
        var move = new PacketReader(Assert.Single(sent, p => p.Opcode == WorldOpcode.SmsgMonsterMove).Payload);
        Assert.Equal(0x01, move.ReadByte()); // packed guid mask
        Assert.Equal(0x01, move.ReadByte()); // guid low byte 1
        Assert.Equal(0f, move.ReadSingle());
        Assert.Equal(0f, move.ReadSingle());
        Assert.Equal(Z, move.ReadSingle());
        Assert.Equal(1u, move.ReadUInt32());
        Assert.Equal(0, move.ReadByte());
        Assert.Equal(0x300u, move.ReadUInt32());
        double length = 2 * Math.Sqrt((32 * 32) + (6.5 * 6.5));
        Assert.Equal((uint)Math.Ceiling(length / 32 * 1000), move.ReadUInt32());
        Assert.Equal(2u, move.ReadUInt32());
        Assert.Equal((32f, 0f, 90f), (move.ReadSingle(), move.ReadSingle(), move.ReadSingle()));
        Assert.Equal((64f, 0f, Z), (move.ReadSingle(), move.ReadSingle(), move.ReadSingle()));
        Assert.Equal(0, move.Remaining);

        // Client movement is ignored in flight; the server moves the player along the path.
        rig.Flights.Update(rig.Map, 1000);
        Assert.True(rig.Flights.IsFlying(kit.Player));
        Assert.InRange(kit.Player.X, 30f, 32f);
        Assert.InRange(kit.Player.Z, Z, 90f);

        rig.Flights.Update(rig.Map, 1100);
        Assert.False(rig.Flights.IsFlying(kit.Player));
        Assert.Equal((64f, 0f, Z), (kit.Player.X, kit.Player.Y, kit.Player.Z));
        Assert.Equal(0u, kit.Player.GetUInt32(UpdateFields.UnitFieldMountdisplayid));
        Assert.Equal((UnitFlags)0, kit.Player.UnitFlags & (UnitFlags.RemoveClientControl | UnitFlags.TaxiFlight));
        Assert.Equal([2u], rig.Landings);
        Assert.Contains(kit.Drain(), p => p.Opcode == WorldOpcode.SmsgMonsterMove); // the stop
    }

    [Fact]
    public void ActivateTaxiExpress_SumsEveryHopAndChainsTheSplines()
    {
        using var rig = new TaxiRig(discount: 0.95f);
        NpcServiceKit kit = rig.Kit;
        kit.Player.Money = 1000;
        kit.Services.ActivateTaxiExpress(kit.Player, kit.Npc.Guid, [1, 2, 3]);
        Assert.Equal(905u, kit.Player.Money); // first leg: uint(100 * 0.95f + 0.5f)
        kit.Drain();

        rig.Flights.Update(rig.Map, 2100); // end of hop 1 → hop 2 starts from node 2
        Assert.True(rig.Flights.IsFlying(kit.Player));
        Assert.Equal(857u, kit.Player.Money); // second leg: uint(50 * 0.95f + 0.5f)
        Assert.Equal((64f, 0f, Z), (kit.Player.X, kit.Player.Y, kit.Player.Z));
        var hop2 = new PacketReader(kit.Single(WorldOpcode.SmsgMonsterMove));
        hop2.ReadBytes(2 + 12 + 4 + 1 + 4);
        Assert.Equal(2000u, hop2.ReadUInt32()); // 64 yd at 32 yd/s
        Assert.Equal(1u, hop2.ReadUInt32());
        Assert.Equal((64f, 64f, Z), (hop2.ReadSingle(), hop2.ReadSingle(), hop2.ReadSingle()));

        rig.Flights.Update(rig.Map, 1000);
        Assert.Equal(32f, kit.Player.Y, 0.01f);
        rig.Flights.Update(rig.Map, 1000);
        Assert.False(rig.Flights.IsFlying(kit.Player));
        Assert.Equal((64f, 64f), (kit.Player.X, kit.Player.Y));
        Assert.Equal([3u], rig.Landings);
    }

    [Fact]
    public void ActivateTaxi_InAShapeshiftFormThatCannotMount_RepliesShapeshiftedAndChargesNothing()
    {
        // vmangos Player::ActivateTaxiPathTo -> IsInDisallowedMountForm (Player.cpp:17872-17880): cat form is 1.
        using var rig = new TaxiRig();
        NpcServiceKit kit = rig.Kit;
        kit.Player.Money = 1000;
        kit.Player.SetByte(UpdateFields.UnitFieldBytes1, 2, 1);

        kit.Services.ActivateTaxi(kit.Player, kit.Npc.Guid, 1, 2);

        Assert.Equal((uint)ActivateTaxiReply.PlayerShapeshifted, Reply(kit));
        Assert.Equal(1000, (long)kit.Player.Money);
    }

    [Fact]
    public void ActivateTaxi_InAStanceOrStealthForm_IsNotRefusedAsShapeshifted()
    {
        // Battle stance (0x11) is an allowed form (Unit.cpp:5870-5878).
        using var rig = new TaxiRig();
        NpcServiceKit kit = rig.Kit;
        kit.Player.Money = 1000;
        kit.Player.SetByte(UpdateFields.UnitFieldBytes1, 2, 0x11);

        kit.Services.ActivateTaxi(kit.Player, kit.Npc.Guid, 1, 2);

        Assert.DoesNotContain(kit.Drain(), p => p.Opcode == WorldOpcode.SmsgActivatetaxireply
            && BitConverter.ToUInt32(p.Payload) == (uint)ActivateTaxiReply.PlayerShapeshifted);
    }

    [Fact]
    public void ActivateTaxi_Refusals_ChargeNothing()
    {
        using var rig = new TaxiRig();
        NpcServiceKit kit = rig.Kit;
        kit.Player.Money = 99;
        kit.Services.ActivateTaxi(kit.Player, kit.Npc.Guid, 1, 2);
        Assert.Equal((uint)ActivateTaxiReply.NotEnoughMoney, Reply(kit));

        kit.Player.Money = 1000;
        kit.Services.ActivateTaxi(kit.Player, kit.Npc.Guid, 5, 1);
        Assert.Equal((uint)ActivateTaxiReply.TooFarAway, Reply(kit));

        kit.Player.SetUInt32(UpdateFields.UnitFieldMountdisplayid, 2410);
        kit.Services.ActivateTaxi(kit.Player, kit.Npc.Guid, 1, 2);
        Assert.Equal((uint)ActivateTaxiReply.PlayerAlreadyMounted, Reply(kit));
        kit.Player.SetUInt32(UpdateFields.UnitFieldMountdisplayid, 0);

        kit.Player.UnitFlags |= UnitFlags.InCombat;
        kit.Services.ActivateTaxi(kit.Player, kit.Npc.Guid, 1, 2);
        Assert.Equal((uint)ActivateTaxiReply.PlayerBusy, Reply(kit));
        kit.Player.UnitFlags &= ~UnitFlags.InCombat;

        // A path that leaves the map cannot be flown: the server error, no charge.
        kit.Services.ActivateTaxi(kit.Player, kit.Npc.Guid, 1, 4);
        Assert.Equal((uint)ActivateTaxiReply.UnspecifiedServerError, Reply(kit));

        // A mount creature without a display cannot be flown either.
        kit.Services.ActivateTaxi(kit.Player, kit.Npc.Guid, 6, 2);
        Assert.Equal((uint)ActivateTaxiReply.UnspecifiedServerError, Reply(kit));

        // No path between the nodes, or an unknown node: silently ignored (vmangos).
        kit.Services.ActivateTaxi(kit.Player, kit.Npc.Guid, 1, 3);
        kit.State.TaxiMask[0] &= ~0b10u;
        kit.Services.ActivateTaxi(kit.Player, kit.Npc.Guid, 1, 2);
        Assert.False(kit.Sent(WorldOpcode.SmsgActivatetaxireply));

        Assert.Equal(1000u, kit.Player.Money);
        Assert.False(rig.Flights.IsFlying(kit.Player));
    }

    [Fact]
    public void ActivateTaxi_DeadOutOfRangeOrAlreadyFlying_IsIgnored()
    {
        using var rig = new TaxiRig();
        NpcServiceKit kit = rig.Kit;
        kit.Player.Money = 1000;
        kit.Npc = kit.Npc with { X = 20 };
        kit.Services.ActivateTaxi(kit.Player, kit.Npc.Guid, 1, 2);
        kit.Npc = kit.Npc with { X = 1 };
        kit.Player.Health = 0;
        kit.Services.ActivateTaxi(kit.Player, kit.Npc.Guid, 1, 2);
        Assert.Empty(kit.Drain());
        Assert.Equal(1000u, kit.Player.Money);

        kit.Player.Health = kit.Player.MaxHealth;
        kit.Services.ActivateTaxi(kit.Player, kit.Npc.Guid, 1, 2);
        Assert.Equal(900u, kit.Player.Money);
        kit.Drain();
        kit.Services.ActivateTaxi(kit.Player, kit.Npc.Guid, 1, 2);
        Assert.Empty(kit.Drain());
        Assert.Equal(900u, kit.Player.Money);
    }

    [Fact]
    public void Flight_IsAbortedByATeleportOrLeavingTheMap_AndLandNowPutsThePlayerDown()
    {
        using (var drift = new TaxiRig())
        {
            drift.Player.Money = 1000;
            drift.Kit.Services.ActivateTaxi(drift.Player, drift.Kit.Npc.Guid, 1, 2);
            drift.Player.Relocate(10, 10, Z, 0, 0);
            drift.Flights.Update(drift.Map, 100);
            Assert.False(drift.Flights.IsFlying(drift.Player));
            Assert.Equal(0u, drift.Player.GetUInt32(UpdateFields.UnitFieldMountdisplayid));
            Assert.Empty(drift.Landings);
            Assert.Equal((10f, 10f), (drift.Player.X, drift.Player.Y));
        }

        using (var removed = new TaxiRig())
        {
            removed.Player.Money = 1000;
            removed.Kit.Services.ActivateTaxi(removed.Player, removed.Kit.Npc.Guid, 1, 2);
            removed.Flights.OnPlayerRemoved(removed.Map, removed.Player);
            Assert.Equal(0, removed.Flights.ActiveFlights);
            Assert.Equal((UnitFlags)0, removed.Player.UnitFlags & UnitFlags.TaxiFlight);
        }

        using var logout = new TaxiRig();
        logout.Player.Money = 1000;
        logout.Kit.Services.ActivateTaxiExpress(logout.Player, logout.Kit.Npc.Guid, [1, 2, 3]);
        logout.Flights.LandNow(logout.Player);
        Assert.Equal((64f, 64f, Z), (logout.Player.X, logout.Player.Y, logout.Player.Z));
        Assert.False(logout.Flights.IsFlying(logout.Player));
        Assert.Equal([3u], logout.Landings);
    }

    [Fact]
    public void Flight_SuspendsAndResumesAtTheCurrentLegWithoutChargingItAgain()
    {
        using var rig = new TaxiRig();
        rig.Player.Money = 1000;
        rig.Kit.Services.ActivateTaxiExpress(rig.Player, rig.Kit.Npc.Guid, [1, 2, 3]);
        Assert.Equal(900u, rig.Player.Money);
        rig.Flights.Update(rig.Map, 1000);
        float x = rig.Player.X;
        TaxiFlightRoute route = Assert.IsType<TaxiFlightRoute>(rig.Flights.SuspendForLogout(rig.Player));
        Assert.Equal([1u, 2u, 3u], route.Nodes);
        Assert.Equal([0u, 50u], route.LegCosts);
        Assert.False(rig.Flights.IsFlying(rig.Player));
        Assert.True(rig.Flights.ResumeFlight(rig.Player, route, Gryphon, (player, amount) =>
        {
            player.Money -= amount;
            return true;
        }));
        Assert.Equal(900u, rig.Player.Money);
        Assert.InRange(rig.Player.X, x - 0.01f, x + 0.01f);
        rig.Flights.Update(rig.Map, 1200);
        Assert.Equal(850u, rig.Player.Money);
        rig.Flights.Update(rig.Map, 2000);
        Assert.False(rig.Flights.IsFlying(rig.Player));
        Assert.Equal((64f, 64f), (rig.Player.X, rig.Player.Y));
    }

    [Fact]
    public void Flight_DeathReturnsToItsCurrentDepartureNodeAndDismounts()
    {
        using var rig = new TaxiRig();
        rig.Player.Money = 1000;
        rig.Kit.Services.ActivateTaxi(rig.Player, rig.Kit.Npc.Guid, 1, 2);
        rig.Flights.Update(rig.Map, 500);
        rig.Player.Health = 0;
        rig.Flights.Update(rig.Map, 100);
        Assert.False(rig.Flights.IsFlying(rig.Player));
        Assert.Equal((0f, 0f, Z), (rig.Player.X, rig.Player.Y, rig.Player.Z));
        Assert.Equal(0u, rig.Player.GetUInt32(UpdateFields.UnitFieldMountdisplayid));
    }

    [Fact]
    public void BuildFlightMove_MatchesTheMonsterMoveFlyingLayout()
    {
        byte[] packet = TaxiFlightSystem.BuildFlightMove(ObjectGuid.Player(0x0102), 1, 2, 3, 7, 1234,
            [new TaxiFlightSystem.Waypoint(4, 5, 6)]);
        var r = new PacketReader(packet);
        Assert.Equal(0b11, r.ReadByte());
        Assert.Equal(0x02, r.ReadByte());
        Assert.Equal(0x01, r.ReadByte());
        Assert.Equal((1f, 2f, 3f), (r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
        Assert.Equal(7u, r.ReadUInt32());
        Assert.Equal(0, r.ReadByte());
        Assert.Equal(0x100u | 0x200u, r.ReadUInt32());
        Assert.Equal(1234u, r.ReadUInt32());
        Assert.Equal(1u, r.ReadUInt32());
        Assert.Equal((4f, 5f, 6f), (r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void StartFlight_ValidatesItsArguments()
    {
        using var rig = new TaxiRig();
        Assert.False(rig.Flights.StartFlight(rig.Player, [1], [], Gryphon));
        Assert.False(rig.Flights.StartFlight(rig.Player, [1, 2], [10, 11], Gryphon));
        Assert.False(rig.Flights.StartFlight(rig.Player, [1, 2], [10], 1));
        Assert.False(rig.Flights.StartFlight(rig.Player, [6, 2], [14], Gryphon)); // no TaxiPathNode rows
        Assert.True(rig.Flights.StartFlight(rig.Player, [1, 2], [10], Gryphon));
        Assert.False(rig.Flights.StartFlight(rig.Player, [1, 2], [10], Gryphon));
        Assert.Equal(3, rig.Flights.CurrentHop(rig.Player)!.Count);
    }
}
