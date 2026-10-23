using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Ops.Watchdog;
using ArcaneCore.Kernel.Ops.Watchdog.Counters;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Bots;
using ArcaneCore.World.Ops.Watchdog;
using ArcaneCore.World.Playerbots.Qa;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Ops;

/// <summary>
/// The completed-hang line names the frame that hung, driven tick by tick on a bare <see cref="WorldRuntime"/> with a fake watchdog clock (no sleeps, no threads):
/// the watchdog's frame boundary is the start of a tick, so the frame's own tick is complete when the frame is closed.
/// </summary>
public sealed class TickWatchdogHungFrameTests : IDisposable
{
    private sealed class ManualClock : WatchdogClock
    {
        private long _micros = 1_000_000;

        public override long NowMicros => Volatile.Read(ref _micros);

        public void AdvanceMs(long ms) => Interlocked.Add(ref _micros, ms * 1000);
    }

    private sealed class CapturingLogger : ILogger<TickMonitor>
    {
        public List<(int Event, string Message)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add((eventId.Id, formatter(state, exception)));
    }

    /// <summary>A world feature that only has to be named in the top features of its tick.</summary>
    private sealed class MarkerFeature
    {
        public void OnUpdate(uint diffMs)
        {
        }
    }

    /// <summary>The bot QA hang sink as the playerbots feature wires it: every report becomes a TickHang signal on the real bus.</summary>
    private sealed class BusHangSink(PlayerbotQaSignalBus bus) : IBotTickHangSink
    {
        public TimeSpan Warmup => TimeSpan.Zero;

        public void ReportHang(string phase, string work, string message, long occurrences)
            => bus.TryWrite(new QaSignal { Kind = QaSignalKind.TickHang, AtUtc = DateTime.UtcNow, Stage = phase, Reason = work, Message = message, Occurrences = occurrences });
    }

    private readonly PlayerbotQaSignalBus _bus = new(64);
    private readonly ManagedBotRegistry _bots = new();
    private readonly ManualClock _clock = new();
    private readonly CapturingLogger _log = new();
    private readonly WorldRuntime _world = new(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, new NullSaveQueue(), NullLogger<WorldRuntime>.Instance);
    private readonly ServiceProvider _provider;
    private readonly TickMonitor _monitor;
    private readonly TickWatchdogFeature _feature;

    public TickWatchdogHungFrameTests()
    {
        var options = new TickMonitorOptions { HangMs = 2000 };
        _monitor = new TickMonitor(options, _clock, new CounterRegistry(), _log);
        _bots.UseHangSink(new BusHangSink(_bus));
        _provider = new ServiceCollection().AddSingleton(_monitor).AddSingleton<WatchdogClock>(_clock).AddSingleton(_bots).BuildServiceProvider();
        _feature = new TickWatchdogFeature(_provider, NullLogger<TickWatchdogFeature>.Instance);
        _feature.Attach(_world);
    }

    public void Dispose()
    {
        _world.Dispose();
        _provider.Dispose();
    }

    private string[] HangLines() => [.. _log.Lines.Where(l => l.Event == WatchdogEvents.TickHang.Id).Select(l => l.Message)];

    [Fact]
    public void ABareWorld_TicksAndFeedsTheMonitorFromItsTickStart()
    {
        _world.RunTick(50);
        _clock.AdvanceMs(100);
        _world.RunTick(50);

        Assert.Equal(1, _monitor.Ring.Count);
    }

    [Fact]
    public void BackToBackHangs_ReportTheWorstFrame_AndSayHowManyThereWere()
    {
        _world.Post(() => { });
        _world.RunTick(50); // tick A runs one command
        _clock.AdvanceMs(3_000);
        var marker = new MarkerFeature();
        _world.Updated += marker.OnUpdate;
        _world.RunTick(50); // tick B closes A's 3 s frame and runs the marker feature
        _clock.AdvanceMs(5_000);
        _world.RunTick(50); // tick C closes B's 5 s frame
        _monitor.Check(_clock.NowMicros);

        string line = Assert.Single(HangLines());
        Assert.Contains("2 new hang(s)", line);
        Assert.Contains("hung frame 5000 ms", line);
        Assert.Contains("MarkerFeature", line);
        Assert.Contains("2 hung frames since the last report", line);
        Assert.DoesNotContain("3000", line);

        // The report consumed both; the next hang is reported alone.
        _clock.AdvanceMs(2_500);
        _world.RunTick(50);
        _monitor.Check(_clock.NowMicros);
        string next = HangLines().Last();
        Assert.Contains("hung frame 2500 ms", next);
        Assert.DoesNotContain("hung frame 5000", next);
        Assert.DoesNotContain("hung frames since", next);
    }

    [Fact]
    public void ACommandInTheHungFramesTick_IsNamedWithItsRunCount()
    {
        _world.Post(() => { });
        _world.RunTick(50);
        _clock.AdvanceMs(3_000);
        _world.RunTick(50);
        _monitor.Check(_clock.NowMicros);

        string line = Assert.Single(HangLines());
        Assert.Contains("hung frame 3000 ms", line);
        Assert.Contains("(1 run)", line);
        Assert.Equal("OutsideTick", _feature.LastHangFacts.Phase); // the body is microseconds of a 3 s frame
    }

    [Fact]
    public void AFrameWithNoTickBody_SaysTheTimeWasOutsideTickCode()
    {
        _world.RunTick(50);
        _clock.AdvanceMs(4_000); // nothing ran: a pause outside the tick
        _world.RunTick(50);
        _monitor.Check(_clock.NowMicros);

        string line = Assert.Single(HangLines());
        Assert.Contains("hung frame 4000 ms", line);
        Assert.Contains("outside tick code", line);
        Assert.Equal(("OutsideTick", "none"), (_feature.LastHangFacts.Phase, _feature.LastHangFacts.Item));
    }

    private QaSignal[] Signals()
    {
        var list = new List<QaSignal>();
        _bus.Drain(list, 16);
        return [.. list];
    }

    [Fact]
    public void AFrameWithNoTickBody_ReachesBotQaAsAnOutsideTickFingerprint()
    {
        _world.RunTick(50);
        _clock.AdvanceMs(4_000);
        _world.RunTick(50);
        _monitor.Check(_clock.NowMicros);

        QaSignal signal = Assert.Single(Signals());
        Assert.Equal(QaSignalKind.TickHang, signal.Kind);
        Assert.Equal("OutsideTick", signal.Stage);
        Assert.Equal("none", signal.Reason);
        Assert.StartsWith("v1|tickhang|phase=OutsideTick|item=none", PlayerbotQaFingerprint.Key(signal), StringComparison.Ordinal);
    }

    [Fact]
    public void AHangAnEarlierReportAlreadyDescribed_SaysSo_AndSendsBotQaNothing()
    {
        _world.RunTick(50);
        _clock.AdvanceMs(4_000);
        _world.RunTick(50);

        CompletedHangText first = _monitor.CompletedHangDetail!(); // the record is consumed here, as a racing report would
        Assert.Equal(1, first.Hangs);
        _monitor.Check(_clock.NowMicros);

        string line = Assert.Single(HangLines());
        Assert.Contains("hung frame described in the previous report", line);
        Assert.DoesNotContain("no tick body", line);
        Assert.Empty(Signals());
    }
}
