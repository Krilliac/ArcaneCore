using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Ops.Diagnostics;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.World.Tests.Ops;

public sealed class LatencySummaryFeatureTests
{
    [Fact]
    public void RunSummaryPass_WhenNothingWasRecorded_LogsNothing()
    {
        var logger = new CapturingLogger();
        var feature = new LatencySummaryFeature(logger, new LatencyRegistry());

        Assert.Equal(0, feature.RunSummaryPass());
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void RunSummaryPass_LogsOneInformationLinePerActiveMetric_AndTheTopOpcodesLine()
    {
        var registry = new LatencyRegistry();
        for (int i = 1; i <= 100; i++)
        {
            registry.Record(LatencyNames.CharacterSave, i * 100);
            registry.Record(LatencyNames.PacketHandlerPrefix + "CMSG_FAST", i);
            registry.Record(LatencyNames.PacketHandlerPrefix + "CMSG_SLOW", i * 1000);
        }

        var logger = new CapturingLogger();
        var feature = new LatencySummaryFeature(logger, registry);

        Assert.Equal(2, feature.RunSummaryPass());
        Assert.All(logger.Levels, l => Assert.Equal(LogLevel.Information, l));
        Assert.Contains("Latency db.character_save, last 15 min: p50 ", logger.Entries[0], StringComparison.Ordinal);
        Assert.Contains("[since start: 100 samples, p99 ", logger.Entries[0], StringComparison.Ordinal);
        string top = logger.Entries[1];
        Assert.StartsWith("Packet handler latency, top 2 opcodes by p99 (last 15 min): CMSG_SLOW p99 ", top, StringComparison.Ordinal);
        Assert.True(top.IndexOf("CMSG_SLOW", StringComparison.Ordinal) < top.IndexOf("CMSG_FAST", StringComparison.Ordinal));
    }

    [Fact]
    public void OnTick_RunsThePassOncePerInterval()
    {
        var registry = new LatencyRegistry();
        registry.Record(LatencyNames.PathQuery, 500);
        var logger = new CapturingLogger();
        var feature = new LatencySummaryFeature(logger, registry, TimeSpan.FromMilliseconds(100));

        feature.OnTick(60);
        Assert.Empty(logger.Entries);
        feature.OnTick(60);
        Assert.Single(logger.Entries);
        feature.OnTick(60);
        Assert.Single(logger.Entries);
    }

    private sealed class CapturingLogger : ILogger<LatencySummaryFeature>
    {
        public List<string> Entries { get; } = [];
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Levels.Add(logLevel);
            Entries.Add(formatter(state, exception));
        }
    }
}
