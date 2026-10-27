using System.Buffers.Binary;
using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.DebugDraw;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Bots;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Features;
using ArcaneCore.World.Gm.DebugDraw;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Creatures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.DebugDraw;

/// <summary>
/// <c>.debug vis follow</c> (docs/areas/debug-draw.md, "Live follow"): a followed creature's spline and a followed bot's route are drawn for
/// the GM alone, redrawn (old markers destroyed, new ones created) only when the path changed, and the follow ends with the unit, the
/// GM's logout or map change. Nothing is read while nobody follows.
/// </summary>
public sealed partial class DebugDrawCommandTests
{
    private static readonly Dictionary<string, string?> FastFollow = new() { [$"{DebugDrawOptions.SectionName}:FollowIntervalMs"] = "100" };

    private static readonly TimeSpan FollowWait = TimeSpan.FromSeconds(10);

    // A long walk (well over 10 s at walk speed), so the spline is still running while a test watches it.
    private static readonly Vector3[] FirstWalk = [new(-8930f, -150f, 83.5f), new(-8900f, -150f, 83.5f), new(-8870f, -120f, 83.5f)];

    private static readonly Vector3[] SecondWalk = [new(-8940f, -100f, 83.5f), new(-8900f, -80f, 83.5f)];

    private static Dictionary<string, string?> With(Dictionary<string, string?> settings, params (string Key, string Value)[] more)
    {
        var all = new Dictionary<string, string?>(settings);
        foreach ((string key, string value) in more)
        {
            all[$"{DebugDrawOptions.SectionName}:{key}"] = value;
        }

        return all;
    }

    private static DebugPathFollow Follows(WorldTestHost host) => Feature(host).Follow;

    /// <summary>Detach the playerbot pack (its route seam is taken while it is attached) and put <paramref name="routes"/> in its place.</summary>
    private static async Task<IDisposable> UseRoutesAsync(WorldTestHost host, IBotRouteSource routes)
    {
        FeatureResult detached = await host.WorldServices.GetRequiredService<FeatureHost>().DetachAsync(nameof(ManagedPlayerbotFeature));
        Assert.True(detached.Ok, detached.Detail);
        return host.WorldServices.GetRequiredService<ManagedBotRegistry>().UseRouteSource(routes);
    }

    private static Task WalkAsync(WorldTestHost host, ObjectGuid creature, Vector3[] path)
        => host.OnWorldAsync(() =>
        {
            CreatureMapSystem system = host.WorldServices.GetRequiredService<CreatureWorldFeature>().GetOrCreateSystem(0);
            system.MovePath(system.FindCreature(creature)!, path, run: false, SplineFacing.None);
        });

    private static ObjectGuid WolfGuid => ObjectGuid.WithEntry(HighGuid.Unit, WolfEntry, WolfSpawn);

    /// <summary>Read the GM's packets until <paramref name="done"/> holds for what arrived (or the wait ends), then the quiet tail.</summary>
    private static async Task<Run> UntilAsync(WorldTestClient client, Func<Run, bool> done)
    {
        var run = new Run([], []);
        DateTime deadline = DateTime.UtcNow + FollowWait;
        while (!done(run) && DateTime.UtcNow < deadline)
        {
            try
            {
                run.Packets.Add(await client.ReadAsync(deadline - DateTime.UtcNow));
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                break;
            }
        }

        run.Packets.AddRange(await client.CollectAsync(Quiet));
        run.Replies.AddRange(run.Packets
            .Where(p => p.Opcode == WorldOpcode.SmsgMessagechat && p.Payload[0] == (byte)ChatType.System)
            .Select(p => ChatMessage.Parse(p.Payload).Text));
        return run;
    }

    private static bool AnyMarkerTraffic(Run run)
        => run.Created.Count > 0 || run.Destroyed.Any(DebugMarkerStyles.IsMarkerGuid);

    /// <summary>A bot route seam that answers the routes the test sets (world thread reads, the test writes through OnWorldAsync).</summary>
    private sealed class TestRoutes : IBotRouteSource
    {
        public Dictionary<ObjectGuid, (Vector3[] Points, BotRouteFlags Flags)> Routes { get; } = [];

        public int Reads { get; private set; }

        public int CopyRoute(Player bot, Span<Vector3> destination, out BotRouteInfo info)
        {
            Reads++;
            info = default;
            if (!Routes.TryGetValue(bot.Guid, out (Vector3[] Points, BotRouteFlags Flags) route))
            {
                return 0;
            }

            route.Points.AsSpan(0, Math.Min(route.Points.Length, destination.Length)).CopyTo(destination);
            info = new BotRouteInfo(1, route.Flags);
            return route.Points.Length;
        }
    }

    [Fact]
    public void Levels_FollowNeedsAGameMaster()
    {
        CommandTable table = ChatCommands.CreateTable();
        foreach (string path in new[] { "debug vis follow", "debug vis follow nearby", "debug vis follow list" })
        {
            Assert.Null(table.Resolve(path, AccountSecurity.Moderator));
            Assert.NotNull(table.Resolve(path, AccountSecurity.GameMaster));
        }
    }

    [Fact]
    public void Options_FollowValuesAreClamped()
    {
        DebugDrawOptions options = DebugDrawOptions.Bind(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{DebugDrawOptions.SectionName}:FollowIntervalMs"] = "1",
            [$"{DebugDrawOptions.SectionName}:MaxFollowedUnits"] = "5000",
            [$"{DebugDrawOptions.SectionName}:FollowNearbyMaxRadius"] = "99999",
        }).Build());
        Assert.Equal(100, options.FollowIntervalMs);
        Assert.Equal(50, options.MaxFollowedUnits);
        Assert.Equal(533f, options.FollowNearbyMaxRadius);

        var defaults = new DebugDrawOptions();
        Assert.Equal((500, 10, 100f), (defaults.FollowIntervalMs, defaults.MaxFollowedUnits, defaults.FollowNearbyMaxRadius));
    }

    [Fact]
    public async Task FollowACreature_DrawsItsSpline_RedrawsWhenItChanges_AndIsSilentWhileItDoesNot()
    {
        await using WorldTestHost host = Start(FastFollow);
        await host.OnWorldAsync(() => WorldCollision.Of(host.World).Install(pathfinder: new DetourPathfinder(WolfAt)));
        await using WorldTestClient gm = await EnterAsync(host, "Drawer", AccountSecurity.GameMaster);
        await using WorldTestClient bystander = await EnterAsync(host, "Watcher", AccountSecurity.Player);
        await gm.CollectAsync(Quiet);
        await SelectWolfAsync(host, "Drawer");

        Run started = await SendAsync(gm, ".debug vis follow");
        Assert.Contains(started.Replies, r => r.StartsWith("DebugDraw: following Test Wolf (1 of 10)", StringComparison.Ordinal));

        await WalkAsync(host, WolfGuid, FirstWalk);
        Run first = await UntilAsync(gm, r => r.Created.Any(m => m.Kind == DebugMarkerKind.PathGoal));

        // The goal (glyphed crystal and its tall glow), every corner of the spline (its start included), and the dots between them.
        Assert.Contains(first.Created, m => m.Kind == DebugMarkerKind.PathGoal && m.DisplayId == 6431 && Vector3.Distance(m.Position, FirstWalk[^1]) < 0.01f);
        Assert.Contains(first.Created, m => m.Kind == DebugMarkerKind.PathGoal && m.DisplayId == 266);
        foreach (Vector3 corner in FirstWalk[..^1])
        {
            Assert.Contains(first.Created, m => m.Kind == DebugMarkerKind.PathCorner && Vector3.Distance(m.Position, corner) < 0.01f);
        }

        Assert.Contains(first.Created, m => m.Kind == DebugMarkerKind.PathCorner && Vector2.Distance(new Vector2(m.Position.X, m.Position.Y), new Vector2(WolfAt.X, WolfAt.Y)) < 0.5f);
        Assert.Contains(first.Created, m => m.Kind == DebugMarkerKind.PathFill);
        Assert.DoesNotContain(first.Created, m => m.Kind == DebugMarkerKind.PathCornerBad);
        IReadOnlyList<ObjectGuid> held = await MarkersOfAsync(host, "Drawer");
        Assert.Equal(first.Created.Select(m => m.Guid.Value).Order(), held.Select(g => g.Value).Order());

        // The path does not change: nothing is sent, however many checks pass.
        Assert.False(AnyMarkerTraffic(await CollectForAsync(gm, TimeSpan.FromMilliseconds(800))));

        // A new path: the old markers are destroyed, the new ones drawn.
        await WalkAsync(host, WolfGuid, SecondWalk);
        Run second = await UntilAsync(gm, r => r.Created.Any(m => m.Kind == DebugMarkerKind.PathGoal));
        Assert.Equal(held.Select(g => g.Value).Order(), second.Destroyed.Where(DebugMarkerStyles.IsMarkerGuid).Select(g => g.Value).Order());
        Assert.Contains(second.Created, m => m.Kind == DebugMarkerKind.PathGoal && Vector3.Distance(m.Position, SecondWalk[^1]) < 0.01f);
        Assert.Equal(second.Created.Select(m => m.Guid.Value).Order(), (await MarkersOfAsync(host, "Drawer")).Select(g => g.Value).Order());

        // Right-click names the unit, the move type and the corner.
        ParsedMarker goal = second.Created.First(m => m.Kind == DebugMarkerKind.PathGoal && m.DisplayId == 6431);
        var use = new PacketWriter(8);
        use.WriteUInt64(goal.Guid.Value);
        await gm.SendAsync(WorldOpcode.CmsgGameobjUse, use.ToArray());
        Assert.StartsWith("[follow Test Wolf] Test Wolf (creature idle move): goal at", (await gm.ReadSystemChatAsync()).Text, StringComparison.Ordinal);

        Assert.Contains((await SendAsync(gm, ".debug vis follow list")).Replies, r => r.StartsWith("DebugDraw: Test Wolf (creature): 3 path point(s)", StringComparison.Ordinal));

        // The bystander never received any of it.
        await AssertNeverSawAsync(bystander, [.. first.Created.Select(m => m.Guid), .. second.Created.Select(m => m.Guid)]);

        // Following again stops, and removes the markers.
        Run stop = await SendAsync(gm, ".debug vis follow");
        Assert.Contains(DebugDrawCommands.Unfollowed("Test Wolf"), stop.Replies);
        Assert.Equal(second.Created.Select(m => m.Guid.Value).Order(), stop.Destroyed.Select(g => g.Value).Order());
        Assert.Equal(0, await host.OnWorldAsync(() => Follows(host).FollowingGmCount));
    }

    private static async Task<Run> CollectForAsync(WorldTestClient client, TimeSpan window)
    {
        var run = new Run([], []);
        DateTime end = DateTime.UtcNow + window;
        while (DateTime.UtcNow < end)
        {
            run.Packets.AddRange(await client.CollectAsync(TimeSpan.FromMilliseconds(100)));
        }

        return run;
    }

    [Fact]
    public async Task FollowACreature_WithoutNavmesh_MarksTheStraightLine()
    {
        await using WorldTestHost host = Start(FastFollow);
        await using WorldTestClient gm = await EnterAsync(host, "Drawer", AccountSecurity.GameMaster);
        await SelectWolfAsync(host, "Drawer");
        await SendAsync(gm, ".debug vis follow");

        await WalkAsync(host, WolfGuid, FirstWalk);
        Run run = await UntilAsync(gm, r => r.Created.Any(m => m.Kind == DebugMarkerKind.PathGoal));

        Assert.Contains(run.Created, m => m.Kind == DebugMarkerKind.PathCornerBad);
        Assert.DoesNotContain(run.Created, m => m.Kind == DebugMarkerKind.PathCorner);
    }

    [Fact]
    public async Task FollowACreature_EndsWhenItDespawns()
    {
        await using WorldTestHost host = Start(FastFollow);
        await using WorldTestClient gm = await EnterAsync(host, "Drawer", AccountSecurity.GameMaster);
        await SelectWolfAsync(host, "Drawer");
        await SendAsync(gm, ".debug vis follow");
        await WalkAsync(host, WolfGuid, FirstWalk);
        Run drawn = await UntilAsync(gm, r => r.Created.Any(m => m.Kind == DebugMarkerKind.PathGoal));

        await host.OnWorldAsync(() =>
        {
            CreatureMapSystem system = host.WorldServices.GetRequiredService<CreatureWorldFeature>().GetOrCreateSystem(0);
            system.Despawn(system.FindCreature(WolfGuid)!);
        });
        string note = DebugPathFollow.StoppedNote("Test Wolf", "it is no longer on your map");
        Run ended = await UntilAsync(gm, r => r.Packets.Any(p => p.Opcode == WorldOpcode.SmsgMessagechat && ChatMessage.Parse(p.Payload).Text == note));

        Assert.Contains(note, ended.Replies);
        Assert.Equal(drawn.Created.Select(m => m.Guid.Value).Order(), ended.Destroyed.Where(DebugMarkerStyles.IsMarkerGuid).Select(g => g.Value).Order());
        Assert.Equal(0, await host.OnWorldAsync(() => Follows(host).FollowingGmCount));
        Assert.Empty(await MarkersOfAsync(host, "Drawer"));
    }

    [Fact]
    public async Task FollowABot_DrawsItsRoute_RedrawsOnAReplan_AndEndsWhenItLogsOut()
    {
        var routes = new TestRoutes();
        await using WorldTestHost host = Start(FastFollow);
        using IDisposable seam = await UseRoutesAsync(host, routes);
        await using WorldTestClient gm = await EnterAsync(host, "Drawer", AccountSecurity.GameMaster);
        WorldTestClient bot = await EnterAsync(host, "Walker", AccountSecurity.GameMaster); // a GM logs out at once
        try
        {
            await gm.CollectAsync(Quiet);
            Player walker = await host.PlayerAsync("Walker");
            Vector3[] plan = [new(walker.X, walker.Y, walker.Z), new(-8930f, -140f, 83.5f), new(-8910f, -120f, 83.5f)];
            await host.OnWorldAsync(() =>
            {
                routes.Routes[walker.Guid] = (plan, BotRouteFlags.Navigated);
                host.World.FindOnlinePlayer("Drawer")!.Selection = walker.Guid;
            });

            // A loopback player is not a managed bot: the GM is told, and the route the seam answers is drawn all the same.
            Run started = await SendAsync(gm, ".debug vis follow");
            Assert.Contains(DebugDrawCommands.FollowNotBot, started.Replies);
            Assert.Contains(started.Created, m => m.Kind == DebugMarkerKind.PathGoal && Vector3.Distance(m.Position, plan[^1]) < 0.01f);
            Assert.Contains(started.Created, m => m.Kind == DebugMarkerKind.PathCorner && Vector3.Distance(m.Position, plan[1]) < 0.01f);
            Assert.DoesNotContain(started.Created, m => m.Kind == DebugMarkerKind.PathCornerBad);

            // A replan that ends short of its goal: redrawn, with the incomplete corners.
            Vector3[] replan = [plan[0], new(-8935f, -110f, 83.5f), new(-8920f, -95f, 83.5f)];
            await host.OnWorldAsync(() => routes.Routes[walker.Guid] = (replan, BotRouteFlags.Navigated | BotRouteFlags.PartialToGoal));
            Run redrawn = await UntilAsync(gm, r => r.Created.Any(m => m.Kind == DebugMarkerKind.PathGoal));
            Assert.Equal(started.Created.Select(m => m.Guid.Value).Order(), redrawn.Destroyed.Where(DebugMarkerStyles.IsMarkerGuid).Select(g => g.Value).Order());
            Assert.Contains(redrawn.Created, m => m.Kind == DebugMarkerKind.PathGoal && Vector3.Distance(m.Position, replan[^1]) < 0.01f);
            Assert.Contains(redrawn.Created, m => m.Kind == DebugMarkerKind.PathCornerBad);

            ParsedMarker goal = redrawn.Created.First(m => m.Kind == DebugMarkerKind.PathGoal && m.DisplayId == 6431);
            var use = new PacketWriter(8);
            use.WriteUInt64(goal.Guid.Value);
            await gm.SendAsync(WorldOpcode.CmsgGameobjUse, use.ToArray());
            Assert.StartsWith("[follow Walker] Walker (bot route, navmesh, partial to goal): goal at", (await gm.ReadSystemChatAsync()).Text, StringComparison.Ordinal);
            await AssertNeverSawAsync(bot, [.. started.Created.Select(m => m.Guid), .. redrawn.Created.Select(m => m.Guid)]);

            // The bot logs out: the follow ends and its markers go.
            await bot.SendAsync(WorldOpcode.CmsgLogoutRequest, []);
            await bot.ReadUntilAsync(WorldOpcode.SmsgLogoutComplete);
            string note = DebugPathFollow.StoppedNote("Walker", "it is no longer on your map");
            Run ended = await UntilAsync(gm, r => r.Replies.Contains(note) || r.Packets.Any(p => p.Opcode == WorldOpcode.SmsgMessagechat && ChatMessage.Parse(p.Payload).Text == note));
            Assert.Contains(note, ended.Replies);
            Assert.Equal(redrawn.Created.Select(m => m.Guid.Value).Order(), ended.Destroyed.Where(DebugMarkerStyles.IsMarkerGuid).Select(g => g.Value).Order());
            Assert.Equal(0, await host.OnWorldAsync(() => Follows(host).FollowingGmCount));
        }
        finally
        {
            await bot.DisposeAsync();
        }
    }

    [Fact]
    public async Task TheFollow_EndsWithTheGmsMapChange()
    {
        await using WorldTestHost host = Start(FastFollow);
        await using WorldTestClient gm = await EnterAsync(host, "Drawer", AccountSecurity.GameMaster);
        await SelectWolfAsync(host, "Drawer");
        await SendAsync(gm, ".debug vis follow");
        await WalkAsync(host, WolfGuid, FirstWalk);
        await UntilAsync(gm, r => r.Created.Any(m => m.Kind == DebugMarkerKind.PathGoal));
        Assert.Equal(1, await host.OnWorldAsync(() => Follows(host).FollowingGmCount));

        // A far teleport: the client drops every object of the old map, the follow is forgotten.
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".tele cross");
        await gm.ReadUntilAsync(WorldOpcode.SmsgNewWorld);
        await gm.SendAsync(WorldOpcode.MsgMoveWorldportAck, []);
        await gm.CollectAsync(Quiet);
        await host.WaitForWorldAsync(() => Follows(host).FollowingGmCount == 0, "the follow to end with the map change");
        Assert.Contains(DebugDrawCommands.FollowNothing, (await SendAsync(gm, ".debug vis follow list")).Replies);
    }

    [Fact]
    public async Task TheFollow_IsForgottenAtTheGmsLogout()
    {
        await using WorldTestHost host = Start(FastFollow);
        await using WorldTestClient gm = await EnterAsync(host, "Leaver", AccountSecurity.GameMaster);
        await SelectWolfAsync(host, "Leaver");
        await SendAsync(gm, ".debug vis follow");
        await WalkAsync(host, WolfGuid, FirstWalk);
        Run drawn = await UntilAsync(gm, r => r.Created.Any(m => m.Kind == DebugMarkerKind.PathGoal));
        Assert.Equal(1, await host.OnWorldAsync(() => Follows(host).FollowingGmCount));

        await gm.SendAsync(WorldOpcode.CmsgLogoutRequest, []);
        List<ObjectGuid> destroyed = [];
        while (true)
        {
            (WorldOpcode opcode, byte[] payload) = await gm.ReadAsync();
            if (opcode == WorldOpcode.SmsgLogoutComplete)
            {
                break;
            }

            if (opcode == WorldOpcode.SmsgDestroyObject)
            {
                destroyed.Add(new ObjectGuid(BinaryPrimitives.ReadUInt64LittleEndian(payload)));
            }
        }

        Assert.Equal(drawn.Created.Select(m => m.Guid.Value).Order(), destroyed.Where(DebugMarkerStyles.IsMarkerGuid).Select(g => g.Value).Order());
        Assert.Equal(0, await host.OnWorldAsync(() => Follows(host).FollowingGmCount));
    }

    [Fact]
    public async Task FollowNearby_TakesTheNearestMovers_UpToTheLimit_AndSharesTheMarkerCap()
    {
        const uint firstSpawn = 88300;
        var template = new CreatureTemplate
        {
            Entry = WolfEntry, Name = "Test Wolf", MinLevel = 2, MaxLevel = 2, DisplayIds = [903], Faction = 35, CreatureType = 1, Family = 1,
            MinLevelHealth = 55, MaxLevelHealth = 55,
        };
        // Four wolves 6, 12, 18 and 24 yards east of the human start, and one 60 yards away.
        float[] offsets = [6f, 12f, 18f, 24f, 60f];
        CreatureTestStore.Current.Value = new CreatureTestContext(new CreatureContent(
            [template],
            [.. offsets.Select((d, i) => new CreatureSpawn { Guid = firstSpawn + (uint)i, Entry = WolfEntry, MapId = 0, X = -8949.95f + d, Y = -132.49f, Z = 83.5f })],
            [], [], []));
        WorldTestHost host;
        try
        {
            host = WorldTestHost.Start(configureServices: services => services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(With(FastFollow, ("MaxFollowedUnits", "2"), ("MaxMarkersPerGm", "20"))).Build()));
        }
        finally
        {
            CreatureTestStore.Current.Value = null;
        }

        await using (host)
        {
            await using WorldTestClient gm = await EnterAsync(host, "Drawer", AccountSecurity.GameMaster);
            ObjectGuid[] wolves = [.. offsets.Select((_, i) => ObjectGuid.WithEntry(HighGuid.Unit, WolfEntry, firstSpawn + (uint)i))];
            await host.WaitForWorldAsync(() => host.WorldServices.GetRequiredService<CreatureWorldFeature>().GetOrCreateSystem(0).FindCreature(wolves[^1]) is not null, "the wolves");

            // The wolf at 12 yards stands; the others walk a long way north.
            foreach (int i in new[] { 0, 2, 3, 4 })
            {
                float x = -8949.95f + offsets[i];
                await WalkAsync(host, wolves[i], [new(x, -100f, 83.5f), new(x + 5f, -60f, 83.5f), new(x, -20f, 83.5f)]);
            }

            Run run = await SendAsync(gm, ".debug vis follow nearby 30");

            Assert.Contains(run.Replies, r => r.Contains("3 unit(s) moving within 30 yd; now following 2 more (2 of 2): Test Wolf, Test Wolf", StringComparison.Ordinal));
            Assert.Contains(run.Replies, r => r.Contains("1 farther unit(s) left out (follow limit 2)", StringComparison.Ordinal));
            IReadOnlyList<ObjectGuid> followed = await host.OnWorldAsync(() =>
                Follows(host).Entries(host.World.FindOnlinePlayer("Drawer")!).Select(e => e.Unit).ToList());
            Assert.Equal([wolves[0], wolves[2]], followed);

            // The two drawings share the 20-marker cap: no more than 10 each, 20 in all, drawn once (nothing evicted and redrawn).
            Assert.InRange(run.Created.Count, 2, 20);
            Assert.DoesNotContain(run.Destroyed, DebugMarkerStyles.IsMarkerGuid);
            Assert.True(await host.OnWorldAsync(() => Feature(host).MarkerCount(host.World.FindOnlinePlayer("Drawer")!)) <= 20);
            Assert.Equal(2, run.Created.Count(m => m.Kind == DebugMarkerKind.PathGoal && m.DisplayId == 6431));

            // Full: nothing more is taken.
            Assert.Contains((await SendAsync(gm, ".debug vis follow nearby")).Replies, r => r.Contains("now following 0 more (2 of 2).", StringComparison.Ordinal));
            Run off = await SendAsync(gm, ".debug vis follow off");
            Assert.Contains(DebugDrawCommands.FollowStopped(2), off.Replies);
            Assert.Empty(await MarkersOfAsync(host, "Drawer"));
        }
    }

    [Fact]
    public async Task NobodyFollowing_ReadsNoPath()
    {
        var routes = new TestRoutes();
        await using WorldTestHost host = Start(FastFollow);
        using IDisposable seam = await UseRoutesAsync(host, routes);
        await using WorldTestClient gm = await EnterAsync(host, "Drawer", AccountSecurity.GameMaster);
        await WalkAsync(host, WolfGuid, FirstWalk);
        await Task.Delay(500);
        Assert.Equal((0L, 0L, 0), await host.OnWorldAsync(() => (Follows(host).PathReads, Follows(host).Redraws, routes.Reads)));

        // A follow reads; once it is off, the reads stop.
        await SelectWolfAsync(host, "Drawer");
        await SendAsync(gm, ".debug vis follow");
        await host.WaitForWorldAsync(() => Follows(host).PathReads > 1, "the follow to read the path");
        await SendAsync(gm, ".debug vis follow off");
        long reads = await host.OnWorldAsync(() => Follows(host).PathReads);
        await Task.Delay(500);
        Assert.Equal(reads, await host.OnWorldAsync(() => Follows(host).PathReads));
        Assert.Equal(0, await host.OnWorldAsync(() => Follows(host).FollowingGmCount));
    }
}
