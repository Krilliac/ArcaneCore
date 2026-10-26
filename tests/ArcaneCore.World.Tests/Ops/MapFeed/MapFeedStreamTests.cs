using System.Numerics;
using System.Text.Json;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Bots;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Ops.Control;
using ArcaneCore.World.Tests.Playerbots.Party;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using static ArcaneCore.World.Tests.Ops.Control.WorldControlPipeTests;

namespace ArcaneCore.World.Tests.Ops.MapFeed;

/// <summary>
/// The frame stream (docs/areas/map-feed.md, "Capture"): what a subscription gets and when. A real world on the manual clock, a real
/// named pipe, a client reading NDJSON. The clock moves only when the test says so, so a frame count is exact: the first frame comes
/// at once (the first tick after subscribing), the next ones every interval of game time.
/// </summary>
public sealed class MapFeedStreamTests
{
    private static void AssertNear(Vector3 expected, Vector3 actual, float tolerance = 0.01f)
        => Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"expected {expected}, got {actual}");

    private static Task WaitForSpawnsAsync(FeedWorld world, int creatures, int gameObjects)
        => world.Host.WaitForWorldAsync(
            () => world.Host.World.FindMap(0, 0) is { } map
                && (map.FindUpdater<CreatureMapSystem>()?.Creatures.Count ?? 0) >= creatures
                && (map.FindUpdater<GameObjectMapSystem>()?.GameObjects.Count ?? 0) >= gameObjects,
            "the content spawns");

    /// <summary>World thread: start a walk of the creature with this GUID along <paramref name="path"/>; the spline's id and duration.</summary>
    private static Task<(uint Id, uint Duration)> StartWalkAsync(FeedWorld world, ulong guid, Vector3[] path)
        => world.Host.OnWorldAsync(() =>
        {
            CreatureMapSystem system = world.Host.World.FindMap(0, 0)!.FindUpdater<CreatureMapSystem>()!;
            Creature creature = system.Creatures.Single(c => c.Guid.Value == guid);
            system.MovePath(creature, path, run: false, SplineFacing.None);
            CreatureSpline spline = creature.Spline!;
            return (spline.Id, spline.DurationMs);
        });

    private static async Task UnsubscribeAsync(FeedWorld world, FeedClient client)
    {
        await client.SendAsync("""{"op":"unsubscribe"}""");
        await WorldTestHost.WaitForAsync(() => world.Feed.SubscriptionCount == 0, "the unsubscribe");
    }

    private static async Task<JsonElement> SubscribeAndReadFirstAsync(FeedClient client, string subscribe)
    {
        await client.SendAsync(subscribe);
        return await client.ReadOpAsync("frame");
    }

    [WindowsFact]
    public async Task TheCapture_CarriesRevisionTwo_TheCombatFlagAndTheDisplayId_OfRealUnits()
    {
        await using FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup { Content = true });
        await world.AddPlayerAsync("Feedalice");
        await WaitForSpawnsAsync(world, 1, 1);
        (uint wolfDisplay, uint playerDisplay) = await world.Host.OnWorldAsync(() =>
        {
            Map map = world.Host.World.FindMap(0, 0)!;
            Creature wolf = map.FindUpdater<CreatureMapSystem>()!.Creatures.Single(c => c.Guid.Value == FeedWorld.WolfGuid);
            Player player = map.Players.Single();

            // The real entry into combat, on the player: a creature with nothing to fight evades within the tick, a player does not.
            map.Combat.SetInCombatState(player, 60_000);
            return (wolf.DisplayId, player.DisplayId);
        });
        FeedClient client = await world.ConnectAsync(hello: false);

        JsonElement hello = await client.HelloAsync();
        JsonElement frame = await SubscribeAndReadFirstAsync(client, FeedClient.Subscribe());

        Assert.Equal(2, hello.GetProperty("rev").GetInt32());
        Assert.NotEqual(0u, wolfDisplay);
        Assert.NotEqual(0u, playerDisplay);
        JsonElement player = Assert.Single(frame.OfKind("player"));
        Assert.True(player.GetProperty("cb").GetBoolean());
        Assert.Equal(playerDisplay, player.GetProperty("d").GetUInt32());
        JsonElement wolfLine = frame.Entity(FeedWorld.WolfGuid);
        Assert.False(wolfLine.TryGetProperty("cb", out _), "a creature out of combat carries no flag");
        Assert.Equal(wolfDisplay, wolfLine.GetProperty("d").GetUInt32());
        Assert.False(frame.Entity(FeedWorld.ChestGuid).TryGetProperty("d", out _), "a game object has no display id on the wire");
    }

    [WindowsFact]
    public async Task TheFirstFrame_ArrivesAtOnce_AndDescribesTheMapAroundThePlayer()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup { Content = true });
        await world.AddPlayerAsync("Feedalice");
        await WaitForSpawnsAsync(world, 1, 1);
        FeedClient client = await world.ConnectAsync();

        JsonElement frame = await SubscribeAndReadFirstAsync(client, FeedClient.Subscribe());

        Assert.Equal(1, frame.GetProperty("seq").GetInt64());
        Assert.Equal(0, frame.GetProperty("map").GetInt32());
        Assert.Equal(0, frame.GetProperty("instance").GetInt32());
        Assert.True(frame.GetProperty("clockMs").GetInt64() >= 0);
        Assert.True(frame.GetProperty("grids").GetArrayLength() > 0, "the player's grid is loaded");
        Assert.All(frame.GetProperty("grids").EnumerateArray(), g => Assert.Equal(2, g.GetArrayLength()));

        JsonElement counters = frame.Counters();
        Assert.Equal(1, counters.GetProperty("players").GetInt32());
        Assert.Equal(0, counters.GetProperty("bots").GetInt32());
        Assert.Equal(1, counters.GetProperty("creatures").GetInt32());
        Assert.Equal(1, counters.GetProperty("gameobjects").GetInt32());
        Assert.Equal(3, counters.GetProperty("emitted").GetInt32());
        Assert.Equal(0, counters.GetProperty("truncated").GetInt32());
        Assert.Equal(0, counters.GetProperty("nonFinite").GetInt32());
        Assert.Equal(0, counters.GetProperty("pathsOmitted").GetInt32());
        Assert.Equal(0, counters.GetProperty("dropped").GetInt32());
        Assert.Equal(1, counters.GetProperty("online").GetInt32());
        Assert.Equal(3, frame.Entities().Length);

        JsonElement player = Assert.Single(frame.OfKind("player"));
        Assert.Equal("Feedalice", player.GetProperty("n").GetString(), ignoreCase: true);
        Assert.InRange(player.GetProperty("x").GetSingle(), FeedWorld.StartX - 1f, FeedWorld.StartX + 1f);
        Assert.InRange(player.GetProperty("y").GetSingle(), FeedWorld.StartY - 1f, FeedWorld.StartY + 1f);
        Assert.True(player.TryGetProperty("hp", out _));
        Assert.False(player.TryGetProperty("e", out _), "a player has no entry");
        Assert.False(player.TryGetProperty("mt", out _), "only creatures have a motion");
        (byte race, byte playerClass) = await world.Host.OnWorldAsync(() =>
        {
            Player real = world.Host.World.FindMap(0, 0)!.Players.Single();
            return ((byte)real.Race, (byte)real.Class);
        });
        Assert.NotEqual(0, race);
        Assert.NotEqual(0, playerClass);
        Assert.Equal(race, player.GetProperty("r").GetByte());
        Assert.Equal(playerClass, player.GetProperty("cl").GetByte());

        JsonElement wolf = frame.Entity(FeedWorld.WolfGuid);
        Assert.False(wolf.TryGetProperty("r", out _), "a creature carries no race");
        Assert.Equal("creature", wolf.GetProperty("k").GetString());
        Assert.Equal(FeedWorld.WolfEntry, wolf.GetProperty("e").GetUInt32());
        Assert.Equal("Young Wolf", wolf.GetProperty("n").GetString());
        AssertNear(new Vector3(-8940f, -132f, 83.5f), new Vector3(wolf.GetProperty("x").GetSingle(), wolf.GetProperty("y").GetSingle(), wolf.GetProperty("z").GetSingle()));
        Assert.Equal("idle", wolf.GetProperty("mt").GetString());
        AssertNear(new Vector3(-8940f, -132f, 83.5f), wolf.GetProperty("home").ToVector());
        Assert.True(wolf.TryGetProperty("hp", out _));
        Assert.False(wolf.TryGetProperty("sp", out _), "an unmoving creature has no spline");
        Assert.False(wolf.TryGetProperty("tg", out _), "an unengaged creature has no target");

        JsonElement chest = frame.Entity(FeedWorld.ChestGuid);
        Assert.Equal("gameobject", chest.GetProperty("k").GetString());
        Assert.Equal(FeedWorld.ChestEntry, chest.GetProperty("e").GetUInt32());
        Assert.Equal("Battered Chest", chest.GetProperty("n").GetString());
        Assert.False(chest.TryGetProperty("hp", out _));
        Assert.False(chest.TryGetProperty("mt", out _));
        AssertNear(new Vector3(-8948f, -132.5f, 83.5f), new Vector3(chest.GetProperty("x").GetSingle(), chest.GetProperty("y").GetSingle(), chest.GetProperty("z").GetSingle()));

        Assert.Equal(1, world.Feed.FramesCaptured);
        Assert.Equal(0, world.Feed.CaptureFaults);
        Assert.DoesNotContain(world.Logs.Entries, e => e.Level >= Microsoft.Extensions.Logging.LogLevel.Error);
    }

    [WindowsFact]
    public async Task ACreatureOnASpline_CarriesItsPath_AndTheTimingToPlaceItAnyMoment()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup { Content = true, Options = o => o.MaxHz = 20 });
        await world.AddPlayerAsync();
        await WaitForSpawnsAsync(world, 1, 1);
        Vector3[] path = [new(-8935f, -132f, 83.5f), new(-8935f, -126f, 83.5f), new(-8928f, -126f, 83.5f)];
        (uint splineId, uint duration) = await StartWalkAsync(world, FeedWorld.WolfGuid, path);
        FeedClient client = await world.ConnectAsync();

        JsonElement first = await SubscribeAndReadFirstAsync(client, FeedClient.Subscribe(hz: 20));

        JsonElement wolf = first.Entity(FeedWorld.WolfGuid);
        JsonElement spline = wolf.GetProperty("sp");
        Assert.Equal(splineId, spline.GetProperty("id").GetUInt32());
        AssertNear(new Vector3(-8940f, -132f, 83.5f), spline.GetProperty("start").ToVector());
        Vector3[] sent = spline.GetProperty("pts").ToVectors();
        Assert.Equal(path.Length, sent.Length);
        for (int i = 0; i < path.Length; i++)
        {
            AssertNear(path[i], sent[i]);
        }

        Assert.Equal(duration, spline.GetProperty("dur").GetUInt32());
        Assert.False(spline.GetProperty("run").GetBoolean());
        Assert.False(spline.TryGetProperty("cut", out _), "a short path is not cut");
        long firstClock = first.GetProperty("clockMs").GetInt64();
        long t0 = spline.GetProperty("t0").GetInt64();
        Assert.Equal(firstClock, t0); // started on this clock reading: nothing has elapsed

        // A second of game time at 20 Hz: a frame every 50 ms, the same spline, the clock moving on and t0 staying put.
        List<JsonElement> frames = await world.StepFramesAsync(client, 20);
        Assert.Equal(20, frames.Count);
        long previous = firstClock;
        foreach (JsonElement frame in frames)
        {
            long clock = frame.GetProperty("clockMs").GetInt64();
            Assert.Equal(previous + 50, clock);
            previous = clock;
            JsonElement now = frame.Entity(FeedWorld.WolfGuid).GetProperty("sp");
            Assert.Equal(splineId, now.GetProperty("id").GetUInt32());
            Assert.Equal(t0, now.GetProperty("t0").GetInt64());
        }

        JsonElement last = frames[^1].Entity(FeedWorld.WolfGuid);
        Assert.True(last.GetProperty("x").GetSingle() > -8940f + 1f, "the creature walked along its path");
        Assert.Equal(20, frames[^1].GetProperty("seq").GetInt64() - 1);
    }

    [WindowsFact]
    public async Task WithoutPaths_NoSplineAndNoRouteIsSent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup { Content = true });
        await world.AddPlayerAsync();
        await WaitForSpawnsAsync(world, 1, 1);
        await StartWalkAsync(world, FeedWorld.WolfGuid, [new Vector3(-8935f, -132f, 83.5f)]);
        FeedClient withPaths = await world.ConnectAsync();
        FeedClient without = await world.ConnectAsync();

        JsonElement a = await SubscribeAndReadFirstAsync(withPaths, FeedClient.Subscribe());
        JsonElement b = await SubscribeAndReadFirstAsync(without, FeedClient.Subscribe(paths: false));

        Assert.True(a.Entity(FeedWorld.WolfGuid).TryGetProperty("sp", out _));
        Assert.False(b.Entity(FeedWorld.WolfGuid).TryGetProperty("sp", out _));
        Assert.Equal(b.Entities().Length, a.Entities().Length); // the same entities, only the paths are left out
    }

    [WindowsFact]
    public async Task ABotWithARoute_CarriesItsRoute_ThroughThePlayerbotPacksSeam()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup { Bots = true, Terrain = false });
        await PartyTestHost.InstallFlatGroundAsync(world.Host, FeedWorld.StartZ);
        Guid botId = await world.AddBotAsync("Feedbot");
        ManagedBot bot = world.Host.WorldServices.GetRequiredService<ManagedBotRegistry>().TryGet(botId, out ManagedBot found)
            ? found
            : throw new InvalidOperationException("the bot is not in the registry");
        WorldSession session = bot.Session;
        ulong botGuid = await world.Host.OnWorldAsync(() => session.Player!.Guid.Value);

        Vector3[] points = await world.Host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            Vector3[] route =
            [
                new(player.X, player.Y, player.Z), new(player.X + 10f, player.Y, player.Z),
                new(player.X + 10f, player.Y + 10f, player.Z), new(player.X + 20f, player.Y + 10f, player.Z),
            ];
            var walk = new PlayerbotRoute(route, 30f, navigated: true)
            {
                Chase = true, MeshEndedShort = true, PartialToGoal = true, CompletedQuestReturn = true,
            };
            session.ManagedBudget = new ManagedActionBudget(4);
            Assert.True(PlayerbotMotion.Follow(session, player, walk, PartyTestHost.PlayerbotOptionsOf(world.Host), world.Host.World.NowMs), "the bot starts the route");
            return route;
        });

        FeedClient client = await world.ConnectAsync();
        JsonElement frame = await SubscribeAndReadFirstAsync(client, FeedClient.Subscribe());

        Assert.Equal(1, frame.Counters().GetProperty("bots").GetInt32());
        Assert.Equal(0, frame.Counters().GetProperty("players").GetInt32());
        JsonElement entity = frame.Entity(botGuid);
        Assert.Equal("bot", entity.GetProperty("k").GetString());
        Assert.Equal("Feedbot", entity.GetProperty("n").GetString(), ignoreCase: true);
        JsonElement route = entity.GetProperty("route");
        Vector3[] sent = route.GetProperty("pts").ToVectors();
        Assert.Equal(points.Length, sent.Length);
        for (int i = 0; i < points.Length; i++)
        {
            AssertNear(points[i], sent[i]);
        }

        Assert.Equal(1, route.GetProperty("next").GetInt32());
        Assert.Equal(["meshEndedShort", "partialToGoal", "chase", "navigated", "questReturn"], route.GetProperty("flags").Strings());
        Assert.False(route.TryGetProperty("cut", out _));

        // Asked without paths, the same bot is there without its route; asked for bots only, it is all there is.
        FeedClient plain = await world.ConnectAsync();
        JsonElement noPaths = await SubscribeAndReadFirstAsync(plain, FeedClient.Subscribe(paths: false, kinds: """["bot"]"""));
        JsonElement bare = Assert.Single(noPaths.Entities());
        Assert.Equal(FeedWorld.Hex(botGuid), bare.GetProperty("g").GetString());
        Assert.False(bare.TryGetProperty("route", out _));
    }

    [WindowsFact]
    public async Task ABotWithoutARoute_HasNoRoute()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup { Bots = true, Terrain = false });
        Guid botId = await world.AddBotAsync("Feedidle");
        ulong botGuid = await world.Host.OnWorldAsync(() =>
        {
            world.Host.WorldServices.GetRequiredService<ManagedBotRegistry>().TryGet(botId, out ManagedBot found);
            return found.Session.Player!.Guid.Value;
        });
        FeedClient client = await world.ConnectAsync();

        JsonElement frame = await SubscribeAndReadFirstAsync(client, FeedClient.Subscribe());

        JsonElement entity = frame.Entity(botGuid);
        Assert.Equal("bot", entity.GetProperty("k").GetString());
        Assert.False(entity.TryGetProperty("route", out _));
    }

    private sealed class CutRouteSource : IBotRouteSource
    {
        public const int MaxPathPoints = 512;

        public int CopyRoute(Player bot, Span<Vector3> destination, out BotRouteInfo info)
        {
            for (int i = 0; i < destination.Length; i++)
            {
                destination[i] = new Vector3(bot.X + i, bot.Y, bot.Z);
            }

            info = new BotRouteInfo(Next: MaxPathPoints + 42, Flags: BotRouteFlags.Navigated);
            return MaxPathPoints + 100;
        }
    }

    [WindowsFact]
    public async Task ABotWithACutRoute_ClampsNextToTheShippedPoints()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup { Bots = true, Terrain = false });
        Guid botId = await world.AddBotAsync("Feedcut");
        ManagedBotRegistry registry = world.Host.WorldServices.GetRequiredService<ManagedBotRegistry>();
        FeatureHost features = world.Host.WorldServices.GetRequiredService<FeatureHost>();
        ulong botGuid = await world.Host.OnWorldAsync(() =>
        {
            registry.TryGet(botId, out ManagedBot found);
            return found.Session.Player!.Guid.Value;
        });

        FeatureResult detached = await features.DetachAsync(nameof(ManagedPlayerbotFeature));
        Assert.True(detached.Ok, detached.Detail);

        using (registry.UseRouteSource(new CutRouteSource()))
        {
            FeedClient client = await world.ConnectAsync();
            JsonElement frame = await SubscribeAndReadFirstAsync(client, FeedClient.Subscribe());
            JsonElement route = frame.Entity(botGuid).GetProperty("route");

            Assert.True(route.GetProperty("cut").GetBoolean());
            Assert.Equal(CutRouteSource.MaxPathPoints, route.GetProperty("pts").GetArrayLength());
            Assert.Equal(CutRouteSource.MaxPathPoints, route.GetProperty("next").GetInt32());
        }
    }

    private sealed class ThrowingRouteSource : IBotRouteSource
    {
        public int CopyRoute(Player bot, Span<Vector3> destination, out BotRouteInfo info)
            => throw new InvalidOperationException("broken route source");
    }

    [WindowsFact]
    public async Task AThrowingRouteSource_OmitsTheRoute_KeepsTheFrame_AndLogsARateLimitedWarningWithoutError()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup { Bots = true, Terrain = false, Options = o => o.MaxHz = 60 });
        Guid botId = await world.AddBotAsync("Feedfault");
        ManagedBotRegistry registry = world.Host.WorldServices.GetRequiredService<ManagedBotRegistry>();
        FeatureHost features = world.Host.WorldServices.GetRequiredService<FeatureHost>();
        ulong botGuid = await world.Host.OnWorldAsync(() =>
        {
            registry.TryGet(botId, out ManagedBot found);
            return found.Session.Player!.Guid.Value;
        });

        FeatureResult detached = await features.DetachAsync(nameof(ManagedPlayerbotFeature));
        Assert.True(detached.Ok, detached.Detail);
        Assert.Null(registry.RouteSource);

        using (registry.UseRouteSource(new ThrowingRouteSource()))
        {
            FeedClient client = await world.ConnectAsync();
            JsonElement first = await SubscribeAndReadFirstAsync(client, FeedClient.Subscribe(hz: 60));

            JsonElement entity = first.Entity(botGuid);
            Assert.Equal("bot", entity.GetProperty("k").GetString());
            Assert.False(entity.TryGetProperty("route", out _));
            Assert.Equal(1, first.Counters().GetProperty("pathsOmitted").GetInt32());
            Assert.Equal(1, world.Feed.RouteFaults);

            CapturedCategoryLog warning = Assert.Single(
                world.FeedLog,
                e => e.Level == LogLevel.Warning && e.Message.Contains("route source", StringComparison.Ordinal));
            Assert.Contains("1 so far", warning.Message, StringComparison.Ordinal);

            // Faults 2..99 stay silent; the 100th logs the second warning.
            List<JsonElement> next = await world.StepFramesAsync(client, 99);
            Assert.Equal(99, next.Count);
            Assert.Equal(100, world.Feed.RouteFaults);
            CapturedCategoryLog[] routeWarnings =
            [
                .. world.FeedLog.Where(e => e.Level == LogLevel.Warning && e.Message.Contains("route source", StringComparison.Ordinal)),
            ];
            Assert.Equal(2, routeWarnings.Length);
            Assert.Contains("100 so far", routeWarnings[1].Message, StringComparison.Ordinal);
            Assert.DoesNotContain(world.Logs.Entries, e => e.Level >= LogLevel.Error);
        }
    }

    [WindowsFact]
    public async Task TheKindsFilter_LeavesOutWhatWasNotAskedFor_ButTheCountersStillCountIt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup { Content = true });
        await world.AddPlayerAsync();
        await WaitForSpawnsAsync(world, 1, 1);
        FeedClient creatures = await world.ConnectAsync();
        FeedClient others = await world.ConnectAsync();

        JsonElement onlyCreatures = await SubscribeAndReadFirstAsync(creatures, FeedClient.Subscribe(kinds: """["creature"]"""));
        JsonElement noCreatures = await SubscribeAndReadFirstAsync(others, FeedClient.Subscribe(kinds: """["player","gameobject"]"""));

        Assert.Equal([FeedWorld.Hex(FeedWorld.WolfGuid)], onlyCreatures.Entities().Select(e => e.GetProperty("g").GetString()!).ToArray());
        Assert.Equal(1, onlyCreatures.Counters().GetProperty("players").GetInt32()); // counted, not listed
        Assert.Equal(1, onlyCreatures.Counters().GetProperty("emitted").GetInt32());
        Assert.Empty(noCreatures.OfKind("creature"));
        Assert.Single(noCreatures.OfKind("player"));
        Assert.Single(noCreatures.OfKind("gameobject"));
        Assert.Equal(1, noCreatures.Counters().GetProperty("creatures").GetInt32());
    }

    [WindowsFact]
    public async Task APlayerlessFrame_AndAnEmptyKindList_AreValid()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup { Content = true });
        await world.AddPlayerAsync();
        await WaitForSpawnsAsync(world, 1, 1);
        FeedClient client = await world.ConnectAsync();

        JsonElement frame = await SubscribeAndReadFirstAsync(client, FeedClient.Subscribe(kinds: "[]"));

        Assert.Empty(frame.Entities());
        Assert.Equal(0, frame.Counters().GetProperty("emitted").GetInt32());
        Assert.Equal(1, frame.Counters().GetProperty("players").GetInt32());
    }

    [WindowsFact]
    public async Task OverTheEntityCap_PlayersComeFirst_ThenMovingCreatures_TheRestIsCountedAsTruncated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const int Extra = 20;
        await using FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup
        {
            Content = true, ExtraCreatures = Extra, Options = o => o.MaxEntitiesPerFrame = 16,
        });
        await world.AddPlayerAsync();
        await WaitForSpawnsAsync(world, Extra + 1, 1);
        // The last spawn walks: of all the creatures it is the one a plain first-come cut would drop.
        ulong walker = FeedWorld.ExtraWolfGuid(Extra - 1);
        await StartWalkAsync(world, walker, [new Vector3(-8930f, -125f, 83.5f)]);
        FeedClient client = await world.ConnectAsync();

        JsonElement frame = await SubscribeAndReadFirstAsync(client, FeedClient.Subscribe());

        JsonElement counters = frame.Counters();
        Assert.Equal(16, frame.Entities().Length);
        Assert.Equal(16, counters.GetProperty("emitted").GetInt32());
        Assert.Equal(1, counters.GetProperty("players").GetInt32());
        Assert.Equal(Extra + 1, counters.GetProperty("creatures").GetInt32()); // the totals are of the map, not of the frame
        Assert.Equal(1, counters.GetProperty("gameobjects").GetInt32());
        Assert.Equal((Extra + 1) - 15 + 1, counters.GetProperty("truncated").GetInt32()); // 6 creatures and the chest did not fit
        Assert.Single(frame.OfKind("player"));
        Assert.Equal(15, frame.OfKind("creature").Length);
        Assert.Empty(frame.OfKind("gameobject"));
        Assert.True(frame.Entity(walker).TryGetProperty("sp", out _), "the walking creature made the cut");
    }

    [WindowsFact]
    public async Task ASubscriptionToAMapThatIsNotRunning_IsToldOnce_AndGetsFramesWhenItStarts()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup { Terrain = false });
        Assert.Null(await world.Host.OnWorldAsync(() => world.Host.World.FindMap(0, 0)));
        FeedClient client = await world.ConnectAsync();

        await client.SendAsync(FeedClient.Subscribe());
        JsonElement missing = await client.ReadAsync();
        Assert.Equal("error", missing.Op());
        Assert.Equal(MapFeedProtocolCodes.MapNotLoaded, missing.Code());

        await world.StepAsync(30); // a second and a half: five intervals due, one notice only
        Assert.Equal(0, world.Feed.FramesCaptured);
        Assert.Equal("hello", (await client.HelloAsync()).Op()); // nothing else was waiting in the pipe

        await world.AddPlayerAsync();
        List<JsonElement> frames = await world.StepFramesAsync(client, 10);
        Assert.NotEmpty(frames);
        Assert.Equal(1, frames[0].Counters().GetProperty("players").GetInt32());
    }

    [WindowsFact]
    public async Task TheFrameRate_IsAsAskedUpToTheCap_AndTheCountsAreExact()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Four clients one after another (each unsubscribes before the next subscribes, so every capture is the current one's).
        await using FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup { Terrain = false, Options = o => o.MaxConnections = 4 });
        await world.AddPlayerAsync();

        // 5 Hz (the default cap): 200 ms apart. The first frame at once, then one in every fourth 50 ms step.
        FeedClient five = await world.ConnectAsync();
        JsonElement firstFive = await SubscribeAndReadFirstAsync(five, FeedClient.Subscribe(hz: 5));
        List<JsonElement> fiveFrames = await world.StepFramesAsync(five, 20);
        Assert.Equal(5, fiveFrames.Count);
        Assert.Equal(200, fiveFrames[0].GetProperty("clockMs").GetInt64() - firstFive.GetProperty("clockMs").GetInt64());
        for (int i = 1; i < fiveFrames.Count; i++)
        {
            Assert.Equal(200, fiveFrames[i].GetProperty("clockMs").GetInt64() - fiveFrames[i - 1].GetProperty("clockMs").GetInt64());
        }

        await UnsubscribeAsync(world, five);

        // When the first tick after subscribing carries 50 ms of game time, the gap to the second frame is still a full 200 ms (4 ticks).
        await world.Host.OnWorldAsync(() =>
        {
            _ = five.SendAsync(FeedClient.Subscribe(hz: 5));
            SpinWait.SpinUntil(() => world.Feed.SubscriptionCount == 1, TimeSpan.FromSeconds(5));
            Assert.Equal(1, world.Feed.SubscriptionCount);
            for (int tick = 0; tick < 5; tick++)
            {
                world.Host.World.RunTick(50);
            }

            return true;
        });
        JsonElement tickFirst = await five.ReadOpAsync("frame");
        JsonElement tickSecond = await five.ReadOpAsync("frame");
        Assert.Equal(200, tickSecond.GetProperty("clockMs").GetInt64() - tickFirst.GetProperty("clockMs").GetInt64());
        await UnsubscribeAsync(world, five);

        // Asking for 60 Hz under a 5 Hz cap gets 5 Hz.
        FeedClient greedy = await world.ConnectAsync();
        await SubscribeAndReadFirstAsync(greedy, FeedClient.Subscribe(hz: 60));
        List<JsonElement> capped = await world.StepFramesAsync(greedy, 20);
        Assert.Equal(5, capped.Count);

        await UnsubscribeAsync(world, greedy);

        // Asking for nothing gets the default, 4 Hz: one in every fifth step.
        FeedClient plain = await world.ConnectAsync();
        await SubscribeAndReadFirstAsync(plain, FeedClient.Subscribe());
        List<JsonElement> defaults = await world.StepFramesAsync(plain, 20);
        Assert.Equal(4, defaults.Count);

        await UnsubscribeAsync(world, plain);

        // 1 Hz: one frame in the second.
        FeedClient slow = await world.ConnectAsync();
        await SubscribeAndReadFirstAsync(slow, FeedClient.Subscribe(hz: 1));
        List<JsonElement> slowFrames = await world.StepFramesAsync(slow, 20);
        Assert.Single(slowFrames);
    }

    [WindowsFact]
    public async Task ARaisedCap_LetsAFasterSubscriptionHaveEveryStep()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup { Terrain = false, Options = o => o.MaxHz = 60 });
        await world.AddPlayerAsync();
        FeedClient client = await world.ConnectAsync();
        await SubscribeAndReadFirstAsync(client, FeedClient.Subscribe(hz: 60));

        List<JsonElement> frames = await world.StepFramesAsync(client, 20);

        // A 50 ms tick is longer than the 17 ms interval: one frame per tick, never a burst to catch up.
        Assert.Equal(20, frames.Count);
        Assert.Equal(0, frames[^1].Counters().GetProperty("dropped").GetInt32());
    }

    [WindowsFact]
    public async Task TwoSubscriptionsOfOneView_ShareOneCapturePerTick()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup { Content = true });
        await world.AddPlayerAsync();
        await WaitForSpawnsAsync(world, 1, 1);
        FeedClient a = await world.ConnectAsync();
        FeedClient b = await world.ConnectAsync();
        await SubscribeAndReadFirstAsync(a, FeedClient.Subscribe(hz: 5));
        await SubscribeAndReadFirstAsync(b, FeedClient.Subscribe(hz: 5));
        long captured = world.Feed.FramesCaptured;

        await world.StepAsync(4); // both are due on the fourth step
        JsonElement fromA = await a.ReadOpAsync("frame");
        JsonElement fromB = await b.ReadOpAsync("frame");

        Assert.Equal(captured + 1, world.Feed.FramesCaptured);
        Assert.Equal(fromA.GetProperty("clockMs").GetInt64(), fromB.GetProperty("clockMs").GetInt64());
        Assert.Equal(fromA.Entities().Length, fromB.Entities().Length);
        // Each connection numbers its own frames.
        Assert.Equal(2, fromA.GetProperty("seq").GetInt64());
        Assert.Equal(2, fromB.GetProperty("seq").GetInt64());
    }

    [WindowsFact]
    public async Task Unsubscribe_StopsTheCapture_AndTheWorldIsAsItWasBefore()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup { Terrain = false });
        await world.AddPlayerAsync();
        int baseline = world.UpdatedHandlers();
        FeedClient client = await world.ConnectAsync();
        Assert.Equal(baseline, world.UpdatedHandlers()); // connected, not subscribed: no handler on the world

        await SubscribeAndReadFirstAsync(client, FeedClient.Subscribe(hz: 5));
        Assert.True(world.Feed.IsAttached);
        Assert.Equal(1, world.Feed.SubscriptionCount);
        Assert.Equal(baseline + 1, world.UpdatedHandlers());
        Assert.Equal(5, (await world.StepFramesAsync(client, 20)).Count);

        await client.SendAsync("""{"op":"unsubscribe"}""");
        await WorldTestHost.WaitForAsync(() => !world.Feed.IsAttached, "the capture handler detaches");
        Assert.Equal(0, world.Feed.SubscriptionCount);
        Assert.Equal(baseline, world.UpdatedHandlers());

        long calls = world.Feed.UpdatedCalls;
        long captured = world.Feed.FramesCaptured;
        await world.StepAsync(40);
        Assert.Equal(calls, world.Feed.UpdatedCalls); // the handler is not even called
        Assert.Equal(captured, world.Feed.FramesCaptured);
        Assert.Equal("hello", (await client.HelloAsync()).Op()); // and no frame is waiting in the pipe

        // Subscribing again starts the stream again.
        await SubscribeAndReadFirstAsync(client, FeedClient.Subscribe(hz: 5));
        Assert.Equal(baseline + 1, world.UpdatedHandlers());
    }

    [WindowsFact]
    public async Task ADisconnect_IsAnUnsubscribe()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup { Terrain = false });
        await world.AddPlayerAsync();
        int baseline = world.UpdatedHandlers();
        FeedClient client = await world.ConnectAsync();
        await SubscribeAndReadFirstAsync(client, FeedClient.Subscribe());
        Assert.Equal(baseline + 1, world.UpdatedHandlers());

        await client.DisposeAsync();

        await WorldTestHost.WaitForAsync(() => !world.Feed.IsAttached && world.Feed.ActiveConnections == 0, "the connection ends and detaches");
        Assert.Equal(0, world.Feed.SubscriptionCount);
        Assert.Equal(baseline, world.UpdatedHandlers());
        Assert.Equal(world.Feed.Pool.Created, world.Feed.Pool.Free); // every frame went back to the pool
    }

    [WindowsFact]
    public async Task NothingIsCaptured_WhileNobodyIsSubscribed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup { Content = true });
        await world.AddPlayerAsync();
        int baseline = world.UpdatedHandlers();
        FeedClient client = await world.ConnectAsync();

        await world.StepAsync(40);
        Assert.Equal("hello", (await client.HelloAsync()).Op()); // a connection that only asks questions

        Assert.False(world.Feed.IsAttached);
        Assert.Equal(baseline, world.UpdatedHandlers());
        Assert.Equal(0, world.Feed.UpdatedCalls);
        Assert.Equal(0, world.Feed.FramesCaptured);
        Assert.Equal(0, world.Feed.FramesDropped);
        Assert.Equal(0, world.Feed.Pool.Created); // not even a frame was allocated
    }

    [WindowsFact]
    public async Task ASecondSubscribe_ReplacesTheFirst()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup { Content = true });
        await world.AddPlayerAsync();
        await WaitForSpawnsAsync(world, 1, 1);
        FeedClient client = await world.ConnectAsync();
        await SubscribeAndReadFirstAsync(client, FeedClient.Subscribe(hz: 5));

        JsonElement replaced = await SubscribeAndReadFirstAsync(client, FeedClient.Subscribe(hz: 5, kinds: """["player"]"""));

        Assert.Equal(1, world.Feed.SubscriptionCount);
        Assert.Single(replaced.Entities());
        Assert.Equal("player", replaced.Entities()[0].GetProperty("k").GetString());
    }
}

/// <summary>The protocol's error codes as the tests read them off the wire.</summary>
internal static class MapFeedProtocolCodes
{
    public const string MapNotLoaded = "map-not-loaded";
}
