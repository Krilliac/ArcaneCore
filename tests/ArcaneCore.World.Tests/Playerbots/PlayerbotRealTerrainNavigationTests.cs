using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Teleport;
using ArcaneCore.World.Features;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Teleport;
using ArcaneCore.World.Tests.Playerbots.Dungeon;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// A fact that needs the live server's terrain extraction (<see cref="Variable"/>: the directory holding <c>maps/</c>,
/// <c>vmaps/</c> and <c>mmaps/</c>, the value of <c>World:Maps:DataDirectory</c>). The data is derived from the 1.12.1 client and
/// never committed; without it the test is reported Skipped, never a silent pass.
/// </summary>
internal sealed class RealTerrainBotFactAttribute : FactAttribute
{
    public const string Variable = "ARCANECORE_TEST_TERRAIN_DIR";

    public RealTerrainBotFactAttribute()
    {
        string? root = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(Path.Combine(root, "mmaps")))
            Skip = $"{Variable} is not set to a terrain data root with mmaps/: the real-terrain bot routes did NOT run.";
    }
}

/// <summary>
/// The places where managed bots stood still on the live server (2026-10-08, docs/integration/talents-bots-20261008.md "Still
/// open"), on the real terrain, heights, models and navigation meshes: a managed bot is put where the live one stood and its
/// route planning and its motion are checked there. Positions are the live characters' and the world database's creature spawns.
/// Time moves only on the manual world clock; every wait is for a state, bounded by game time.
/// </summary>
public sealed class PlayerbotRealTerrainNavigationTests(ITestOutputHelper output)
{
    // Undead start inside the Deathknell crypt (playercreateinfo race 5), where Graveweaver stood for hours.
    private static readonly Vector3 CryptStart = new(1676.35f, 1677.45f, 121.67f);

    // Undertaker Mordo (creature 1568), outside the crypt at the top of its stairs.
    private static readonly Vector3 Mordo = new(1678.99f, 1667.86f, 135.85f);

    // A spot on the crypt's stairs the navigation mesh covers but where the floor probe finds nothing.
    private static readonly Vector3 CryptStairs = new(1645.4f, 1665.9f, 132.6f);

    // Where Mirthblade stood after its revive with goal Quest, target 1718 (Rockjaw Raider) for quest 179, and that raider's spawn.
    private static readonly Vector3 ColdridgeAfterRevive = new(-6222.2f, 236.17f, 388.48f);
    private static readonly Vector3 RockjawRaider = new(-6150.35f, 50.82f, 416.67f);

    // Where Dawnrover stood (goal Quest, target 253) and William Pestle (creature 253), inside the Lion's Pride Inn.
    private static readonly Vector3 InnBesidePestle = new(-9465.6f, 35.2f, 57.0f);
    private static readonly Vector3 WilliamPestle = new(-9460.3f, 31.94f, 57.05f);

    // Mirthblade's ghost and its body in the rehearsal snapshot (359 yards apart, Dun Morogh).
    private static readonly Vector3 MirthbladeGhost = new(-6254.0f, 424.7f, 387.1f);
    private static readonly Vector3 MirthbladeBody = new(-6509.82f, 676.62f, 387.32f);

    /// <summary>
    /// Out of the crypt: a route from the undead start to Undertaker Mordo is planned, and the bot walks it to the quest giver.
    /// Before, the route's corners were re-tested with a line of sight at floor height, which hits the crypt's floor model: every
    /// route out of the crypt was refused (Graveweaver: goal Explore, never moved).
    /// </summary>
    [RealTerrainBotFact]
    public async Task FromTheCryptStart_TheBotWalksUpTheStairs_ToUndertakerMordo()
    {
        await using Terrain terrain = await Terrain.StartAsync();
        await terrain.PlaceAsync(CryptStart);
        float left = await terrain.WalkAsync(Mordo, 60_000, output);
        Assert.True(left <= 5f, $"stopped {left:F1} yards from Undertaker Mordo");
    }

    /// <summary>
    /// The crypt's stairs have a spot the navigation mesh covers but where no floor is found: a bot standing there still gets a
    /// route. Before, the start corner's missing floor refused every route, and Graveweaver (stopped there by the old motion)
    /// stood on the stairs for good.
    /// </summary>
    [RealTerrainBotFact]
    public async Task OnTheCryptStairs_WhereNoFloorIsFound_ARouteIsStillPlanned()
    {
        await using Terrain terrain = await Terrain.StartAsync();
        await terrain.PlaceAsync(CryptStairs);
        int points = await terrain.Host.OnWorldAsync(() => PlayerbotNavigation.TryPlan(terrain.Player, Mordo, new PlayerbotOptions(), out PlayerbotRoute? route)
            ? route!.Points.Count : 0);
        Assert.True(points >= 2, "no route from the crypt stairs to Undertaker Mordo");
    }

    /// <summary>
    /// 200 yards across Coldridge Valley to the Rockjaw Raiders: the navigation mesh needs more than 512 search nodes for it. Before,
    /// the search was capped at 512 (vmangos uses 2048) and answered no path, and Mirthblade stood with that goal for good.
    /// </summary>
    [RealTerrainBotFact]
    public async Task AcrossColdridgeValley_ARouteToTheRockjawRaidersIsPlanned()
    {
        await using Terrain terrain = await Terrain.StartAsync();
        await terrain.PlaceAsync(ColdridgeAfterRevive);
        float length = await terrain.Host.OnWorldAsync(() => PlayerbotNavigation.TryPlan(terrain.Player, RockjawRaider, new PlayerbotOptions(),
            out PlayerbotRoute? route) ? route!.Distance : -1f);
        output.WriteLine($"route length {length:F1}");
        Assert.True(length > 0, "no route to the Rockjaw Raiders");
    }

    /// <summary>
    /// In the Goldshire inn the route to William Pestle turns around a pillar; the bot walks it and stands in interaction range.
    /// Before, the motion tested a straight line of sight from the bot to a point two yards along the route, which crossed the
    /// pillar: every start was refused, and Dawnrover stood six yards from the quest giver for hours.
    /// </summary>
    [RealTerrainBotFact]
    public async Task InTheGoldshireInn_TheBotWalksAroundThePillar_ToWilliamPestle()
    {
        await using Terrain terrain = await Terrain.StartAsync();
        await terrain.PlaceAsync(InnBesidePestle);
        float left = await terrain.WalkAsync(WilliamPestle, 20_000, output);
        Assert.True(left <= 4f, $"stopped {left:F1} yards from William Pestle");
    }

    /// <summary>
    /// A ghost 359 yards from its body in the Dun Morogh hills gets a route that closes on the body, and walks to it. Before, the
    /// route went to the point 126 yards along the straight line, which no exact path reaches there: no route, the corpse run was
    /// judged stuck after 10 seconds, and Mirthblade took the spirit healer.
    /// </summary>
    [RealTerrainBotFact]
    public async Task AGhost359YardsFromItsBody_GetsRoutesThatCloseOnIt()
    {
        await using Terrain terrain = await Terrain.StartAsync();
        await terrain.PlaceAsync(MirthbladeGhost);
        float before = Vector2.Distance(new(MirthbladeGhost.X, MirthbladeGhost.Y), new(MirthbladeBody.X, MirthbladeBody.Y));
        float left = await terrain.WalkTowardAsync(MirthbladeBody, 39f, 240_000, output);
        Assert.True(left < 39f, $"the body is still {left:F1} yards away (started {before:F1})");
    }

    /// <summary>
    /// A ghost at its body with two hostile creatures camping within 25 yards of it (the camped-body rule) does not wait for them
    /// or take the spirit healer: the reclaim radius is 39 yards, so it walks to a spot inside it that is clear of both and that the
    /// navigation mesh reaches, and revives there. Before, it stood at the body for a minute and took the spirit healer
    /// (Mirthblade, two Frostmane Troll Whelps).
    /// </summary>
    [RealTerrainBotFact]
    public async Task AGhostAtACampedBody_WalksToAClearSpotInsideTheReclaimRadius_AndRevivesThere()
    {
        var clock = new RevivalClock(1_000);
        await using Terrain terrain = await Terrain.StartAsync(withFactions: true);
        await terrain.Host.OnWorldAsync(() => DeathHooks.Register(terrain.Host.World, new DeathHooks(new DeathOptions(), clock)));
        await terrain.PlaceAsync(ColdridgeAfterRevive);
        Vector3 body = await terrain.KillAndReleaseAsync();
        Vector3 ghost = new(body.X + 6f, body.Y, body.Z + 0.3f);
        await terrain.PlaceAsync(ghost);

        Vector3[] camp = [new(body.X + 14f, body.Y + 6f, body.Z), new(body.X + 9f, body.Y - 12f, body.Z)];
        Creature[] hostiles = await terrain.Host.OnWorldAsync(() => camp.Select((at, index) =>
            DungeonBotHost.AddCreature(terrain.Player.Map!, 994500u + (uint)index, at.X, at.Y, at.Z, 0, terrain.Host.World.NowMs, faction: 14)).ToArray());
        await terrain.WaitAsync(() => hostiles.All(h => terrain.Player.VisibleObjects.Contains(h.Guid)), "the ghost sees the camp");
        clock.Seconds += 31;

        var recovery = new PlayerbotRecovery(terrain.Session, new PlayerbotOptions { Enabled = true });
        bool walkedToSpot = false, usedHealer = false, alive = false;
        for (int think = 0; think < 400 && !alive; think++)
        {
            alive = await terrain.Host.OnWorldAsync(() =>
            {
                PlayerbotMovementControl.Update(terrain.Session, terrain.Player);
                if (terrain.Player.IsAlive) return true;
                terrain.Session.ManagedBudget = new ManagedActionBudget(4);
                recovery.Update(terrain.Player, 500);
                walkedToSpot |= recovery.LastStep == PlayerbotRecoveryStep.WalkToReviveSpot;
                usedHealer |= recovery.UsingSpiritHealer;
                return terrain.Player.IsAlive;
            });
            for (int tick = 0; tick < 10 && !alive; tick++)
            {
                await terrain.Host.World.AdvanceClockAsync(50);
                await terrain.Host.OnWorldAsync(() => PlayerbotMotion.Pump(terrain.Session, terrain.Player, terrain.Host.World.NowMs));
            }
        }

        Assert.True(alive, $"still a ghost; last step {recovery.LastStep}, spot {recovery.ReviveSpot}");
        Assert.True(walkedToSpot, "the ghost never walked to a revive spot");
        Assert.False(usedHealer, "the ghost took the spirit healer");
        await terrain.Host.OnWorldAsync(() =>
        {
            var at = new Vector3(terrain.Player.X, terrain.Player.Y, terrain.Player.Z);
            Assert.True(Vector3.Distance(at, body) < ArcaneCore.Game.Combat.CombatConstants.CorpseReclaimRadius, $"revived {Vector3.Distance(at, body):F1} yards from the body");
            foreach (Creature hostile in hostiles)
                Assert.True(Vector3.Distance(at, new Vector3(hostile.X, hostile.Y, hostile.Z)) > PlayerbotRecovery.HostileClearYards,
                    $"revived {Vector3.Distance(at, new Vector3(hostile.X, hostile.Y, hostile.Z)):F1} yards from a hostile");
        });
    }

    private sealed class RevivalClock(long seconds) : DeathClock
    {
        public long Seconds { get; set; } = seconds;
        public override long UnixSeconds => Seconds;
    }

    /// <summary>A world on the real terrain with one managed bot session on the manual clock.</summary>
    private sealed class Terrain : IAsyncDisposable
    {
        private Terrain(WorldTestHost host, WorldSession session)
        {
            Host = host;
            Session = session;
        }

        public WorldTestHost Host { get; }

        public WorldSession Session { get; }

        public Player Player => Session.Player!;

        private TeleportService Teleports => Host.WorldServices.GetRequiredService<TeleportFeature>().Teleports;

        public static async Task<Terrain> StartAsync(bool withFactions = false)
        {
            string root = Environment.GetEnvironmentVariable(RealTerrainBotFactAttribute.Variable)!;
            WorldTestHost host = WorldTestHost.Start(configure: options => options.Maps.DataDirectory = root,
                configureServices: services =>
                {
                    services.AddSingleton<IWorldFeature, ManualClock>();
                    if (!withFactions) return;
                    // The scenario world's factions (DungeonBotHost): faction 14 monsters are hostile to the human bot.
                    services.AddSingleton(new FactionTemplateCatalog(
                    [
                        new FactionTemplateRecord(1, 1, 0, OwnMask: 3, FriendlyMask: 2, HostileMask: 12),
                        new FactionTemplateRecord(14, 14, 0, OwnMask: 8, FriendlyMask: 0, HostileMask: 1),
                    ]));
                });
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            return new Terrain(host, session);
        }

        /// <summary>Teleport the bot (eastern kingdoms) and acknowledge like a client until it stands there.</summary>
        public async Task PlaceAsync(Vector3 at)
        {
            Assert.True(await Host.OnWorldAsync(() => Teleports.TeleportTo(Player, 0, at.X, at.Y, at.Z, 0)), "teleport refused");
            for (int tick = 0; tick < 200; tick++)
            {
                bool there = await Host.OnWorldAsync(() =>
                {
                    PlayerbotMovementControl.Update(Session, Player);
                    return Player.IsInWorld && Teleports.StageOf(Player) is null
                        && Vector2.Distance(new(Player.X, Player.Y), new(at.X, at.Y)) < 1f;
                });
                if (there) return;
                await Host.World.AdvanceClockAsync(50);
            }

            throw new TimeoutException($"the bot did not arrive at {at}");
        }

        /// <summary>Kill the bot where it stands and release its spirit; the body's position.</summary>
        public async Task<Vector3> KillAndReleaseAsync()
        {
            await Host.OnWorldAsync(() =>
            {
                Player.Health = 0;
                Player.Map!.Combat.KillPlayer(Player);
                if (!Player.Map!.Combat.RepopPlayer(Player)) throw new InvalidOperationException("release refused");
            });
            await WaitAsync(() => (Player.Flags & PlayerFlags.Ghost) != 0 && Teleports.StageOf(Player) is null, "the release");
            return await Host.OnWorldAsync(() => new Vector3(Player.Combat.Corpse!.X, Player.Combat.Corpse.Y, Player.Combat.Corpse.Z));
        }

        /// <summary>Tick the world and acknowledge the server orders until <paramref name="done"/> holds (bounded by game time).</summary>
        public async Task WaitAsync(Func<bool> done, string what)
        {
            for (int tick = 0; tick < 400; tick++)
            {
                bool reached = await Host.OnWorldAsync(() =>
                {
                    PlayerbotMovementControl.Update(Session, Player);
                    return Player.IsInWorld && done();
                });
                if (reached) return;
                await Host.World.AdvanceClockAsync(50);
            }

            throw new TimeoutException($"timed out waiting for: {what}");
        }

        /// <summary>Plan one route to <paramref name="goal"/> and follow it as the brain does; the distance left when it stops.</summary>
        public async Task<float> WalkAsync(Vector3 goal, uint maxMs, ITestOutputHelper log)
        {
            var options = new PlayerbotOptions();
            PlayerbotRoute? route = await Host.OnWorldAsync(() => PlayerbotNavigation.TryPlan(Player, goal, options, out PlayerbotRoute? planned) ? planned : null);
            Assert.NotNull(route);
            log.WriteLine($"route: {string.Join(" ", route!.Points)}");
            return await FollowAsync(route, goal, maxMs);
        }

        /// <summary>Follow routes from <see cref="PlayerbotNavigation.TryPlanToward"/> until within <paramref name="reach"/> of the goal.</summary>
        public async Task<float> WalkTowardAsync(Vector3 goal, float reach, uint maxMs, ITestOutputHelper log)
        {
            var options = new PlayerbotOptions();
            float left = float.PositiveInfinity;
            for (uint spent = 0; spent < maxMs;)
            {
                PlayerbotRoute? route = await Host.OnWorldAsync(() => PlayerbotNavigation.TryPlanToward(Player, goal, options, out PlayerbotRoute? planned) ? planned : null);
                Assert.True(route is not null, $"no route towards {goal} from {await Host.OnWorldAsync(() => new Vector3(Player.X, Player.Y, Player.Z))}");
                log.WriteLine($"leg to {route!.Points[^1]}");
                uint started = Host.World.NowMs;
                left = await FollowAsync(route, goal, maxMs - spent);
                spent += unchecked(Host.World.NowMs - started);
                if (left < reach) return left;
            }

            return left;
        }

        private async Task<float> FollowAsync(PlayerbotRoute route, Vector3 goal, uint maxMs)
        {
            var options = new PlayerbotOptions();
            for (uint elapsed = 0; elapsed <= maxMs; elapsed += 100)
            {
                bool moving = await Host.OnWorldAsync(() =>
                {
                    Session.ManagedBudget = new ManagedActionBudget(4);
                    return PlayerbotNavigation.TryAdvance(Session, route, options, 100, Host.World.NowMs);
                });
                if (!moving) break;
                await Host.World.AdvanceClockAsync(50);
                await Host.OnWorldAsync(() => PlayerbotMotion.Pump(Session, Player, Host.World.NowMs));
                await Host.World.AdvanceClockAsync(50);
                await Host.OnWorldAsync(() => PlayerbotMotion.Pump(Session, Player, Host.World.NowMs));
            }

            return await Host.OnWorldAsync(() => Vector2.Distance(new(Player.X, Player.Y), new(goal.X, goal.Y)));
        }

        public async ValueTask DisposeAsync()
        {
            Session.Kick();
            await Session.ManagedClosed;
            await Host.DisposeAsync();
        }
    }

    private sealed class ManualClock : IWorldFeature
    {
        public void Attach(WorldRuntime world) => world.UseManualClock();
    }
}
