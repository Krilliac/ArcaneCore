using System.Collections.Concurrent;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using Microsoft.Extensions.Logging;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Ops;

/// <summary>The sub-phase breakdown the slow-map-update line carries (<see cref="MapUpdateDiagnostics.Describe"/>).</summary>
public sealed class SlowMapBreakdownTests
{
    private sealed class Capture : ILogger<WorldRuntime>
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Enqueue(formatter(state, exception));
    }

    private sealed class Sleeper(int ms) : IMapUpdater
    {
        public void Update(Map map, uint diffMs) => Thread.Sleep(ms);

        public void OnPlayerRemoved(Map map, Player player)
        {
        }
    }

    private sealed class Explaining(int ms) : IMapUpdater, IMapUpdateBreakdown
    {
        public void Update(Map map, uint diffMs) => Thread.Sleep(ms);

        public void OnPlayerRemoved(Map map, Player player)
        {
        }

        public string DescribeLastUpdate() => "inner stage alpha 1.0 ms";
    }

    private static WorldRuntime Create(Capture log, int slowMs)
    {
        var options = new WorldRuntimeOptions { TickIntervalMs = 5, UpdateCompressionThreshold = 0, AutosaveIntervalMs = 0 };
        options.Perf.SlowMapUpdate = slowMs;
        return new WorldRuntime(options, new RecordingSaveQueue(), log);
    }

    [Fact]
    public void SlowLine_NamesTheSimulationSubPhases_TheSlowestUpdater_AndTheTileCounter()
    {
        var log = new Capture();
        using WorldRuntime world = Create(log, 1);
        world.GetMap(1, 3).AddUpdater(new Sleeper(25));

        world.RunTick(50);

        string line = Assert.Single(log.Messages, m => m.StartsWith("Slow map update", StringComparison.Ordinal));
        Assert.Contains("simulation: packets ", line);
        Assert.Contains(", heartbeat ", line);
        Assert.Contains(", active cells ", line);
        Assert.Contains(", updaters Sleeper ", line);
        Assert.Contains("cleanup: grids ", line);
        Assert.Contains("grid lifecycle: created ", line);
        Assert.Contains("navmesh tile reads on world thread: 0 (0.0 ms)", line);
        Assert.DoesNotContain("DOMINANT", line); // 25 ms is nowhere near the 500 ms bar
    }

    [Fact]
    public void SlowestUpdater_ExplainsItself_WhenItGivesABreakdown()
    {
        var log = new Capture();
        using WorldRuntime world = Create(log, 1);
        Map map = world.GetMap(1, 4);
        map.AddUpdater(new Sleeper(2));
        map.AddUpdater(new Explaining(25));

        world.RunTick(50);

        string line = Assert.Single(log.Messages, m => m.StartsWith("Slow map update", StringComparison.Ordinal));
        Assert.Contains("Explaining ", line);
        Assert.Contains("(inner stage alpha 1.0 ms)", line);
        Assert.True(line.IndexOf("Explaining ", StringComparison.Ordinal) < line.IndexOf("Sleeper ", StringComparison.Ordinal), "slowest first");
    }

    [Fact]
    public void ADominantSubPhase_IsNamed_WhenSimulationPassesHalfASecond()
    {
        var log = new Capture();
        using WorldRuntime world = Create(log, 100);
        world.GetMap(1, 5).AddUpdater(new Sleeper(520));

        world.RunTick(50);

        string line = Assert.Single(log.Messages, m => m.StartsWith("Slow map update", StringComparison.Ordinal));
        Assert.Matches(@"; DOMINANT Sleeper 5[2-9]\d\.\d ms", line);
    }

    [Fact]
    public void AtOrBelowTheThreshold_NothingIsFormatted()
    {
        var log = new Capture();
        using WorldRuntime world = Create(log, 10_000);
        world.GetMap(1, 6).AddUpdater(new Sleeper(2));

        world.RunTick(50);

        Assert.DoesNotContain(log.Messages, m => m.Contains("Slow map update", StringComparison.Ordinal));
    }

    [Fact]
    public void CreatureSystem_ReportsItsStages_AndTheSlowestCreature()
    {
        var content = new CreatureContent([Template()], [Spawn(1, WolfEntry, 15f, 0f)], [], [], [], CreatureAiContent.Empty);
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content);
        using (world)
        {
            AddPlayer(world, 1, 0, 0);
            MapUpdateDiagnostics diagnostics = map.ReusableDiagnostics();
            map.Update(100, diagnostics);

            string text = ((IMapUpdateBreakdown)system).DescribeLastUpdate();

            Assert.Contains("creatures ", text);
            Assert.Contains("over 1/1 active", text);
            Assert.Contains("catch-up moves ", text);
            Assert.Contains("spawn groups ", text);
            Assert.Contains($"slowest creature entry {WolfEntry} ", text);
            Assert.Contains(diagnostics.Updaters, u => ReferenceEquals(u.Updater, system)); // timed like every updater
        }
    }
}
