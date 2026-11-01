using ArcaneCore.World.Playerbots.Qa;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>
/// A starved host gives each bot few simulated ticks in the world time a stall is judged by, so a bot that merely got little done reads as stalled. While the tick monitor
/// counts overruns or hangs (<see cref="PlayerbotQaOptions.StallStarvationSeconds"/>), bot QA files no stall or set-aside; a healthy host's stalls are filed as before.
/// </summary>
public sealed class PlayerbotQaStallTests : IDisposable
{
    private readonly string _directory = QaData.NewTempDirectory();
    private long _overruns;
    private long _hangs;

    public void Dispose() => QaData.DeleteQuietly(_directory);

    private (PlayerbotQaFeature Feature, PlayerbotQaSignalBus Bus, ManualTime Time, ServiceProvider Services) Start(int starvationSeconds = 120, bool withHealth = true)
    {
        var options = new PlayerbotQaOptions { Directory = _directory, StallStarvationSeconds = starvationSeconds };
        var bus = new PlayerbotQaSignalBus(64);
        ServiceProvider services = new ServiceCollection()
            .AddSingleton<IOptions<PlayerbotQaOptions>>(Options.Create(options))
            .AddSingleton(bus)
            .BuildServiceProvider();
        var time = new ManualTime(QaData.T0);
        var feature = new PlayerbotQaFeature(services)
        {
            Time = time,
            TickHealthSource = withHealth ? () => (Interlocked.Read(ref _overruns), Interlocked.Read(ref _hangs)) : null,
        };
        feature.Attach(null!);
        // The first fold only takes the monitor's baseline.
        Assert.True(bus.TryWrite(QaData.Relay(7)));
        feature.Drain();
        Assert.Equal(1, feature.Aggregator!.Count);
        return (feature, bus, time, services);
    }

    private static int StallFindings(PlayerbotQaFeature feature) => feature.Aggregator!.Snapshot().Count(f => f.Kind == QaSignalKind.Stall);

    [Fact]
    public async Task AStarvedHostWithSlowTicks_FilesNoStall()
    {
        (PlayerbotQaFeature feature, PlayerbotQaSignalBus bus, ManualTime time, ServiceProvider services) = Start();
        using (services)
        {
            Interlocked.Add(ref _overruns, 40); // the tick monitor counted slow frames: the bots' simulated ticks were few
            Assert.True(bus.TryWrite(QaData.Stall(QaData.Bot1, -5608.8f, -439.4f)));
            Assert.True(bus.TryWrite(QaData.Stall(QaData.Bot2, -5608.8f, -439.4f)));

            feature.Drain();

            Assert.Equal(0, StallFindings(feature));
            Assert.Equal(2, feature.DiscountedStalls);

            // Still inside the window although no further overrun was counted: the bot's stall is reported with the lag of its own idle time.
            time.Advance(TimeSpan.FromSeconds(60));
            Assert.True(bus.TryWrite(QaData.Stall(QaData.Bot3, -5608.8f, -439.4f)));
            feature.Drain();
            Assert.Equal(0, StallFindings(feature));
            await feature.StopAsync();
        }
    }

    [Fact]
    public async Task OneIsolatedOverrun_DoesNotSuppressStalls()
    {
        (PlayerbotQaFeature feature, PlayerbotQaSignalBus bus, ManualTime time, ServiceProvider services) = Start();
        using (services)
        {
            Interlocked.Increment(ref _overruns); // one frame over budget: routine on a loaded host
            Assert.True(bus.TryWrite(QaData.Stall(QaData.Bot1, -5608.8f, -439.4f)));
            feature.Drain();
            Assert.Equal(1, StallFindings(feature));

            // One more every look, far below the threshold within the window: still not starved.
            for (int i = 0; i < PlayerbotQaFeature.StarvationOverruns - 3; i++)
            {
                time.Advance(TimeSpan.FromSeconds(1));
                Interlocked.Increment(ref _overruns);
                Assert.True(bus.TryWrite(QaData.Relay((uint)(20 + i))));
                feature.Drain();
            }

            Assert.True(bus.TryWrite(QaData.Stall(QaData.Bot2, -5608.8f, -439.4f)));
            feature.Drain();
            Assert.Equal(0, feature.DiscountedStalls);
            Assert.Equal(1, StallFindings(feature));

            // Enough of them within the window is starvation.
            Interlocked.Add(ref _overruns, PlayerbotQaFeature.StarvationOverruns);
            Assert.True(bus.TryWrite(QaData.Stall(QaData.Bot3, -5608.8f, -439.4f)));
            feature.Drain();
            Assert.Equal(1, feature.DiscountedStalls);
            await feature.StopAsync();
        }
    }

    [Fact]
    public async Task RoutineOverrunsSpreadOverAQuietSpell_DoNotSuppressTheFirstStallAfterIt()
    {
        (PlayerbotQaFeature feature, PlayerbotQaSignalBus bus, ManualTime time, ServiceProvider services) = Start();
        using (services)
        {
            // Twice the threshold, one overrun a minute for far longer than the 120 s window, and no signal at all: each empty look stamps its overrun with its own time.
            for (int i = 0; i < PlayerbotQaFeature.StarvationOverruns * 2; i++)
            {
                time.Advance(TimeSpan.FromSeconds(60));
                Interlocked.Increment(ref _overruns);
                feature.Drain();
            }

            Assert.True(bus.TryWrite(QaData.Stall(QaData.Bot1, -5608.8f, -439.4f, at: time.GetUtcNow().UtcDateTime)));
            feature.Drain();

            Assert.Equal(0, feature.DiscountedStalls);
            Assert.Equal(1, StallFindings(feature));
            await feature.StopAsync();
        }
    }

    [Fact]
    public async Task AHangCountsAsStarvationToo()
    {
        (PlayerbotQaFeature feature, PlayerbotQaSignalBus bus, _, ServiceProvider services) = Start();
        using (services)
        {
            Interlocked.Increment(ref _hangs);
            Assert.True(bus.TryWrite(QaData.Stall(QaData.Bot1, -5608.8f, -439.4f)));

            feature.Drain();

            Assert.Equal(0, StallFindings(feature));
            await feature.StopAsync();
        }
    }

    [Fact]
    public async Task AHealthyHost_StillFilesItsStalls()
    {
        (PlayerbotQaFeature feature, PlayerbotQaSignalBus bus, _, ServiceProvider services) = Start();
        using (services)
        {
            Assert.True(bus.TryWrite(QaData.Stall(QaData.Bot1, -5608.8f, -439.4f)));

            feature.Drain();

            Assert.Equal(1, StallFindings(feature));
            Assert.Equal(0, feature.DiscountedStalls);
            await feature.StopAsync();
        }
    }

    [Fact]
    public async Task OnceTheWindowHasPassed_StallsAreFiledAgain()
    {
        (PlayerbotQaFeature feature, PlayerbotQaSignalBus bus, ManualTime time, ServiceProvider services) = Start();
        using (services)
        {
            Interlocked.Add(ref _overruns, 40);
            Assert.True(bus.TryWrite(QaData.Stall(QaData.Bot1, -5608.8f, -439.4f)));
            feature.Drain();
            Assert.Equal(0, StallFindings(feature));

            time.Advance(TimeSpan.FromSeconds(121));
            Assert.True(bus.TryWrite(QaData.Stall(QaData.Bot2, -5608.8f, -439.4f, at: QaData.T0.AddSeconds(121))));
            feature.Drain();

            Assert.Equal(1, StallFindings(feature));
            await feature.StopAsync();
        }
    }

    [Fact]
    public async Task AFindingOfAnotherKind_IsFiledWhileTheHostIsStarved()
    {
        (PlayerbotQaFeature feature, PlayerbotQaSignalBus bus, _, ServiceProvider services) = Start();
        using (services)
        {
            Interlocked.Add(ref _overruns, 40);
            Assert.True(bus.TryWrite(QaData.Relay(8)));
            Assert.True(bus.TryWrite(QaData.Stall(QaData.Bot1, -5608.8f, -439.4f)));

            feature.Drain();

            Assert.Equal(1, feature.DiscountedStalls); // the host really was starved: the stall of the same batch was dropped
            Assert.Equal(0, StallFindings(feature));
            Assert.Equal(2, feature.Aggregator!.Count); // the relay finding was filed all the same
            await feature.StopAsync();
        }
    }

    [Fact]
    public async Task StarvationSecondsZero_FilesStallsWhateverTheMonitorCounts()
    {
        (PlayerbotQaFeature feature, PlayerbotQaSignalBus bus, _, ServiceProvider services) = Start(starvationSeconds: 0);
        using (services)
        {
            Interlocked.Add(ref _overruns, 40);
            Assert.True(bus.TryWrite(QaData.Stall(QaData.Bot1, -5608.8f, -439.4f)));

            feature.Drain();

            Assert.Equal(1, StallFindings(feature));
            await feature.StopAsync();
        }
    }

    [Fact]
    public async Task WithoutATickMonitor_StallsAreFiled()
    {
        (PlayerbotQaFeature feature, PlayerbotQaSignalBus bus, _, ServiceProvider services) = Start(withHealth: false);
        using (services)
        {
            Assert.True(bus.TryWrite(QaData.Stall(QaData.Bot1, -5608.8f, -439.4f)));

            feature.Drain();

            Assert.Equal(1, StallFindings(feature));
            await feature.StopAsync();
        }
    }

    [Fact]
    public void TheWindowIsValidated()
    {
        Assert.Contains(new PlayerbotQaOptions { StallStarvationSeconds = -1 }.Validate(), p => p.Contains("StallStarvationSeconds", StringComparison.Ordinal));
        Assert.Contains(new PlayerbotQaOptions { StallStarvationSeconds = 3601 }.Validate(), p => p.Contains("StallStarvationSeconds", StringComparison.Ordinal));
        Assert.DoesNotContain(new PlayerbotQaOptions { StallStarvationSeconds = 0 }.Validate(), p => p.Contains("StallStarvationSeconds", StringComparison.Ordinal));
    }
}
