using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Exploration;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Game.Tests.WorldState;

/// <summary>vmangos Player::CheckAreaExploreAndOutdoor (Player.cpp:6089-6204) and the SetPosition trigger (:5969-5985).</summary>
public sealed class ExplorationServiceTests
{
    private sealed class FlagLocator : IZoneLocator
    {
        public uint Flag = 35; // word 1, bit 3

        public bool CanDeriveZones { get; set; } = true;

        public Dictionary<uint, AreaTemplate> ByFlag { get; } = new()
        {
            [35] = new AreaTemplate(87, 0, 12, 35, 0, 10, "Goldshire", 0, 0),
            [36] = new AreaTemplate(88, 0, 12, 36, 0, 0, "Levelless", 0, 0),
        };

        public (uint ZoneId, uint AreaId) Locate(Map map, Player player) => (12, 87);

        public AreaTemplate? Find(uint areaId) => ByFlag.Values.FirstOrDefault(a => a.Entry == areaId);

        public uint GetAreaFlag(Map map, Player player) => Flag;

        public AreaTemplate? FindByAreaFlag(uint areaFlag, uint mapId) => ByFlag.GetValueOrDefault(areaFlag);
    }

    private sealed class RecordingExperience : IPlayerExperience
    {
        public List<(Player Player, uint Xp)> Given { get; } = [];

        public void GiveXp(Player player, uint xp)
        {
            Given.Add((player, xp));
            player.Session.Send(WorldOpcode.SmsgLogXpgain, [1]); // the marker the real GiveXp sends
        }
    }

    private sealed class Fixture
    {
        public WorldRuntime World { get; } = TestWorld.CreateRuntime();

        public FlagLocator Locator { get; } = new();

        public RecordingExperience Experience { get; } = new();

        public WorldStateHooks Hooks { get; }

        public Fixture(bool withTable = true)
        {
            Hooks = WorldStateHooks.For(World);
            Hooks.Locator = Locator;
            if (withTable)
            {
                Hooks.ExplorationBaseXp = new ExplorationBaseXpTable(Enumerable.Range(0, 70).Select(l => new KeyValuePair<uint, uint>((uint)l, (uint)l * 10)));
            }

            Hooks.Explorer = new ExplorationService(Hooks, () => Experience, () => 60u, NullLogger.Instance);
        }

        public (Player Player, FakeSession Session) Join(byte level = 10)
        {
            var session = new FakeSession();
            Player player = TestWorld.CreatePlayer(1, 0, 0, session);
            player.Level = level;
            World.AddPlayer(player);
            return (player, session);
        }
    }

    private static List<WorldOpcode> Opcodes(FakeSession session) => session.Sent.Select(p => p.Opcode).ToList();

    [Fact]
    public void NewFlag_SetsTheBit_GrantsXp_AndSendsLogXpGainBeforeTheExplorationPacket()
    {
        var f = new Fixture();
        (Player player, FakeSession session) = f.Join(10);

        f.World.RunTick(50);

        Assert.Equal(0x8u, player.GetUInt32(UpdateFields.PlayerExploredZones1 + 1));
        Assert.Equal([(player, 100u)], f.Experience.Given); // area level 10, player level 10: base(10) = 100
        Assert.Equal([WorldOpcode.SmsgLogXpgain, WorldOpcode.SmsgExplorationExperience], Opcodes(session).Where(o => o is WorldOpcode.SmsgLogXpgain or WorldOpcode.SmsgExplorationExperience));
        byte[] packet = session.Sent.Single(p => p.Opcode == WorldOpcode.SmsgExplorationExperience).Payload;
        Assert.Equal([87, 0, 0, 0, 100, 0, 0, 0], packet);
    }

    [Fact]
    public void ReenteringTheSameFlag_SendsNothing_AndRateDoublesTheXp()
    {
        var f = new Fixture();
        f.Hooks.ExplorationSettings.RateXp = 2.0f;
        (Player player, FakeSession session) = f.Join(10);
        f.World.RunTick(50);
        Assert.Equal(200u, f.Experience.Given.Single().Xp);
        session.Clear();

        player.SetPosition(player.X + 1, player.Y, player.Z, 0); // moved, same flag
        f.World.RunTick(50);

        Assert.DoesNotContain(WorldOpcode.SmsgExplorationExperience, Opcodes(session));
    }

    [Fact]
    public void AreaWithoutALevel_StillSendsThePacket_WithZeroXp_AndNoGrant()
    {
        var f = new Fixture();
        f.Locator.Flag = 36;
        (_, FakeSession session) = f.Join(10);

        f.World.RunTick(50);

        Assert.Empty(f.Experience.Given);
        Assert.Equal([88, 0, 0, 0, 0, 0, 0, 0], session.Sent.Single(p => p.Opcode == WorldOpcode.SmsgExplorationExperience).Payload);
    }

    [Fact]
    public void MaxLevelPlayer_GetsNoXp_ButTheRevealPacket()
    {
        var f = new Fixture();
        (_, FakeSession session) = f.Join(60);

        f.World.RunTick(50);

        Assert.Empty(f.Experience.Given);
        Assert.Equal([87, 0, 0, 0, 0, 0, 0, 0], session.Sent.Single(p => p.Opcode == WorldOpcode.SmsgExplorationExperience).Payload);
    }

    [Fact]
    public void UnknownFlag_SetsTheBit_AndSendsNothing()
    {
        var f = new Fixture();
        f.Locator.Flag = 40; // no area entry
        (Player player, FakeSession session) = f.Join(10);

        f.World.RunTick(50);

        Assert.Equal(1u << 8, player.GetUInt32(UpdateFields.PlayerExploredZones1 + 1));
        Assert.DoesNotContain(WorldOpcode.SmsgExplorationExperience, Opcodes(session));
        Assert.Empty(f.Experience.Given);
    }

    [Fact]
    public void WithoutABaseXpTable_TheXpIsZero_AndNoAreaFlagNoOutOfRangeAndDeadPlayersDiscoverNothing()
    {
        var f = new Fixture(withTable: false);
        (Player player, FakeSession session) = f.Join(10);
        f.World.RunTick(50);
        Assert.Equal([87, 0, 0, 0, 0, 0, 0, 0], session.Sent.Single(p => p.Opcode == WorldOpcode.SmsgExplorationExperience).Payload);

        uint discovered = player.GetUInt32(UpdateFields.PlayerExploredZones1 + 1);
        f.Locator.Flag = ExploredZones.NoAreaFlag; // terrain without area data
        player.SetPosition(player.X + 1, player.Y, player.Z, 0);
        f.World.RunTick(50);
        f.Locator.Flag = 2048; // beyond the 64 words: logged, ignored, no throw
        player.SetPosition(player.X + 1, player.Y, player.Z, 0);
        f.World.RunTick(50);

        Assert.Equal(discovered, player.GetUInt32(UpdateFields.PlayerExploredZones1 + 1)); // the earlier bit is untouched
        f.Locator.Flag = 36;
        player.Health = 0; // dead
        player.SetPosition(player.X + 1, player.Y, player.Z, 0);
        f.World.RunTick(50);
        Assert.Equal(0u, player.GetUInt32(UpdateFields.PlayerExploredZones1 + 1) & (1u << 4));
    }

    [Fact]
    public void WithoutAreaData_NothingIsDiscovered()
    {
        var f = new Fixture();
        f.Locator.CanDeriveZones = false;
        (Player player, FakeSession session) = f.Join(10);

        f.World.RunTick(50);

        Assert.Equal(0u, player.GetUInt32(UpdateFields.PlayerExploredZones1 + 1));
        Assert.DoesNotContain(WorldOpcode.SmsgExplorationExperience, Opcodes(session));
    }

    private sealed class CountingChecker : IExplorationChecker
    {
        public int Calls;

        public void CheckAreaExplore(Player player) => Calls++;
    }

    [Fact]
    public void TheCheckRunsOnFirstSight_AndWhenThePlayerMoves_NotWhileItStandsStill()
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        var checker = new CountingChecker();
        WorldStateHooks.For(world).Explorer = checker;
        Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
        world.AddPlayer(player);

        world.RunTick(50);
        Assert.Equal(1, checker.Calls);
        world.RunTick(50);
        world.RunTick(50);
        Assert.Equal(1, checker.Calls);

        player.SetPosition(player.X + 3, player.Y, player.Z, 0);
        world.RunTick(50);
        Assert.Equal(2, checker.Calls);
    }

    [Fact]
    public void RelocationCheckDelay_DefersTheCheckAfterAMove()
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        var checker = new CountingChecker();
        WorldStateHooks hooks = WorldStateHooks.For(world);
        hooks.Explorer = checker;
        hooks.Zones.RelocationCheckDelayMs = 500;
        Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
        world.AddPlayer(player);

        world.RunTick(50); // first sight arms the timer
        Assert.Equal(0, checker.Calls);
        world.RunTick(400);
        Assert.Equal(0, checker.Calls);
        world.RunTick(50);
        Assert.Equal(1, checker.Calls);
    }
}
