using System.Numerics;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Transports;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Audit;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Elevator;

/// <summary>
/// <c>.debug lift</c> (info, phase, stop, freeze, resume, speed, reset) and <c>.playerbot lift</c> end to end in the Great Lift fixture world
/// (<see cref="ElevatorTestWorld"/>): the commands reach the map's lift, and the bot's ride is listed.
/// </summary>
public sealed class LiftCommandTests
{
    private const uint TopCarSpawn = 16874;
    private static readonly Vector3 TopCar = new(-4665.43f, -1827.67f, 85.4053f);

    [Fact]
    public void TheCommands_HaveTheirSecurityLevels()
    {
        CommandTable table = ChatCommands.CreateTable();
        Assert.Null(table.Resolve("debug lift", AccountSecurity.Moderator));
        Assert.NotNull(table.Resolve("debug lift", AccountSecurity.GameMaster));
        Assert.Null(table.Resolve("playerbot lift", AccountSecurity.Moderator));
        Assert.NotNull(table.Resolve("playerbot lift", AccountSecurity.GameMaster));
        foreach (string change in new[] { "phase", "stop", "freeze", "resume", "speed", "reset" })
        {
            Assert.Null(table.Resolve($"debug lift {change}", AccountSecurity.GameMaster));
            Assert.NotNull(table.Resolve($"debug lift {change}", AccountSecurity.Administrator));
        }
    }

    [Fact]
    public async Task TheInfoAndTheChanges_ReachTheLiftOfTheMap()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        await using WorldTestClient gm = await world.ObserverAsync("LIFTGM", "Liftgm", TopCar + new Vector3(8f, 0f, 2f), AccountSecurity.Administrator);
        await gm.CollectAsync();
        GameObjectMapSystem system = world.Objects()!;
        GameObject car = await world.OnWorldAsync(() => system.GameObjects.Single(g => g.Spawn!.Guid == TopCarSpawn));
        uint id = car.Guid.Counter;

        string[] info = await RunAsync(gm, $".debug lift {id}");
        Assert.Contains(info, l => l.StartsWith($"Mesa Elevator (entry {car.Entry}, guid {id}): cycle 30000 ms, at ", StringComparison.Ordinal)
            && l.Contains("speed x1", StringComparison.Ordinal) && l.EndsWith("schedule default.", StringComparison.Ordinal));
        Assert.Contains(info, l => l.StartsWith("  stop 1: from 0 ms for 5000 ms", StringComparison.Ordinal));
        Assert.Contains(info, l => l.StartsWith("  stop 2: from 15000 ms for 5000 ms", StringComparison.Ordinal));
        Assert.Contains(info, l => l.StartsWith("  next stop in ", StringComparison.Ordinal));
        Assert.Contains(info, l => l.StartsWith("  in view of ", StringComparison.Ordinal) && l.Contains("Liftgm", StringComparison.Ordinal));

        Assert.Contains(await RunAsync(gm, $".debug lift phase 12000 {id}"), l => l.Contains("now at 12000 ms of 30000", StringComparison.Ordinal));
        Assert.True(await world.OnWorldAsync(() => system.GetElevatorControl(car).Overridden));
        Assert.Equal(12000u, await world.OnWorldAsync(() => system.ElevatorProgressAhead(car, 0)));

        Assert.Contains(await RunAsync(gm, $".debug lift stop 2 {id}"), l => l.Contains("at the start of stop 2 (15000 ms, stays 5000 ms)", StringComparison.Ordinal));
        Assert.Equal(15000u, await world.OnWorldAsync(() => system.ElevatorProgressAhead(car, 0)));
        Assert.Contains(await RunAsync(gm, $".debug lift stop 9 {id}"), l => l.Contains("has stops 1 to 2", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(gm, $".debug lift phase"), l => l.StartsWith("Syntax:", StringComparison.Ordinal));

        await using WorldTestClient watcher = await world.ObserverAsync("LIFTWATCH", "Liftwatch", TopCar + new Vector3(10f, 0f, 2f));
        await watcher.CollectAsync();
        string[] frozen = await RunAsync(gm, $".debug lift freeze {id}");
        // The world clock is manual and ObserverAsync advances it by a varying amount while the watcher logs in, so the lift has moved on
        // from stop 2's start (15000 ms) by an unpredictable amount before the freeze: assert "15000 or later in the cycle", not 15xxx.
        // Phase, stop, freeze and resume themselves run on the world thread between clock advances, so those readings stay exact.
        System.Text.RegularExpressions.Match frozenAt = frozen.Select(l => System.Text.RegularExpressions.Regex.Match(l, @"frozen at (\d+) ms on the server\."))
            .FirstOrDefault(m => m.Success) ?? System.Text.RegularExpressions.Match.Empty;
        Assert.True(frozenAt.Success, "no 'frozen at <n> ms on the server.' line in: " + string.Join(" | ", frozen));
        uint frozenMs = uint.Parse(frozenAt.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(frozenMs, 15000u, 29999u);
        Assert.Contains(frozen, l => l.StartsWith("Warning: Liftwatch keep the normal animation", StringComparison.Ordinal) && !l.Contains("Liftgm", StringComparison.Ordinal));
        Assert.True(await world.OnWorldAsync(() => system.GetElevatorControl(car).Frozen));

        string[] speed = await RunAsync(gm, $".debug lift speed 2.5 {id}");
        Assert.Contains(speed, l => l.Contains("speed x2.5 (frozen, applies on resume)", StringComparison.Ordinal));
        Assert.Contains(speed, l => l.StartsWith("Warning: Liftwatch", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(gm, $".debug lift resume {id}"), l => l.Contains($"running again from {frozenMs} ms;", StringComparison.Ordinal));
        Assert.Equal(2.5, await world.OnWorldAsync(() => system.GetElevatorControl(car).Scale));

        Assert.Contains(await RunAsync(gm, $".debug lift reset {id}"), l => l.Contains("back on its default schedule", StringComparison.Ordinal));
        Assert.False(await world.OnWorldAsync(() => system.GetElevatorControl(car).Overridden));

        string[] fast = await RunAsync(gm, $".debug lift speed 50 {id}");
        Assert.Contains(fast, l => l.Contains("speed x10 (requested x50, clamped to 0.1-10);", StringComparison.Ordinal));
        string[] exact = await RunAsync(gm, $".debug lift speed 4 {id}");
        Assert.Contains(exact, l => l.Contains("speed x4;", StringComparison.Ordinal) && !l.Contains("clamped", StringComparison.Ordinal));
        Assert.Equal(["No transport game object with guid 999999 on this map."], await RunAsync(gm, ".debug lift 999999"));
    }

    [Fact]
    public async Task EachChange_RecordsOneDebugWriteWithItsOldAndNewValue_AndAFreezeOfAFrozenLiftRecordsNothing()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync();
        await using WorldTestClient gm = await world.ObserverAsync("LIFTGM", "Liftgm", TopCar + new Vector3(8f, 0f, 2f), AccountSecurity.Administrator);
        await gm.CollectAsync();
        GameObjectMapSystem system = world.Objects()!;
        GameObject car = await world.OnWorldAsync(() => system.GameObjects.Single(g => g.Spawn!.Guid == TopCarSpawn));
        uint id = car.Guid.Counter;
        DebugWriteLog log = world.Host.WorldServices.GetRequiredService<GmAuditFeature>().DebugWrites;
        DebugWriteEntry[] Writes() => [.. log.Tail(100).Where(e => e.Command.Contains("debug lift", StringComparison.Ordinal))];
        DebugWriteEntry Next(string verb, int expectedCount)
        {
            DebugWriteEntry[] all = Writes();
            Assert.Equal(expectedCount, all.Length);
            DebugWriteEntry entry = all[^1];
            Assert.EndsWith("debug lift " + verb, entry.Command, StringComparison.Ordinal);
            Assert.StartsWith($"Mesa Elevator (entry {car.Entry}, guid {id})", entry.Target, StringComparison.Ordinal);
            Assert.Equal(DebugWriteScope.Live, entry.Scope);
            return entry;
        }

        await RunAsync(gm, $".debug lift phase 12000 {id}");
        DebugWriteEntry phase = Next("phase", 1);
        Assert.Equal(("phase ms", "12000"), (phase.Field, phase.NewValue));
        Assert.NotEmpty(phase.OldValue);

        await RunAsync(gm, $".debug lift stop 2 {id}");
        DebugWriteEntry stop = Next("stop", 2);
        Assert.Equal(("phase ms", "12000", "15000"), (stop.Field, stop.OldValue, stop.NewValue));

        await RunAsync(gm, $".debug lift freeze {id}");
        DebugWriteEntry freeze = Next("freeze", 3);
        Assert.Equal(("frozen", "false", "true"), (freeze.Field, freeze.OldValue, freeze.NewValue));

        await RunAsync(gm, $".debug lift freeze {id}");
        Assert.Equal(3, Writes().Length);

        await RunAsync(gm, $".debug lift speed 2.5 {id}");
        DebugWriteEntry speed = Next("speed", 4);
        Assert.Equal(("speed scale", "1", "2.5"), (speed.Field, speed.OldValue, speed.NewValue));

        await RunAsync(gm, $".debug lift resume {id}");
        DebugWriteEntry resume = Next("resume", 5);
        Assert.Equal(("frozen", "true", "false"), (resume.Field, resume.OldValue, resume.NewValue));

        await RunAsync(gm, $".debug lift reset {id}");
        DebugWriteEntry reset = Next("reset", 6);
        Assert.Equal(("schedule", "default schedule"), (reset.Field, reset.NewValue));
        Assert.Matches(@"^x2\.5 at \d+ ms$", reset.OldValue);

        await RunAsync(gm, $".debug lift freeze {id}");
        Assert.Equal(7, Writes().Length);
        await RunAsync(gm, $".debug lift reset {id}");
        DebugWriteEntry resetFrozen = Next("reset", 8);
        Assert.Matches(@"^x1 frozen at \d+ ms$", resetFrozen.OldValue);

        // A refused or failed change records nothing.
        int count = Writes().Length;
        await RunAsync(gm, $".debug lift stop 9 {id}");
        await RunAsync(gm, ".debug lift phase");
        Assert.Equal(count, Writes().Length);
    }

    [Fact]
    public async Task WithoutAGuid_TheNearestLiftIsPicked_EvenWhenAnotherKindOfObjectIsCloser()
    {
        const uint doorEntry = 90001;
        var door = new GameObjectTemplate
        {
            Entry = doorEntry, Type = (uint)GameObjectType.Door, DisplayId = 1, Name = "Nearby Door", Data = new uint[GameObjectTemplate.DataCount],
        };
        Vector3 doorAt = TopCar + new Vector3(8f, 0f, 2f);
        GameObjectContent content = new(
            [.. ElevatorTestData.Cars.Select(c => c.Entry).Distinct().Select(ElevatorTestData.Template), door],
            [.. ElevatorTestData.Cars.Where(c => c.Guid is TopCarSpawn or 16876).Select(ElevatorTestData.Spawn),
                new GameObjectSpawn { Guid = 90001, Entry = doorEntry, MapId = ElevatorTestData.Kalimdor, X = doorAt.X + 1f, Y = doorAt.Y, Z = doorAt.Z, SpawnTimeSeconds = 180 }],
            [], [], []);
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync(configure: services => services.AddScoped<IGameObjectDataStore>(_ => new FixedStore(content)));
        await using WorldTestClient gm = await world.ObserverAsync("LIFTGM", "Liftgm", doorAt, AccountSecurity.Administrator);
        await gm.CollectAsync();
        GameObjectMapSystem system = world.Objects()!;
        Assert.True(await world.OnWorldAsync(() => system.GameObjects.Any(g => g.Entry == doorEntry)), "the door was not spawned");

        string[] info = await RunAsync(gm, ".debug lift");
        Assert.Contains(info, l => l.StartsWith($"Mesa Elevator (entry {ElevatorTestData.Cars[0].Entry}, guid {TopCarSpawn}", StringComparison.Ordinal));
        Assert.DoesNotContain(info, l => l.Contains("Nearby Door", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FreezeAndSpeed_AreRefusedOnAMapWideTram_ButPhaseAndResetWork()
    {
        // The top car travels 3000 yards sideways, farther than the visibility range: a map-wide car like the Deeprun Tram's.
        TransportAnimationCatalog tram = new([.. ElevatorTestData.Paths.SelectMany(p => p.Value.Select(n =>
            (p.Key, new TransportAnimationNode(n.T, p.Key == 11898 && n.T == 15000 ? 3000f : 0f, 0, n.Z))))]);
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync(tram);
        await using WorldTestClient gm = await world.ObserverAsync("LIFTGM", "Liftgm", TopCar + new Vector3(8f, 0f, 2f), AccountSecurity.Administrator);
        await gm.CollectAsync();
        GameObjectMapSystem system = world.Objects()!;
        GameObject car = await world.OnWorldAsync(() => system.GameObjects.Single(g => g.Spawn!.Guid == TopCarSpawn));
        Assert.True(await world.OnWorldAsync(() => system.IsMapWideElevator(car)), "the fixture car is not map-wide");
        uint id = car.Guid.Counter;

        Assert.Contains(await RunAsync(gm, $".debug lift freeze {id}"), l => l.Contains("map-wide tram car: 'freeze' is refused", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(gm, $".debug lift speed 3 {id}"), l => l.Contains("map-wide tram car: 'speed' is refused", StringComparison.Ordinal));
        Assert.False(await world.OnWorldAsync(() => system.GetElevatorControl(car).Overridden));

        Assert.Contains(await RunAsync(gm, $".debug lift phase 12000 {id}"), l => l.Contains("now at 12000 ms of 30000", StringComparison.Ordinal));
        Assert.True(await world.OnWorldAsync(() => system.GetElevatorControl(car).Overridden));
        Assert.Contains(await RunAsync(gm, $".debug lift reset {id}"), l => l.Contains("back on its default schedule", StringComparison.Ordinal));
        Assert.False(await world.OnWorldAsync(() => system.GetElevatorControl(car).Overridden));
    }

    private sealed class FixedStore(GameObjectContent content) : IGameObjectDataStore
    {
        public Task<GameObjectContent> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(content);
    }

    [Fact]
    public async Task PlayerbotLift_ListsTheRidingBot_AndNamesItsStage()
    {
        await using ElevatorTestWorld world = await ElevatorTestWorld.StartAsync(creatures: RatchetEnder());
        await using WorldTestClient gm = await world.ObserverAsync("LIFTGM", "Liftgm", TopCar + new Vector3(8f, 0f, 2f), AccountSecurity.Administrator);
        await gm.CollectAsync();
        string bot = world.Player.Name;
        string[] none = await RunAsync(gm, ".playerbot lift");
        Assert.Equal(["0 bot(s) riding a lift."], none);

        await world.PlaceAsync(ElevatorTestWorld.CanyonStart);
        var options = new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 100, Population = { TramMinLevel = 0 } };
        var destinations = new PlayerbotWorldDestinations(world.Session, options);
        Assert.True(await world.OnWorldAsync(() => destinations.Update(world.Player, 3453, 100)), "the destination attempted nothing");
        var brain = new PlayerbotBrain(world.Session, options);
        ElevatorStage? stage = null;
        for (int step = 0; step < 3_000 && stage is not (ElevatorStage.Aboard or ElevatorStage.Done or ElevatorStage.Failed); step++)
        {
            stage = await world.TickAsync(() =>
            {
                brain.Update(100);
                return PlayerbotElevator.TripOf(world.Player)?.Stage;
            });
        }

        Assert.Equal(ElevatorStage.Aboard, stage);
        string[] list = await RunAsync(gm, ".playerbot lift");
        Assert.Equal(2, list.Length);
        Assert.StartsWith($"{bot}: Aboard Low->High on ", list[0], StringComparison.Ordinal);
        Assert.Contains(", car entry ", list[0], StringComparison.Ordinal);
        Assert.Contains("route ", list[0], StringComparison.Ordinal);
        Assert.Equal("1 bot(s) riding a lift.", list[1]);

        string[] detail = await RunAsync(gm, $".playerbot lift {bot}");
        Assert.StartsWith($"{bot}: Aboard on ", detail[0], StringComparison.Ordinal);
        Assert.Contains(detail, l => l.Contains("ms in this stage", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(gm, ".playerbot lift Nobodyhere"), l => l == "Nobodyhere is not riding a lift.");
    }

    private static CreatureContent RatchetEnder() => new(
        [new CreatureTemplate { Entry = 3453, Name = "Wharfmaster Dizzywig", DisplayIds = [49] }],
        [new CreatureSpawn { Guid = 14419, Entry = 3453, MapId = ElevatorTestData.Kalimdor, X = ElevatorTestWorld.Ratchet.X,
            Y = ElevatorTestWorld.Ratchet.Y, Z = ElevatorTestWorld.Ratchet.Z }], [], [], []);

    private static async Task<string[]> RunAsync(WorldTestClient client, string command)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, command);
        return await client.CollectChatLinesAsync();
    }
}
