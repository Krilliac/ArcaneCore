using System.Numerics;
using System.Text.Json;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Ops.MapFeed;
using Xunit;
using static ArcaneCore.World.Tests.Ops.Control.WorldControlPipeTests;

namespace ArcaneCore.World.Tests.Ops.MapFeed;

/// <summary>
/// The live-insight queries over a real pipe (docs/areas/atlas-insight.md): <c>inspect</c> reads one unit on the world thread, and
/// <c>anomalies</c> returns the ring's notices with the current position of what each names. A real world (content: a Young Wolf and a
/// chest), a real player, and the process-wide anomaly ledger that every test world shares.
/// </summary>
public sealed class MapFeedInspectTests
{
    private static readonly Vector3 Start = new(FeedWorld.StartX, FeedWorld.StartY, FeedWorld.StartZ);

    private static FeedWorldSetup Content() => new() { Content = true, Options = o => o.AnomaliesUnowned = false };

    private static string InspectLine(long id, ulong guid, uint map = 0, uint instance = 0)
        => $$"""{"op":"query","id":{{id}},"kind":"inspect","map":{{map}},"instance":{{instance}},"guid":"{{FeedWorld.Hex(guid)}}"}""";

    private static string AnomaliesLine(long id, long since = 0, int max = 256)
        => $$"""{"op":"query","id":{{id}},"kind":"anomalies","since":{{since}},"max":{{max}}}""";

    private static async Task<JsonElement> AskAsync(FeedClient client, string line)
    {
        await client.SendAsync(line);
        return await client.ReadAsync();
    }

    private static void AssertOk(JsonElement result, long id, string kind)
    {
        Assert.Equal("result", result.Op());
        Assert.Equal(id, result.GetProperty("id").GetInt64());
        Assert.True(result.GetProperty("ok").GetBoolean(), result.GetRawText());
        Assert.Equal(kind, result.GetProperty("kind").GetString());
    }

    private static void AssertRefused(JsonElement result, long id, string code)
    {
        Assert.Equal("result", result.Op());
        Assert.Equal(id, result.GetProperty("id").GetInt64());
        Assert.False(result.GetProperty("ok").GetBoolean(), result.GetRawText());
        Assert.Equal(code, result.Code());
    }

    [WindowsFact]
    public async Task Inspect_AYoungWolf_AnswersWhatTheServerKnowsOfIt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(Content());
        await world.AddPlayerAsync();
        FeedClient client = await world.ConnectAsync();

        JsonElement r = await AskAsync(client, InspectLine(31, FeedWorld.WolfGuid));

        AssertOk(r, 31, "inspect");
        Assert.Equal(FeedWorld.Hex(FeedWorld.WolfGuid), r.GetProperty("guid").GetString());
        Assert.Equal("creature", r.GetProperty("entity").GetString());
        Assert.Equal(FeedWorld.WolfEntry, r.GetProperty("entry").GetUInt32());
        Assert.Equal("Young Wolf", r.GetProperty("name").GetString());
        Assert.Equal(0u, r.GetProperty("map").GetUInt32());
        Assert.Equal(2, r.GetProperty("level").GetInt32());
        Assert.Equal(55u, r.GetProperty("maxHp").GetUInt32());
        Assert.Equal(55u, r.GetProperty("hp").GetUInt32());
        Assert.Equal(35u, r.GetProperty("faction").GetUInt32());
        Assert.False(r.GetProperty("combat").GetBoolean());
        Assert.Equal(JsonValueKind.Array, r.GetProperty("threat").ValueKind);
        Assert.Empty(r.GetProperty("threat").EnumerateArray());
        Vector3 at = r.GetProperty("p").ToVector();
        Assert.True(Vector3.Distance(at, new Vector3(-8940f, -132f, 83.5f)) < 5f, $"the wolf stands at its spawn, got {at}");
        Assert.True(r.GetProperty("stand").GetString()!.Length > 0);
        Assert.True(r.TryGetProperty("motion", out JsonElement motion) && motion.ValueKind == JsonValueKind.Object, "a creature has a motion master");
        Assert.True(motion.GetProperty("stack").ValueKind == JsonValueKind.Array);
        Assert.Equal(JsonValueKind.Null, r.GetProperty("bot").ValueKind);
    }

    [WindowsFact]
    public async Task Inspect_APlayer_AndAChest_AreTheirOwnKindsOfEntity()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(Content());
        await world.AddPlayerAsync("Insightpc");
        ulong player = (await world.Host.PlayerAsync("Insightpc")).Guid.Value;
        FeedClient client = await world.ConnectAsync();

        JsonElement p = await AskAsync(client, InspectLine(1, player));
        JsonElement c = await AskAsync(client, InspectLine(2, FeedWorld.ChestGuid));

        AssertOk(p, 1, "inspect");
        Assert.Equal("player", p.GetProperty("entity").GetString());
        Assert.Equal("Insightpc", p.GetProperty("name").GetString());
        Assert.True(p.GetProperty("level").GetInt32() >= 1);
        Assert.Equal(JsonValueKind.Null, p.GetProperty("bot").ValueKind);

        AssertOk(c, 2, "inspect");
        Assert.Equal("gameobject", c.GetProperty("entity").GetString());
        Assert.Equal("Battered Chest", c.GetProperty("name").GetString());
    }

    [WindowsFact]
    public async Task Inspect_ThreatList_IsOrderedHighestFirst_NamesTheTargets_AndCapsAtTen()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup { Content = true, ExtraCreatures = 12, Options = o => o.AnomaliesUnowned = false });
        await world.AddPlayerAsync("Tankone");
        ulong tank = (await world.Host.PlayerAsync("Tankone")).Guid.Value;
        FeedClient client = await world.ConnectAsync();

        // The wolf is threatened by the player (700) and by twelve other wolves (10, 20, ... 120): thirteen entries, the top ten are asked for.
        await world.Host.OnWorldAsync(() =>
        {
            Map map = world.Host.World.FindMap(0, 0)!;
            var wolf = (Unit)map.FindObject(new ObjectGuid(FeedWorld.WolfGuid))!;
            wolf.Combat.Threat.AddThreat((Unit)map.FindObject(new ObjectGuid(tank))!, 700f);
            for (int i = 0; i < 12; i++)
            {
                wolf.Combat.Threat.AddThreat((Unit)map.FindObject(new ObjectGuid(FeedWorld.ExtraWolfGuid(i)))!, (i + 1) * 10f);
            }
        });

        JsonElement r = await AskAsync(client, InspectLine(5, FeedWorld.WolfGuid));

        AssertOk(r, 5, "inspect");
        JsonElement[] threat = [.. r.GetProperty("threat").EnumerateArray()];
        Assert.Equal(10, threat.Length);
        Assert.Equal(FeedWorld.Hex(tank), threat[0].GetProperty("g").GetString());
        Assert.Equal("Tankone", threat[0].GetProperty("name").GetString());
        Assert.Equal(700f, threat[0].GetProperty("t").GetSingle());
        Assert.Equal(120f, threat[1].GetProperty("t").GetSingle());
        Assert.Equal(40f, threat[9].GetProperty("t").GetSingle());
        Assert.Equal(threat.Select(t => t.GetProperty("t").GetSingle()).OrderDescending(), threat.Select(t => t.GetProperty("t").GetSingle()));
    }

    [WindowsFact]
    public async Task Inspect_AnUnknownGuid_IsNotFound_AMapThatIsNotRunning_IsRefused_AndNothingIsLoaded()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(Content());
        await world.AddPlayerAsync();
        FeedClient client = await world.ConnectAsync();
        ulong nobody = 0x00F1300000FFFFFFul;

        AssertRefused(await AskAsync(client, InspectLine(1, nobody)), 1, MapFeedProtocol.NotFound);
        AssertRefused(await AskAsync(client, InspectLine(2, FeedWorld.WolfGuid, map: 1)), 2, MapFeedProtocol.MapNotLoaded);
        AssertRefused(await AskAsync(client, InspectLine(3, FeedWorld.WolfGuid, map: 0, instance: 9)), 3, MapFeedProtocol.MapNotLoaded);
        AssertRefused(await AskAsync(client, InspectLine(4, 0)), 4, MapFeedProtocol.BadArgs);

        // The wrong-map and wrong-instance questions loaded no map: a query is a read, never a reason to build anything.
        bool created = await world.Host.OnWorldAsync(() => world.Host.World.FindMap(1, 0) is not null || world.Host.World.FindMap(0, 9) is not null);
        Assert.False(created);
        JsonElement still = await AskAsync(client, InspectLine(5, FeedWorld.WolfGuid));
        AssertOk(still, 5, "inspect");
    }

    [WindowsFact]
    public async Task Inspect_IsAnsweredByTheWorldThread_NotBeforeIt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(Content());
        await world.AddPlayerAsync();
        FeedClient client = await world.ConnectAsync();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        // Hold the world thread; an inspect sent meanwhile must wait for it (it reads world state, so it may only run there).
        Task hold = world.Host.OnWorldAsync(() =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        await client.SendAsync(InspectLine(77, FeedWorld.WolfGuid));
        Task<JsonElement> reply = client.ReadAsync();
        Task winner = await Task.WhenAny(reply, Task.Delay(500));
        Assert.NotSame(reply, winner);

        release.Set();
        await hold;
        AssertOk(await reply, 77, "inspect");
    }

    [WindowsFact]
    public async Task EveryQueryKind_AnswersWithItsOwnKind_NeverUndermap()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(Content());
        await world.AddPlayerAsync();
        FeedClient client = await world.ConnectAsync();
        long id = 0;

        foreach (QueryKind kind in Enum.GetValues<QueryKind>())
        {
            id++;
            string name = kind.ToString().ToLowerInvariant();
            string line = kind switch
            {
                QueryKind.Inspect => InspectLine(id, FeedWorld.WolfGuid),
                QueryKind.Anomalies => AnomaliesLine(id),
                QueryKind.Path => FeedClient.QueryLine(id, name, 0, Start, Start + new Vector3(5, 0, 0)),
                QueryKind.LineOfSight => FeedClient.QueryLine(id, "los", 0, Start, Start + new Vector3(5, 0, 0)),
                _ => FeedClient.QueryLine(id, name, 0, Start),
            };

            JsonElement result = await AskAsync(client, line);
            string wire = kind == QueryKind.LineOfSight ? "los" : name;
            Assert.True(result.GetProperty("ok").GetBoolean(), $"{kind}: {result.GetRawText()}");
            Assert.True(result.GetProperty("kind").GetString() == wire, $"{kind} answered kind '{result.GetProperty("kind").GetString()}'");
        }

        Assert.True(id >= 6, "the enum has the old kinds and the two new ones");
    }

    [WindowsFact]
    public async Task Anomalies_OfThisWorldOnly_WithTheCurrentPositionOfWhatTheySay()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(Content());
        await using FeedWorld other = await FeedWorld.StartAsync(Content());
        await world.AddPlayerAsync("Anomalyone");
        var player = await world.Host.PlayerAsync("Anomalyone");
        FeedClient client = await world.ConnectAsync();
        object mine = world.Host.World.Identity;
        string tag = "insight" + Guid.NewGuid().ToString("N")[..8];

        AnomalyLedger.Shared.Report(tag, "stuck", $"creature {FeedWorld.WolfEntry} spawn {FeedWorld.WolfSpawn}", "wolf is stuck", AnomalySeverity.Warning, mine);
        AnomalyLedger.Shared.Report(tag, "lost", ArcaneCore.World.Playerbots.PlayerbotAnomalies.SubjectOf(player), "bot lost", AnomalySeverity.Error, mine);
        AnomalyLedger.Shared.Report(tag, "gone", "creature 299 spawn 999999", "no such spawn", AnomalySeverity.Info, mine);
        AnomalyLedger.Shared.Report(tag, "real", ArcaneCore.Game.Creatures.CreatureAnomalies.Subject(FeedWorld.WolfEntry, FeedWorld.WolfSpawn), "evade loop", AnomalySeverity.Warning, mine);
        AnomalyLedger.Shared.Report(tag, "other", $"creature {FeedWorld.WolfEntry} spawn {FeedWorld.WolfSpawn}", "other world's notice", AnomalySeverity.Warning, other.Host.World.Identity);
        AnomalyLedger.Shared.Report(tag, "unowned", "something", "no owner", AnomalySeverity.Warning, origin: null);

        JsonElement r = await AskAsync(client, AnomaliesLine(9, since: 0, max: 4096));

        AssertOk(r, 9, "anomalies");
        JsonElement[] items = [.. r.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("subsystem").GetString() == tag)];
        Assert.Equal(["stuck", "lost", "gone", "real"], items.Select(i => i.GetProperty("code").GetString()));
        Assert.Equal(["warning", "error", "info", "warning"], items.Select(i => i.GetProperty("severity").GetString()));

        // The subject the creature reporters really write ("creature entry E spawn S") is positioned like the hand-written form.
        JsonElement real = items[3];
        Assert.Equal("current", real.GetProperty("pos").GetString());
        Assert.True(Vector3.Distance(real.GetProperty("p").ToVector(), new Vector3(-8940f, -132f, 83.5f)) < 5f);

        JsonElement wolf = items[0];
        Assert.Equal("current", wolf.GetProperty("pos").GetString());
        Assert.Equal(0u, wolf.GetProperty("map").GetUInt32());
        Assert.True(Vector3.Distance(wolf.GetProperty("p").ToVector(), new Vector3(-8940f, -132f, 83.5f)) < 5f);

        JsonElement bot = items[1];
        Assert.Equal("current", bot.GetProperty("pos").GetString());
        Assert.True(Vector3.Distance(bot.GetProperty("p").ToVector(), Start) < 5f);

        JsonElement gone = items[2];
        Assert.False(gone.TryGetProperty("pos", out _), "a notice that names no live object has no position");
        Assert.False(gone.TryGetProperty("p", out _));

        long latest = r.GetProperty("latest").GetInt64();
        JsonElement none = await AskAsync(client, AnomaliesLine(10, since: latest));
        AssertOk(none, 10, "anomalies");
        Assert.Empty(none.GetProperty("items").EnumerateArray());
        Assert.Equal(latest, none.GetProperty("latest").GetInt64());
    }

    [WindowsFact]
    public async Task Anomalies_OfAnElevator_AreOnlyPositioned_BecauseItsGuidIsATransportGuid()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup { Content = true, Elevator = true, Options = o => o.AnomaliesUnowned = false });
        await world.AddPlayerAsync("Liftone");
        FeedClient client = await world.ConnectAsync();
        string tag = "insight" + Guid.NewGuid().ToString("N")[..8];
        Assert.Equal(HighGuid.Transport, new ObjectGuid(FeedWorld.ElevatorGuid).High);
        await world.Host.WaitForWorldAsync(
            () => world.Host.World.FindMap(0, 0)?.FindObject(new ObjectGuid(FeedWorld.ElevatorGuid)) is not null, "the elevator spawns");

        // The subject the elevator reporters write (GameObjectMapSystem.Elevators.cs): "gameobject entry E spawn S".
        AnomalyLedger.Shared.Report(tag, "lift", $"gameobject entry {FeedWorld.ElevatorEntry} spawn {FeedWorld.ElevatorSpawn}", "elevator input", AnomalySeverity.Error, world.Host.World.Identity);

        JsonElement r = await AskAsync(client, AnomaliesLine(5, since: 0, max: 4096));

        AssertOk(r, 5, "anomalies");
        JsonElement lift = Assert.Single(r.GetProperty("items").EnumerateArray(), i => i.GetProperty("subsystem").GetString() == tag);
        Assert.Equal("current", lift.GetProperty("pos").GetString());
        Assert.True(Vector3.Distance(lift.GetProperty("p").ToVector(), new Vector3(-8930f, -140f, 90f)) < 5f);
    }

    [WindowsFact]
    public async Task Anomalies_AreNotKeptWithoutAnOwner_WhenTheOptionIsOff_AndAre_WhenOn()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld strict = await FeedWorld.StartAsync(new FeedWorldSetup { Options = o => o.AnomaliesUnowned = false });
        await using FeedWorld lenient = await FeedWorld.StartAsync(new FeedWorldSetup { Options = o => o.AnomaliesUnowned = true });
        FeedClient strictClient = await strict.ConnectAsync();
        FeedClient lenientClient = await lenient.ConnectAsync();
        string tag = "unowned" + Guid.NewGuid().ToString("N")[..8];

        AnomalyLedger.Shared.Report(tag, "code", "nobody owns this", "d", AnomalySeverity.Warning, origin: null);

        JsonElement a = await AskAsync(strictClient, AnomaliesLine(1, max: 4096));
        JsonElement b = await AskAsync(lenientClient, AnomaliesLine(1, max: 4096));
        Assert.DoesNotContain(a.GetProperty("items").EnumerateArray(), i => i.GetProperty("subsystem").GetString() == tag);
        Assert.Contains(b.GetProperty("items").EnumerateArray(), i => i.GetProperty("subsystem").GetString() == tag);
    }

    [WindowsFact]
    public async Task Anomalies_ReportedFromOtherThreads_WhileTheWorldAnswersQueries_AreAllCountedAndNothingThrows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(Content());
        await world.AddPlayerAsync();
        FeedClient client = await world.ConnectAsync();
        object mine = world.Host.World.Identity;
        string tag = "race" + Guid.NewGuid().ToString("N")[..8];
        const int Threads = 4;
        const int PerThread = 400;

        Task[] reporters = [.. Enumerable.Range(0, Threads).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < PerThread; i++)
            {
                AnomalyLedger.Shared.Report(tag, "c" + t, $"creature {FeedWorld.WolfEntry} spawn {FeedWorld.WolfSpawn}", "d" + i, AnomalySeverity.Warning, mine);
            }
        }))];

        long id = 0;
        while (!reporters.All(r => r.IsCompleted))
        {
            JsonElement r = await AskAsync(client, AnomaliesLine(++id, max: 4096));
            AssertOk(r, id, "anomalies");
        }

        await Task.WhenAll(reporters);
        JsonElement all = await AskAsync(client, AnomaliesLine(++id, max: 4096));
        JsonElement[] mine2 = [.. all.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("subsystem").GetString() == tag)];
        Assert.Equal(Threads * PerThread, mine2.Length);
        Assert.All(mine2, i => Assert.Equal("current", i.GetProperty("pos").GetString()));
        long[] seqs = [.. mine2.Select(i => i.GetProperty("seq").GetInt64())];
        Assert.Equal(seqs.Order(), seqs);
        Assert.Equal(seqs.Length, seqs.Distinct().Count());
    }

    [WindowsFact]
    public async Task TheRing_IsAttachedToTheLedgerWhileTheFeedRuns_AndDetachedWhenItStops()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(Content());
        MapFeedAnomalyRing ring = Assert.IsType<MapFeedAnomalyRing>(world.Feed.AnomalyRing);
        Assert.True(ring.IsSubscribed);

        await world.StopFeedAsync();

        Assert.False(ring.IsSubscribed);
        long accepted = ring.Accepted;
        AnomalyLedger.Shared.Report("stopped" + Guid.NewGuid().ToString("N")[..8], "code", "subject", "d", AnomalySeverity.Info, world.Host.World.Identity);
        Assert.Equal(accepted, ring.Accepted);
    }

    [WindowsFact]
    public async Task ADisabledFeed_NeverAttachesARing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup { StartFeed = false });

        Assert.Null(world.Feed.AnomalyRing);
    }
}
