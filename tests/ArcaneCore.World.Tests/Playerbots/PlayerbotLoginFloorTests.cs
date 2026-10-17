using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Net;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;
using Code = ArcaneCore.World.Playerbots.PlayerbotMotionAnomalies.Code;
using Outcome = ArcaneCore.World.Playerbots.PlayerbotLoginFloor.Outcome;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// A managed bot saved over a void (no floor near its height) is put on a floor when it logs in (<see cref="PlayerbotLoginFloor"/>), and
/// a bot that stands in a void is reported once per place, not once per cooldown (<see cref="PlayerbotMotionAnomalies.MotionWatch.DueAt"/>).
/// </summary>
public sealed class PlayerbotLoginFloorTests(ITestOutputHelper output)
{
    // ---- the pure decision ------------------------------------------------------------------------------------------------------

    private static readonly Vector3 Void = new(-4724f, -895.7f, 501.7f);

    /// <summary>A mesh with one floor disc of 3 yd radius at <paramref name="center"/> at height <paramref name="z"/>.</summary>
    private static PlayerbotLoginFloor.MeshFloorQuery Disc(Vector3 center, float z, List<Vector3>? asked = null)
        => (float x, float y, float _, float _, float _, out float height) =>
        {
            asked?.Add(new Vector3(x, y, 0));
            height = z;
            return Vector2.Distance(new(x, y), new(center.X, center.Y)) <= 3f;
        };

    private static bool NoMesh(float x, float y, float z, float below, float above, out float height)
    {
        height = 0f;
        return false;
    }

    [Fact]
    public void Resolve_OnAFloor_KeepsTheBot_AndAsksNothingElse()
    {
        var asked = new List<Vector3>();
        PlayerbotLoginFloor.Result result = PlayerbotLoginFloor.Resolve(Void, _ => true, Disc(Void, 500f, asked), home: null);

        Assert.Equal(Outcome.Kept, result.Outcome);
        Assert.Equal(Void, result.Position);
        Assert.Empty(asked);
    }

    [Fact]
    public void Resolve_OverAVoid_MovesToTheNearestMeshFloor()
    {
        var floor = new Vector3(Void.X + 14f, Void.Y, 502.5f);
        Vector3 far = new(Void.X, Void.Y - 24f, 501f);
        PlayerbotLoginFloor.MeshFloorQuery both = (float x, float y, float z, float below, float above, out float height) =>
            Disc(floor, floor.Z)(x, y, z, below, above, out height) || Disc(far, far.Z)(x, y, z, below, above, out height);

        PlayerbotLoginFloor.Result result = PlayerbotLoginFloor.Resolve(Void, p => p.Z is > 502f and < 503f, both, home: new Vector3(1, 2, 3));

        Assert.Equal(Outcome.MovedToMesh, result.Outcome);
        Assert.Equal(502.5f, result.Position.Z);
        Assert.True(Vector2.Distance(new(result.Position.X, result.Position.Y), new(floor.X, floor.Y)) <= 3f, $"took {result.Position}");
    }

    [Fact]
    public void Resolve_SkipsAMeshFloorThatFailsTheGroundCheck()
    {
        // The only polygon in reach is itself judged "under the map" (e.g. a ledge polygon that is a void by the bot-motion rule).
        PlayerbotLoginFloor.Result result = PlayerbotLoginFloor.Resolve(Void, _ => false, Disc(new Vector3(Void.X + 6f, Void.Y, 0), 501f), home: null);

        Assert.Equal(Outcome.NoFloorFound, result.Outcome);
        Assert.Equal(Void, result.Position);
    }

    [Fact]
    public void Resolve_WithNoMeshFloorInReach_FallsBackToTheBindPoint_OrStays()
    {
        var home = new Vector3(-4600f, -900f, 503f);

        PlayerbotLoginFloor.Result toHome = PlayerbotLoginFloor.Resolve(Void, _ => false, NoMesh, home);
        Assert.Equal(Outcome.MovedToHome, toHome.Outcome);
        Assert.Equal(home, toHome.Position);

        // A floor beyond the search radius is not "nearest within a sane distance".
        var beyond = new Vector3(Void.X + PlayerbotLoginFloor.SearchYards + 10f, Void.Y, 502f);
        Assert.Equal(Outcome.MovedToHome, PlayerbotLoginFloor.Resolve(Void, p => p.X > Void.X + 5f, Disc(beyond, 502f), home).Outcome);

        PlayerbotLoginFloor.Result stays = PlayerbotLoginFloor.Resolve(Void, _ => false, NoMesh, home: null);
        Assert.Equal(Outcome.NoFloorFound, stays.Outcome);
        Assert.Equal(Void, stays.Position);
    }

    // ---- one report per place ---------------------------------------------------------------------------------------------------

    [Fact]
    public void DueAt_ReportsAStationaryBotOncePerPlace_AndRearmsWhenItMovesOrAfterTheLongInterval()
    {
        var watch = new PlayerbotMotionAnomalies.MotionWatch();
        Vector3 here = Void;

        Assert.True(watch.DueAt(Code.UnderMap, 1000, here));
        // Every cooldown tick afterwards, within a couple of yards of the same spot: silent.
        for (uint now = 1000 + PlayerbotMotionAnomalies.EpisodeQuietMs; now < 1000 + PlayerbotMotionAnomalies.SamePlaceQuietMs; now += PlayerbotMotionAnomalies.EpisodeQuietMs)
            Assert.False(watch.DueAt(Code.UnderMap, now, here + new Vector3(1.5f, -1f, 0.5f)), $"reported again at {now}");

        // Moved away: a new episode, still behind the ordinary cooldown.
        uint later = 1000 + PlayerbotMotionAnomalies.EpisodeQuietMs;
        Vector3 away = here + new Vector3(PlayerbotMotionAnomalies.SamePlaceYards + 3f, 0, 0);
        Assert.True(watch.DueAt(Code.UnderMap, later, away));
        Assert.False(watch.DueAt(Code.UnderMap, later + 1, away + new Vector3(30f, 0, 0))); // the 30 s cooldown still holds

        // Another code is its own episode.
        Assert.True(watch.DueAt(Code.Airwalk, later, away));

        // Same place, long after: re-armed.
        Assert.True(watch.DueAt(Code.UnderMap, later + PlayerbotMotionAnomalies.SamePlaceQuietMs, away));
    }

    [Fact]
    public async Task ReportGround_AStationaryVoidBot_IsReportedOnce_NotOncePerCooldown()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            using AnomalyCapture cap = H1AnomalyKit.CaptureFor(session.Player!);
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                var at = new Vector3(player.X, player.Y, player.Z);
                var watch = new PlayerbotMotionAnomalies.MotionWatch();
                for (int tick = 0; tick < 20; tick++)
                {
                    uint now = 1000 + ((uint)tick * PlayerbotMotionAnomalies.EpisodeQuietMs);
                    PlayerbotMotionAnomalies.ReportGround(player, player.Map!, watch, PlayerbotMotionAnomalies.GroundVerdict.UnderMap, at, at.Z + 60, -200000, now);
                    PlayerbotMotionAnomalies.ReportGround(player, player.Map!, watch, PlayerbotMotionAnomalies.GroundVerdict.Airwalk, at, 0, at.Z - 10, now);
                }

                return true;
            });

            // 20 cooldown ticks (10 minutes less one) at one spot: the first report of each code, nothing more. Before: 20 each.
            Assert.Single(cap.Notices, n => n.Key == PlayerbotMotionAnomalies.Key(Code.UnderMap));
            Assert.Single(cap.Notices, n => n.Key == PlayerbotMotionAnomalies.Key(Code.Airwalk));
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    // ---- the login, on the real terrain -----------------------------------------------------------------------------------------

    [RealTerrainBotFact]
    public async Task ABotSavedInTheIronforgeVoid_LogsInOnAFloor()
    {
        (Vector3 saved, Vector3 placed, PlayerbotMotionAnomalies.GroundVerdict before, PlayerbotMotionAnomalies.GroundVerdict after) = await LoginAsync(Void);

        output.WriteLine($"saved {saved} ({before}) -> logged in at {placed} ({after})");
        Assert.NotEqual(PlayerbotMotionAnomalies.GroundVerdict.Ok, before); // the saved point is a real void, so this is not vacuous
        Assert.Equal(PlayerbotMotionAnomalies.GroundVerdict.Ok, after);
        Assert.InRange(Vector2.Distance(new(saved.X, saved.Y), new(placed.X, placed.Y)), 0.1f, PlayerbotLoginFloor.SearchYards);
        Assert.InRange(MathF.Abs(placed.Z - saved.Z), 0f, PlayerbotLoginFloor.StoreyBelowYards);
    }

    [RealTerrainBotFact]
    public async Task ABotSavedOnTheIronforgeFloor_LogsInWhereItWasSaved()
    {
        var floorSpot = new Vector3(-4601.65f, -896.47f, 503.01f); // a live report position that is a mesh floor (WmoUnderMapRealTerrainTests)
        (Vector3 saved, Vector3 placed, PlayerbotMotionAnomalies.GroundVerdict before, _) = await LoginAsync(floorSpot);

        output.WriteLine($"saved {saved} ({before}) -> logged in at {placed}");
        Assert.Equal(PlayerbotMotionAnomalies.GroundVerdict.Ok, before);
        Assert.Equal(saved, placed);
    }

    private async Task<(Vector3 Saved, Vector3 Placed, PlayerbotMotionAnomalies.GroundVerdict Before, PlayerbotMotionAnomalies.GroundVerdict After)> LoginAsync(Vector3 saved)
    {
        string root = Environment.GetEnvironmentVariable(RealTerrainBotFactAttribute.Variable)!;
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var owners = new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore();
        await using WorldTestHost host = WorldTestHost.Start(configure: o => o.Maps.DataDirectory = root, configureServices: services =>
        {
            services.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>();
            var options = new PlayerbotOptions
            {
                Enabled = true, MaxBots = 4, ThinkIntervalMs = 100, MaxActionsPerTick = 2, MaxPathPoints = 32, MaxRouteYards = 100,
                AllowedMaps = [0, 1],
            };
            services.AddSingleton<IAccountStore>(accounts);
            services.AddSingleton<IAccountAdmin>(accounts);
            services.AddSingleton<ICharacterStore>(characters);
            services.AddSingleton<ICharacterLifeStore>(characters);
            services.AddSingleton<IManagedPlayerbotStore>(owners);
            services.AddSingleton<IOptions<PlayerbotOptions>>(Options.Create(options));
            services.AddSingleton<IManagedPlayerbotProvisionStore>(new ManagedPlayerbotLifecycleTests.MemoryProvisionStore(accounts));
        });
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        Guid id = Assert.IsType<Guid>((await feature.CreateAsync("Voidone", 1, 1)).BotId);
        CharacterRecord character = (await characters.GetByIdAsync((await owners.FindAsync(id))!.CharacterId))!;
        (character.MapId, character.ZoneId, character.X, character.Y, character.Z) = (0u, 1537u, saved.X, saved.Y, saved.Z);

        Assert.True((await feature.StartScriptedAsync(id.ToString(), new ArcaneCore.World.Tests.Playerbots.Party.IdleController())).Success);
        try
        {
            return await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Voidone")!;
                Map map = player.Map!;
                PlayerbotMotionAnomalies.GroundVerdict Judge(Vector3 at)
                {
                    float floor = PlayerbotMotionAnomalies.FloorUnder(map.Collision, at);
                    return PlayerbotMotionAnomalies.JudgeGround(at, floor, map.Collision.LineOfSight.Enabled, map.Collision, out _);
                }

                var placed = new Vector3(player.X, player.Y, player.Z);
                // The point the character was saved at, judged by the same rule on the map the login loaded.
                return (saved, placed, Judge(saved), Judge(placed));
            });
        }
        finally
        {
            await feature.ShutdownBeforeWorldStopAsync();
        }
    }
}
