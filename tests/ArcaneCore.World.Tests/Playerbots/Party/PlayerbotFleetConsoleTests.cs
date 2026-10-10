using System.Text;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Net;
using ArcaneCore.World.Ops.ServerConsole;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Party;

/// <summary>
/// An executor over a test host's world, with its own DI scope and a recording sink (the pattern of
/// Ops/Console/WorldConsoleTests.ExecutorRig, which is private there). Lines come back without the console prefix.
/// </summary>
internal sealed class FleetConsoleRig : IAsyncDisposable
{
    private readonly WorldTestHost _host;
    private readonly AsyncServiceScope _scope;
    private readonly Sink _output;

    public FleetConsoleRig(WorldTestHost host)
    {
        _host = host;
        _output = new Sink();
        _scope = host.WorldServices.CreateAsyncScope();
        Executor = WorldConsoleExecutor.Create(
            _scope.ServiceProvider, host.Opcodes, host.World, host.Registry, new WorldSessionOptions(), NullLogger.Instance, _output);
    }

    public WorldConsoleExecutor Executor { get; }

    /// <summary>Every line printed since the last <see cref="RunAsync"/>, without the console prefix.</summary>
    public string[] Lines => [.. _output.Snapshot().Select(l => l.StartsWith(WorldConsoleExecutor.Prefix, StringComparison.Ordinal) ? l[WorldConsoleExecutor.Prefix.Length..] : l)];

    /// <summary>
    /// Run <paramref name="line"/> on the world thread (after <paramref name="before"/>, same turn) and return what it printed at once.
    /// The lines are taken on the world thread right after the command returns: a bulk start/stop replies again later from a
    /// worker (<see cref="WaitForLineAsync"/>), and a snapshot taken after this method's await resumed could already hold that
    /// later summary when the continuation is scheduled late (a loaded machine).
    /// </summary>
    public async Task<string[]> RunAsync(string line, Action? before = null)
    {
        _output.Clear();
        return await _host.World.InvokeAsync(() =>
        {
            before?.Invoke();
            Executor.Execute(line);
            return Lines;
        });
    }

    /// <summary>Wait (up to 20 s) for a line a worker replies with later (bulk start/stop); returns all lines then.</summary>
    public async Task<string[]> WaitForLineAsync(Func<string, bool> match, string what)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (!Lines.Any(match))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"timed out waiting for: {what}; got: {string.Join(" | ", Lines)}");
            await Task.Delay(20);
        }

        return Lines;
    }

    public ValueTask DisposeAsync() => _scope.DisposeAsync();

    private sealed class Sink : TextWriter
    {
        private readonly object _gate = new();
        private readonly List<string> _lines = [];

        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            lock (_gate) _lines.Add(value ?? string.Empty);
        }

        public string[] Snapshot()
        {
            lock (_gate) return [.. _lines];
        }

        public void Clear()
        {
            lock (_gate) _lines.Clear();
        }
    }
}

/// <summary>The fleet commands run from the server console: no invoking player, so "to me" needs an explicit <c>to $player</c>.</summary>
public sealed class PlayerbotFleetConsoleTests
{
    [Fact]
    public async Task Console_SummonToNamedPlayer_Works()
    {
        await using FleetWorld world = await FleetWorld.StartAsync();
        await world.AddGmAsync("FLEETCGM", "Fleetcgm");
        await world.AddBotAsync("Fleetcaa", 40);
        await world.AddBotAsync("Fleetcab", 50);

        string[] lines = await world.Console.RunAsync(".playerbot summon Fleetca* to Fleetcgm");

        Assert.Equal(["Playerbot summon: matched=2 moved=2 skipped=0"], lines);
        await world.WaitAroundAsync(["Fleetcaa", "Fleetcab"], "Fleetcgm");
    }

    [Fact]
    public async Task Console_SummonWithoutTo_RefusesWithNeedsAPlayer()
    {
        await using FleetWorld world = await FleetWorld.StartAsync();
        await world.AddGmAsync("FLEETCGM", "Fleetcgm");
        await world.AddBotAsync("Fleetcba", 40);
        (float X, float Y, float Z) before = await world.PositionAsync("Fleetcba");

        string[] summon = await world.Console.RunAsync(".playerbot summon Fleetcba");
        string[] sendHere = await world.Console.RunAsync(".playerbot send Fleetcba here");
        string[] unknown = await world.Console.RunAsync(".playerbot summon Fleetcba to Nobodyatall");

        Assert.Equal([GmStrings.ConsoleNeedsPlayer], summon);
        Assert.Equal([GmStrings.ConsoleNeedsPlayer], sendHere);
        Assert.Equal([GmStrings.PlayerNotFound], unknown);
        Assert.Equal(before, await world.PositionAsync("Fleetcba"));
    }

    [Fact]
    public async Task Console_SendToATele_NeedsNoPlayer()
    {
        await using FleetWorld world = await FleetWorld.StartAsync();
        await world.AddBotAsync("Fleetcca", 40);

        string[] lines = await world.Console.RunAsync(".playerbot send Fleetcca Stormwind");
        string[] missing = await world.Console.RunAsync(".playerbot send Fleetcca Nowhereland");

        Assert.Equal(["Playerbot send: matched=1 moved=1 skipped=0"], lines);
        Assert.Equal([GmStrings.TeleNotFound], missing);
        await world.WaitNearAsync("Fleetcca", 0, -8913.23f, 554.633f, 93.7944f, maxXyDistance: 3.6f);
    }

    [Fact]
    public async Task Console_FollowStayWithoutTo_Refuse_DismissAndStartStopRevive_Work()
    {
        await using FleetWorld world = await FleetWorld.StartAsync(o => o.MaxBots = 1);
        Guid first = await world.AddBotAsync("Fleetcda", 40);
        await world.Bots.CreateAsync("Fleetcdb", 1, 1);

        Assert.Equal([GmStrings.ConsoleNeedsPlayer], await world.Console.RunAsync(".playerbot follow Fleetcda"));
        Assert.Equal([GmStrings.ConsoleNeedsPlayer], await world.Console.RunAsync(".playerbot stay Fleetcd*"));
        Assert.False(world.Bots.IsPartyDriven(first));

        // dismiss, revive, start and stop name their bots and need no player.
        string[] dismiss = await world.Console.RunAsync(".playerbot dismiss Fleetcda");
        Assert.Equal(["Playerbot dismiss: matched=1 dismissed=0 skipped=1 (not-leashed=1)", "Fleetcda: not-leashed"], dismiss);
        string[] revive = await world.Console.RunAsync(".playerbot revive Fleetcd*");
        Assert.Contains("Playerbot revive: matched=2 revived=0 skipped=2 (alive=1 not-running=1)", revive);

        string[] start = await world.Console.RunAsync(".playerbot start Fleetcd*");
        Assert.Equal(["Playerbot start accepted (2)."], start);
        string[] done = await world.Console.WaitForLineAsync(l => l.StartsWith("Playerbot start: matched=2", StringComparison.Ordinal), "the start summary");
        Assert.Contains("Playerbot start: matched=2 started=1 failed=1 (playerbot-capacity=1)", done);
        Assert.Contains("Fleetcdb: playerbot-capacity", done);

        string[] stop = await world.Console.RunAsync(".playerbot stop all");
        Assert.Equal(["Playerbot stop accepted (2)."], stop);
        await world.Console.WaitForLineAsync(l => l.StartsWith("Playerbot stop: matched=2 stopped=2 failed=0", StringComparison.Ordinal), "the stop summary");
    }

    [Fact]
    public async Task Console_NearWithoutPlayer_Refuses()
    {
        await using FleetWorld world = await FleetWorld.StartAsync();
        await world.AddBotAsync("Fleetcea", 5);

        Assert.Equal([GmStrings.ConsoleNeedsPlayer], await world.Console.RunAsync(".playerbot near"));
        Assert.Equal([GmStrings.ConsoleNeedsPlayer], await world.Console.RunAsync(".playerbot near 100"));
        Assert.StartsWith("Syntax: .playerbot near", (await world.Console.RunAsync(".playerbot near 9999")).First(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Console_NothingMatches_FailsClosedWithAClearMessage()
    {
        await using FleetWorld world = await FleetWorld.StartAsync();
        await world.AddGmAsync("FLEETCGM", "Fleetcgm");
        await world.AddBotAsync("Fleetcfa", 40);

        Assert.Equal(["No playerbot matches 'Nope*'."], await world.Console.RunAsync(".playerbot summon Nope* to Fleetcgm"));
        Assert.Equal(["No playerbot matches 'Nope*'."], await world.Console.RunAsync(".playerbot start Nope*"));
        Assert.Equal(["No playerbot matches 'Fleetcgm'."], await world.Console.RunAsync(".playerbot summon Fleetcgm to Fleetcgm"));
        Assert.StartsWith("Syntax: .playerbot summon", (await world.Console.RunAsync(".playerbot summon *")).First(), StringComparison.Ordinal);
        Assert.StartsWith("Syntax: .playerbot summon", (await world.Console.RunAsync(".playerbot summon Fl*ee to Fleetcgm")).First(), StringComparison.Ordinal);
    }
}
