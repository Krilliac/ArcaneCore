using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Kernel.WorldData;
using Xunit;

namespace ArcaneCore.Game.Tests.Death;

/// <summary>
/// The per-world death seams and the scheduled repop (vmangos Player::ScheduleRepopAtGraveyard,
/// Player.cpp:4980-4986, deferred until no movement change is pending, Player.cpp:1329-1334).
/// </summary>
public sealed class DeathSeamsTests
{
    private sealed class CountingGraveyards : IGraveyardRepop
    {
        public List<Player> Calls { get; } = [];

        public bool RepopAtGraveyard(Player player)
        {
            Calls.Add(player);
            return true;
        }
    }

    /// <summary>A world with the DEFAULT combat hooks (no test hooks), one dead-and-released player and the given seam.</summary>
    private static (WorldRuntime World, Map Map, Player Player, FakeSession Session) Create(IGraveyardRepop? seam)
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        DeathHooks.Register(world, new DeathHooks(new DeathOptions(), new FixedDeathClock(1_700_000_000)));
        if (seam is not null)
        {
            Assert.True(DeathSeams.Of(world).TryRegisterGraveyards(seam));
        }

        var session = new FakeSession(3);
        Player player = CombatTestKit.AddPlayer(world, 3, 10, 20, session);
        world.RunTick(1);
        session.Clear();
        return (world, world.GetMap(0), player, session);
    }

    private static void Die(Map map, Player player)
    {
        player.Health = 0;
        map.Combat.KillPlayer(player);
    }

    [Fact]
    public void Seams_AreKeptPerWorld()
    {
        using WorldRuntime one = TestWorld.CreateRuntime();
        using WorldRuntime two = TestWorld.CreateRuntime();
        var seam = new CountingGraveyards();

        Assert.True(DeathSeams.Of(one).TryRegisterGraveyards(seam));

        Assert.Same(seam, DeathSeams.Find(one)!.Graveyards);
        Assert.Null(DeathSeams.Find(two));
        Assert.Null(DeathSeams.Of(two).Graveyards);
    }

    [Fact]
    public void FirstGraveyardRegistrationWins()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        var first = new CountingGraveyards();

        Assert.True(DeathSeams.Of(world).TryRegisterGraveyards(first));
        Assert.False(DeathSeams.Of(world).TryRegisterGraveyards(new CountingGraveyards()));

        Assert.Same(first, DeathSeams.Of(world).Graveyards);
    }

    [Fact]
    public void ReplacingTheDeathHooks_DoesNotDropTheSeams()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        var seam = new CountingGraveyards();
        DeathSeams.Of(world).TryRegisterGraveyards(seam);

        DeathHooks.Register(world, new DeathHooks(new DeathOptions(), DeathClock.System));

        Assert.Same(seam, DeathSeams.Of(world).Graveyards);
    }

    [Fact]
    public void Repop_CallsTheGraveyardSeamOnce_OnlyAfterTheWaterWalkOrderIsAnswered()
    {
        var seam = new CountingGraveyards();
        (WorldRuntime world, Map map, Player player, _) = Create(seam);
        using (world)
        {
            Die(map, player);

            Assert.True(map.Combat.RepopPlayer(player));
            Assert.True(player.Locomotion.Pending.HasPending); // the ghost's water-walk order
            world.RunTick(100);
            Assert.Empty(seam.Calls);                          // not while a movement change is pending

            CombatTestKit.AckPendingMovement(player);
            Assert.Empty(seam.Calls);
            world.RunTick(100);
            Assert.Single(seam.Calls);
            Assert.Same(player, seam.Calls[0]);

            world.RunTick(100);
            Assert.Single(seam.Calls);                         // the pending flag is cleared first (Player.cpp:4992)
        }
    }

    [Fact]
    public void WithoutASeam_TheGhostStaysOnItsBody()
    {
        (WorldRuntime world, Map map, Player player, _) = Create(null);
        using (world)
        {
            Die(map, player);
            float x = player.X;

            Assert.True(map.Combat.RepopPlayer(player));
            CombatTestKit.AckPendingMovement(player);
            world.RunTick(100);

            Assert.Equal(x, player.X);
            Assert.NotEqual(0u, (uint)(player.Flags & PlayerFlags.Ghost));
        }
    }

    [Fact]
    public void ALoggingOutSpirit_IsSentAtOnce_NotScheduled()
    {
        var seam = new CountingGraveyards();
        (WorldRuntime world, Map map, Player player, _) = Create(seam);
        using (world)
        {
            Die(map, player);
            player.Combat.DeathTimer = 1000;
            player.BeginLogout(0);

            map.Combat.OnPlayerLeaving(player);

            Assert.Single(seam.Calls); // WorldSession.cpp:694-701 repops synchronously
        }
    }

    [Fact]
    public void ImmediateHookStillWorks_ForTheUndermapPath()
    {
        var seam = new CountingGraveyards();
        (WorldRuntime world, Map map, Player player, _) = Create(seam);
        using (world)
        {
            bool moved = map.Combat.Hooks.RepopAtGraveyard(player);

            Assert.True(moved);
            Assert.Single(seam.Calls);
        }
    }

    private static MapContent Content(params MapTemplate[] maps) => new(maps, [], [], [], []);

    [Fact]
    public void Instanceable_ReadsTheMapTemplate_WhenOneIsLoaded()
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        using (world)
        {
            WorldMaps.Of(world).Load(Content(
                new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""),
                new MapTemplate(13, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Test", ""),
                new MapTemplate(33, 0, MapType.Instance, 0, 10, 0, 0, 1, 2, "Shadowfang Keep", ""),
                new MapTemplate(30, 0, MapType.Battleground, 0, 40, 0, -1, 0, 0, "Alterac Valley", "")));
            MapCombat combat = world.GetMap(0).Combat;

            Assert.False(combat.IsInstanceableMap(0));
            Assert.False(combat.IsInstanceableMap(13)); // a common map that is not a continent: "not 0/1" said true
            Assert.True(combat.IsInstanceableMap(33));
            Assert.True(combat.IsInstanceableMap(30));
            Assert.True(combat.IsInstanceableMap(99));  // unknown map: the old default
        }
    }
}
