using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using Xunit;

namespace ArcaneCore.Game.Tests;

/// <summary>
/// The per-map update seam (<see cref="IMapUpdater"/>, docs/integration/seams.md): default
/// systems on every map, attach order, fault isolation and the player-removed notification.
/// </summary>
public sealed class MapUpdaterTests
{
    [Fact]
    public void EveryMap_GetsCombat_AsItsFirstUpdater()
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        Assert.Contains(typeof(MapCombat), DefaultMapUpdaters.Types);
        Assert.Equal(typeof(MapCombat), DefaultMapUpdaters.Types[0]);

        foreach (uint mapId in new uint[] { 0, 1, 489 })
        {
            Map map = world.GetMap(mapId);
            Assert.IsType<MapCombat>(map.Updaters[0]);
            Assert.Single(map.Updaters.OfType<MapCombat>());
            Assert.Same(map.Updaters[0], map.Combat);
        }

        Assert.NotSame(world.GetMap(0).Combat, world.GetMap(1).Combat);
        Assert.Same(world.GetMap(0).Combat, world.GetMap(0).Combat); // created once per map
    }

    [Fact]
    public void Updaters_RunEveryTick_InAttachOrder_AndAFailingOneDoesNotStopTheOthers()
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var calls = new List<string>();
        map.AddUpdater(new Probe("first", calls, fail: true));
        map.AddUpdater(new Probe("second", calls));
        Assert.Equal("first", map.FindUpdater<Probe>()!.Name);

        world.RunTick(50);
        world.RunTick(50);

        Assert.Equal(["first:50", "second:50", "first:50", "second:50"], calls);
    }

    [Fact]
    public void LeavingTheWorld_NotifiesEveryUpdater_BeforeThePlayersMapStateIsCleared()
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var calls = new List<string>();
        var probe = new Probe("probe", calls);
        map.AddUpdater(probe);
        var session = new FakeSession();
        Player player = TestWorld.CreatePlayer(1, 0, 0, session);
        world.AddPlayer(player);

        world.LogoutPlayer(player);

        Assert.Equal(["probe:removed P1 from its map"], calls);
        Assert.Null(player.Map);
        Assert.Equal(1, session.LoggedOutCount);
    }

    [Fact]
    public void ALoggedOutPlayer_HasNoCombatLeft()
    {
        (WorldRuntime world, Map map, _, _) = CombatTestKit.CreateWorld();
        Player a = CombatTestKit.AddPlayer(world, 1, 0, 0, new FakeSession());
        Player v = CombatTestKit.AddPlayer(world, 2, 1, 0, new FakeSession());
        map.Combat.TogglePvp(a, true);
        map.Combat.TogglePvp(v, true);
        Assert.True(map.Combat.Attack(a, v));

        world.LogoutPlayer(v);

        Assert.Null(a.Combat.Victim);
        Assert.Empty(v.Combat.Attackers);
    }

    private sealed class Probe(string name, List<string> calls, bool fail = false) : IMapUpdater
    {
        public string Name { get; } = name;

        public void Update(Map map, uint diffMs)
        {
            calls.Add($"{Name}:{diffMs}");
            if (fail)
            {
                throw new InvalidOperationException("updater bug");
            }
        }

        public void OnPlayerRemoved(Map map, Player player)
            => calls.Add($"{Name}:removed {player.Name}{(ReferenceEquals(player.Map, map) ? " from its map" : "")}");
    }
}
