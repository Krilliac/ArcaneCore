using System.Reflection;
using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Battlegrounds;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Battlegrounds;

/// <summary>
/// (1) Going AFK inside a battleground runs the full leave sequence, deserter included (vmangos <c>Player::ToggleAFK</c>, Player.cpp:1734-1745).
/// (2) The port into a match ends a taxi flight first (vmangos HandleBattleFieldPortOpcode, BattleGroundHandler.cpp:461-464).
/// </summary>
public sealed class BattlegroundAfkTaxiTests
{
    private const uint Gryphon = 1000;
    private const uint GryphonDisplay = 2000;

    /// <summary>A Warsong Gulch match in a bare world, with the spell and NPC-services features next to the battleground feature.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly ServiceProvider _services;
        private int _nextPlayer = 1;

        public Harness()
        {
            ServiceCollection collection = new();
            collection.AddLogging();
            collection.AddSingleton<ILogger<TeleportFeature>>(NullLogger<TeleportFeature>.Instance);
            collection.AddSingleton<TeleportFeature>();
            collection.AddSingleton(sp => new SpellFeature(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<SpellFeature>.Instance));
            collection.AddSingleton(sp => new NpcServicesFeature(sp, NullLogger<NpcServicesFeature>.Instance));
            _services = collection.BuildServiceProvider();
            World = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 },
                new CharacterSaveQueue(_services.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance),
                NullLogger<WorldRuntime>.Instance);
            _services.GetRequiredService<TeleportFeature>().Attach(World);
            Spells = _services.GetRequiredService<SpellFeature>();
            Spells.Attach(World);
            Spells.System.Store = new SpellStore([DeserterSpell()], [], []);

            Feature = new BattlegroundFeature(_services, _services.GetRequiredService<IServiceScopeFactory>(), NullLoggerFactory.Instance);
            Feature.Attach(World);
            Feature.Manager.RegisterTemplate(new BattlegroundTemplate
            {
                Type = BattlegroundType.WarsongGulch,
                MapId = BattlegroundManager.MapOfType(BattlegroundType.WarsongGulch),
                Name = "Warsong Gulch",
                MinPlayersPerTeam = 5,
                MaxPlayersPerTeam = 10,
                MinLevel = 10,
                MaxLevel = 60,
                AllianceStart = new BattlegroundStartLocation(1f, 2f, 3f, 0f),
                HordeStart = new BattlegroundStartLocation(4f, 5f, 6f, 0f),
            });
            Battleground = Feature.Manager.CreateNewBattleground(BattlegroundType.WarsongGulch, 0) ?? throw new InvalidOperationException("no match");
            Map = (Feature.FindMatch(Battleground.InstanceId) ?? throw new InvalidOperationException("no match runtime")).Map
                ?? throw new InvalidOperationException("the match has no map");
        }

        public WorldRuntime World { get; }

        public SpellFeature Spells { get; }

        public BattlegroundFeature Feature { get; }

        public Battleground Battleground { get; }

        public ArcaneCore.Game.Maps.Map Map { get; }

        public Player AddPlayer()
        {
            int id = _nextPlayer++;
            var character = new CharacterRecord
            {
                Id = id, AccountId = 1, Name = "Afktaxi" + id, Race = 1, Class = 1, Gender = 0, Level = 60, MapId = Map.MapId,
            };
            var player = new Player(character, new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), new NullPlayerSession());
            player.Relocate(10f, 10f, 10f, 0f, 0);
            World.AddPlayer(player, Map);
            return player;
        }

        /// <summary>A queue entrant who accepted the invitation and entered the match (the state a player is in when it plays).</summary>
        public Player AddParticipant()
        {
            Player player = AddPlayer();
            Feature.Manager.BindPlayer(player.Guid, Battleground, Team.Alliance);
            Feature.Manager.StateOf(player.Guid).SetInvite(Battleground.Template.QueueType, Battleground.InstanceId);
            Assert.True(Feature.Manager.EnterBattleground(player.Guid), "setup: the invitee enters the match");
            return player;
        }

        public BattlegroundWorldHost Host() => new(Feature);

        /// <summary>Install a taxi flight system that knows one two-node path on the match map, and start a flight for <paramref name="player"/>.</summary>
        public TaxiFlightSystem StartFlight(Player player)
        {
            uint map = Map.MapId;
            var pathNodes = new TaxiPathNodeCatalog(
            [
                new TaxiPathNodeRecord(1, 10, 0, map, 0, 0, 10, 0, 0),
                new TaxiPathNodeRecord(2, 10, 1, map, 300, 0, 10, 0, 0),
            ]);
            NpcContent content = NpcContent.Empty with
            {
                TaxiNodes =
                [
                    new TaxiNode { Id = 1, MapId = map, X = 0, Y = 0, Z = 10, MountAlliance = Gryphon, MountHorde = Gryphon },
                    new TaxiNode { Id = 2, MapId = map, X = 300, Y = 0, Z = 10, MountAlliance = Gryphon, MountHorde = Gryphon },
                ],
                TaxiPaths = [new TaxiPath { Id = 10, FromNode = 1, ToNode = 2, Price = 0 }],
            };
            var flights = new TaxiFlightSystem(new NpcStore(content), pathNodes, e => e == Gryphon ? GryphonDisplay : 0u, () => 0u);
            typeof(NpcServicesFeature).GetProperty(nameof(NpcServicesFeature.Flights), BindingFlags.Instance | BindingFlags.Public)!
                .SetValue(_services.GetRequiredService<NpcServicesFeature>(), flights);
            Assert.True(flights.ResumeFlight(player, new TaxiFlightRoute([1, 2], [10], [0]), Gryphon, (_, _) => true), "setup: the flight starts");
            Assert.True(flights.IsFlying(player), "setup: flying");
            return flights;
        }

        public void Dispose()
        {
            Feature.StopAsync().GetAwaiter().GetResult();
            World.Dispose();
            _services.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        private static SpellInfo DeserterSpell() => new()
        {
            Id = BattlegroundConstants.SpellDeserter, Name = "Deserter", RangeIndex = SpellConstants.RangeIndexSelfOnly, Range = new SpellRange(0, 0),
            Duration = new SpellDuration(900_000, 0, 900_000), SpellVisual = 1,
            Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, BaseDice = 1, DieSides = 1, TargetA = SpellImplicitTarget.UnitCaster, AuraType = AuraType.Dummy }, new(), new()],
        };
    }

    [Fact]
    public void GoingAfkInAMatch_LeavesIt_WithTheDeserterDebuff()
    {
        using var rig = new Harness();
        Player player = rig.AddParticipant();
        Assert.NotNull(rig.Feature.BattlegroundOf(player.Guid));
        Assert.NotNull(rig.Battleground.PlayerTeam(player.Guid));

        Assert.True(player.ToggleAfk());
        rig.Feature.OnPlayerWentAfk(player);

        Assert.Null(rig.Feature.BattlegroundOf(player.Guid));
        Assert.Null(rig.Battleground.PlayerTeam(player.Guid));
        Assert.True(rig.Spells.System.HasAura(player, BattlegroundConstants.SpellDeserter), "vmangos Player::LeaveBattleground casts Deserter on a running match");
    }

    [Fact]
    public void GoingAfkOutsideAMatch_ChangesNothing()
    {
        using var rig = new Harness();
        Player player = rig.AddPlayer();

        Assert.True(player.ToggleAfk());
        rig.Feature.OnPlayerWentAfk(player);

        Assert.Null(rig.Feature.BattlegroundOf(player.Guid));
        Assert.False(rig.Spells.System.HasAura(player, BattlegroundConstants.SpellDeserter));
        Assert.True(player.IsAfk);
    }

    [Fact]
    public void PortingIn_EndsATaxiFlight()
    {
        using var rig = new Harness();
        Player player = rig.AddPlayer();
        TaxiFlightSystem flights = rig.StartFlight(player);
        Assert.NotEqual(0u, (uint)(player.UnitFlags & UnitFlags.TaxiFlight));

        rig.Host().PrepareToPortIn(player.Guid);

        Assert.False(flights.IsFlying(player));
        Assert.Equal(0u, (uint)(player.UnitFlags & UnitFlags.TaxiFlight));
        Assert.Equal(0u, player.GetUInt32(UpdateFields.UnitFieldMountdisplayid));
    }

    [Fact]
    public void PortingIn_WithoutAFlight_LeavesThePlayerAlone()
    {
        using var rig = new Harness();
        Player player = rig.AddPlayer();

        rig.Host().PrepareToPortIn(player.Guid);

        Assert.True(player.IsAlive);
        Assert.Equal(0u, (uint)(player.UnitFlags & UnitFlags.TaxiFlight));
    }
}
