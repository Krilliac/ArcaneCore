using System.Numerics;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Playerbots;
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
        Assert.Contains(frozen, l => l.Contains("frozen at 15", StringComparison.Ordinal));
        Assert.Contains(frozen, l => l.StartsWith("Warning: Liftwatch keep the normal animation", StringComparison.Ordinal) && !l.Contains("Liftgm", StringComparison.Ordinal));
        Assert.True(await world.OnWorldAsync(() => system.GetElevatorControl(car).Frozen));

        string[] speed = await RunAsync(gm, $".debug lift speed 2.5 {id}");
        Assert.Contains(speed, l => l.Contains("speed x2.5 (frozen, applies on resume)", StringComparison.Ordinal));
        Assert.Contains(speed, l => l.StartsWith("Warning: Liftwatch", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(gm, $".debug lift resume {id}"), l => l.Contains("running again from 15", StringComparison.Ordinal));
        Assert.Equal(2.5, await world.OnWorldAsync(() => system.GetElevatorControl(car).Scale));

        Assert.Contains(await RunAsync(gm, $".debug lift reset {id}"), l => l.Contains("back on its default schedule", StringComparison.Ordinal));
        Assert.False(await world.OnWorldAsync(() => system.GetElevatorControl(car).Overridden));
        Assert.Contains(await RunAsync(gm, ".debug lift 999999"), l => l.Contains("999999", StringComparison.Ordinal));
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
