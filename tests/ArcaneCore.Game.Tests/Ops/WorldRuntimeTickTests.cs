using System.Collections.Concurrent;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.Game.Tests.Ops;

/// <summary>Tick measurement and the vmangos-style slow-update log (Perf.log).</summary>
public sealed class WorldRuntimeTickTests
{
    private static WorldRuntime Create(CapturingLogger log, Action<WorldRuntimeOptions>? tune = null)
    {
        var options = new WorldRuntimeOptions { TickIntervalMs = 5, UpdateCompressionThreshold = 0, AutosaveIntervalMs = 0 };
        tune?.Invoke(options);
        return new WorldRuntime(options, new RecordingSaveQueue(), log);
    }

    private static void WaitFor(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for the world thread");
            Thread.Sleep(2);
        }
    }

    [Fact]
    public void Run_RecordsEveryTick_AndTheHeartbeatAdvances()
    {
        using WorldRuntime world = Create(new CapturingLogger());
        world.Start();
        WaitFor(() => world.Stats.Snapshot().TotalTicks >= 5);

        TickStatsSnapshot first = world.Stats.Snapshot();
        WaitFor(() => world.Stats.Snapshot().TotalTicks > first.TotalTicks + 3);
        TickStatsSnapshot second = world.Stats.Snapshot();
        Assert.True(second.LastTickTimestamp > first.LastTickTimestamp);
        Assert.True(second.Samples > 0);
    }

    [Fact]
    public void SlowCommand_IsCountedAsAnOverrun_AndMeasuredInTheMaximum()
    {
        using WorldRuntime world = Create(new CapturingLogger());
        world.Start();
        WaitFor(() => world.Stats.Snapshot().TotalTicks >= 3);
        long before = world.Stats.Snapshot().Overruns;

        world.Post(() => Thread.Sleep(80));
        WaitFor(() => world.Stats.Snapshot().Overruns > before);

        Assert.True(world.Stats.Snapshot().MaxMicros >= 80_000 - 5_000);
    }

    [Fact]
    public void SlowWorldUpdate_LogsWithThePerfEventId()
    {
        var log = new CapturingLogger();
        using WorldRuntime world = Create(log, o => o.Perf.SlowWorldUpdate = 30);
        world.Start();
        world.Post(() => Thread.Sleep(70));
        WaitFor(() => log.Entries.Any(e => e.EventName == PerformanceLogOptions.EventName));

        CapturingLogger.Entry entry = log.Entries.First(e => e.EventName == PerformanceLogOptions.EventName);
        Assert.Contains("Slow world update", entry.Message);
        Assert.Equal(LogLevel.Warning, entry.Level);
    }

    [Fact]
    public void SlowWorldUpdate_ThresholdZero_LogsNothing()
    {
        var log = new CapturingLogger();
        using WorldRuntime world = Create(log, o => o.Perf.SlowWorldUpdate = 0);
        world.Start();
        long before = 0;
        world.Post(() => Thread.Sleep(70));
        WaitFor(() => world.Stats.Snapshot().Overruns > before);
        Thread.Sleep(30);
        Assert.DoesNotContain(log.Entries, e => e.EventName == PerformanceLogOptions.EventName);
    }

    [Fact]
    public void SlowMapUpdate_NamesTheMapAndInstance()
    {
        var log = new CapturingLogger();
        using WorldRuntime world = Create(log, o => o.Perf.SlowMapUpdate = 20);
        Map map = world.GetMap(1, 7);
        map.AddUpdater(new SleepingUpdater(50));

        world.RunTick(50);

        CapturingLogger.Entry entry = Assert.Single(log.Entries, e => e.EventName == PerformanceLogOptions.EventName);
        Assert.Contains("Slow map update", entry.Message);
        Assert.Contains("map 1", entry.Message);
        Assert.Contains("instance 7", entry.Message);
    }

    [Fact]
    public void RunTick_CalledDirectly_StillWorksWithoutStats()
    {
        using WorldRuntime world = Create(new CapturingLogger());
        var ran = false;
        world.Post(() => ran = true);
        world.RunTick(50);
        Assert.True(ran);
        Assert.Equal(0, world.Stats.Snapshot().TotalTicks); // only the world loop records
    }

    private sealed class SleepingUpdater(int ms) : IMapUpdater
    {
        public void Update(Map map, uint diffMs) => Thread.Sleep(ms);

        public void OnPlayerRemoved(Map map, Player player)
        {
        }
    }

    private sealed class CapturingLogger : ILogger<WorldRuntime>
    {
        public ConcurrentQueue<Entry> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Enqueue(new Entry(logLevel, eventId.Name, formatter(state, exception)));

        public sealed record Entry(LogLevel Level, string? EventName, string Message);
    }
}
