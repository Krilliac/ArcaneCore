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
        // The only updater, so it is the slowest by construction. A second, faster updater would make "slowest" a wall-clock race
        // (a descheduled 2 ms sleeper can out-time a 25 ms one on a loaded box); the ranking is covered with injected times below.
        map.AddUpdater(new Explaining(25));

        world.RunTick(50);

        string line = Assert.Single(log.Messages, m => m.StartsWith("Slow map update", StringComparison.Ordinal));
        Assert.Contains("Explaining ", line);
        Assert.Contains("(inner stage alpha 1.0 ms)", line);
    }

    [Fact]
    public void Describe_RanksUpdatersSlowestFirst_AndOnlyTheSlowestExplainsItself()
    {
        var diagnostics = new MapUpdateDiagnostics();
        diagnostics.Updaters.Add((new Sleeper(0), 30_000));
        diagnostics.Updaters.Add((new Explaining(0), 90_000));

        string text = diagnostics.Describe();

        Assert.Contains("(inner stage alpha 1.0 ms)", text);
        Assert.True(text.IndexOf("Explaining ", StringComparison.Ordinal) < text.IndexOf("Sleeper ", StringComparison.Ordinal), "slowest first");

        var reversed = new MapUpdateDiagnostics();
        reversed.Updaters.Add((new Explaining(0), 30_000));
        reversed.Updaters.Add((new Sleeper(0), 90_000));

        string other = reversed.Describe();

        Assert.DoesNotContain("inner stage alpha", other); // the slowest (Sleeper) gives no breakdown, and the runner-up is not asked
        Assert.True(other.IndexOf("Sleeper ", StringComparison.Ordinal) < other.IndexOf("Explaining ", StringComparison.Ordinal));
    }

    [Fact]
    public void ADominantSubPhase_IsNamed_WhenSimulationPassesHalfASecond()
    {
        var log = new Capture();
        using WorldRuntime world = Create(log, 100);
        world.GetMap(1, 5).AddUpdater(new Sleeper(520));

        world.RunTick(50);

        string line = Assert.Single(log.Messages, m => m.StartsWith("Slow map update", StringComparison.Ordinal));
        System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(line, @"; DOMINANT Sleeper (\d+\.\d) ms");
        Assert.True(m.Success, line);
        Assert.True(double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) >= 500.0);
    }

    [Fact]
    public void BelowTheThreshold_NoSlowLineIsLogged()
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
