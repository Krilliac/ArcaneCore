using System.Diagnostics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// Per-bot think attribution (live-w24-r1, 2026-10-11 13:01:27: a 2 139 ms ManagedPlayerbotFeature tick named no bot, goal or phase):
/// <see cref="PlayerbotThinkProbe"/> splits one think by phase and <see cref="PlayerbotSlowThinkLog"/> logs the slow ones, rate-limited.
/// </summary>
public sealed class PlayerbotThinkProbeTests
{
    private static readonly Guid Bot = Guid.Parse("6f1c0f4e-6a8e-4b9f-9d7e-3f1e2a6b7c11");

    /// <summary>Burn the CPU for <paramref name="ms"/> milliseconds (a think busy in its phase, not asleep).</summary>
    private static void Spin(double ms)
    {
        long until = Stopwatch.GetTimestamp() + (long)(ms * Stopwatch.Frequency / 1000);
        while (Stopwatch.GetTimestamp() < until) Thread.SpinWait(50);
    }

    /// <summary>A fake think: a short "threats" phase, then <paramref name="questsMs"/> in "quests".</summary>
    private static PlayerbotThinkProbe Think(double questsMs)
    {
        var probe = new PlayerbotThinkProbe();
        probe.Begin();
        try
        {
            PlayerbotThinkProbe.Phase("threats");
            Spin(1);
            PlayerbotThinkProbe.Phase("quests");
            Spin(questsMs);
        }
        finally
        {
            probe.End();
        }
        return probe;
    }

    [Fact]
    public void SlowFakeThink_IsLoggedWithBotGoalAndSlowestPhase()
    {
        var logger = new ListLogger();
        var log = new PlayerbotSlowThinkLog(logger);
        PlayerbotThinkProbe probe = Think(questsMs: 60);

        Assert.True(log.Observe(probe, thresholdMs: 50, nowMs: 1000, Bot, "Slowpoke", "brain", "Quest entry 1196 quest 179"));

        string line = Assert.Single(logger.Lines);
        Assert.Contains(Bot.ToString(), line);
        Assert.Contains("(Slowpoke) slow think", line);
        Assert.Contains("by its brain: goal Quest entry 1196 quest 179", line);
        Assert.Contains("slowest phase quests", line);
        Assert.Contains("phases quests ", line);
        Assert.Contains("path 0 queries", line);
        Assert.True(probe.ElapsedMs >= 60, $"the think took {probe.ElapsedMs} ms");
        Assert.Equal("quests", probe.SlowestPhase);
        Assert.Null(PlayerbotThinkProbe.Active);
    }

    [Fact]
    public void FastThink_OrThresholdOff_IsNotLogged()
    {
        var logger = new ListLogger();
        var log = new PlayerbotSlowThinkLog(logger);

        Assert.False(log.Observe(Think(questsMs: 1), thresholdMs: 10_000, nowMs: 0, Bot, "Quick", "brain", "Explore entry 0 quest 0"));
        Assert.False(log.Observe(Think(questsMs: 5), thresholdMs: 0, nowMs: 0, Bot, "Off", "brain", "Explore entry 0 quest 0"));
        Assert.Empty(logger.Lines);
        Assert.Equal(0, log.Slow);
    }

    [Fact]
    public void SlowThinks_AreRateLimitedPerWindow_AndTheNextLineCountsTheOnesLeftOut()
    {
        var logger = new ListLogger();
        var log = new PlayerbotSlowThinkLog(logger);
        PlayerbotThinkProbe probe = Think(questsMs: 3);
        int extra = 5;
        for (int i = 0; i < PlayerbotSlowThinkLog.MaxLinesPerWindow + extra; i++)
            Assert.True(log.Observe(probe, thresholdMs: 1, nowMs: 10_000 + i, Bot, "Spiky", "brain", "Grind entry 0 quest 0"));

        Assert.Equal(PlayerbotSlowThinkLog.MaxLinesPerWindow, logger.Lines.Count);
        Assert.Equal(PlayerbotSlowThinkLog.MaxLinesPerWindow + extra, log.Slow);

        // The next window: logged again, with the count left out of the last one.
        log.Observe(probe, thresholdMs: 1, nowMs: 10_000 + PlayerbotSlowThinkLog.WindowMs, Bot, "Spiky", "brain", "Grind entry 0 quest 0");
        Assert.Equal(PlayerbotSlowThinkLog.MaxLinesPerWindow + 1, logger.Lines.Count);
        Assert.Contains($"({extra} slow thinks not logged before this one)", logger.Lines[^1]);
    }

    [Fact]
    public void PhaseOutsideAThink_DoesNothing_AndPhasesAccumulateByName()
    {
        PlayerbotThinkProbe.Phase("nowhere"); // no probe running: ignored
        var probe = new PlayerbotThinkProbe();
        probe.Begin();
        try
        {
            PlayerbotThinkProbe.Phase("a");
            Spin(4);
            PlayerbotThinkProbe.Phase("b"); // no work in b: preemption can only lengthen "a", or land in the few ticks between the two marks
            PlayerbotThinkProbe.Phase("a");
            Spin(4);
        }
        finally
        {
            probe.End();
        }

        var phases = probe.Phases().ToDictionary(p => p.Phase, p => p.Ms);
        Assert.Equal("a", probe.SlowestPhase);
        Assert.True(phases["a"] >= 8, $"phase a {phases["a"]} ms");
        Assert.True(phases.ContainsKey("b"));
        Assert.DoesNotContain("nowhere", phases.Keys);
        PlayerbotThinkProbe.Phase("after"); // ended: ignored again
        Assert.Null(PlayerbotThinkProbe.Active);
    }

    [Fact]
    public void TileAttribution_ReportsSlowestTileAndPrefetchState_WithoutLeakingIntoNextThink()
    {
        var probe = new PlayerbotThinkProbe();
        probe.Begin();
        try
        {
            PlayerbotThinkProbe.Phase("explore atlas find");
            PathSearchCounters.RecordTileLoad(100, 0, 31, 32, prefetchHit: true);
            PathSearchCounters.RecordTileLoad(400, 1, 22, 23, prefetchHit: false);
        }
        finally { probe.End(); }

        Assert.Equal(2, probe.Search.TileLoads);
        Assert.Equal(new TileLoadDetail(1, 22, 23, 400, false), probe.SlowestTile);
        Assert.Contains("slowest tile map 1 (22, 23)", probe.Describe());
        Assert.Contains("prefetch miss", probe.Describe());

        var next = new PlayerbotThinkProbe();
        next.Begin();
        try { PlayerbotThinkProbe.Phase("tram ride"); }
        finally { next.End(); }
        Assert.Equal(0, next.Search.TileLoads);
        Assert.Equal(default, next.SlowestTile);
    }

    internal sealed class ListLogger : ILogger
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception));
    }
}
