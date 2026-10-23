using ArcaneCore.Game.Maps;
using ArcaneCore.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Game.Tests.Maps;

/// <summary>The tick-start seam the tick watchdog closes its frames on: raised before the commands, with the previous tick's facts readable.</summary>
public sealed class WorldRuntimeTickStartingTests
{
    private sealed class NamedFeature
    {
        public void OnUpdate(uint diffMs)
        {
        }
    }

    private static WorldRuntime NewWorld() => new(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, new NullSaveQueue(), NullLogger<WorldRuntime>.Instance);

    [Fact]
    public void TickStarting_SeesThePreviousTicksPhasesDurationAndFeatures_BeforeItsOwnCommandsRun()
    {
        using WorldRuntime world = NewWorld();
        var feature = new NamedFeature();
        world.Updated += feature.OnUpdate;
        var seen = new List<(int CommandsRun, long Duration, int TopCount, string? FirstTop, int RanAtStart)>();
        int ran = 0;
        world.TickStarting += _ =>
        {
            Span<string?> names = new string?[5];
            Span<long> micros = new long[5];
            int count = world.CopyLastTickTopFeatures(names, micros);
            seen.Add((world.LastTickPhases.CommandsRun, world.LastTickDurationMicros, count, count > 0 ? names[0] : null, ran));
        };

        world.Post(() => ran++);
        world.RunTick(50); // tick 1: one command and the feature
        world.RunTick(50); // tick 2

        Assert.Equal(2, seen.Count);
        Assert.Equal((0, 0L, 0, null, 0), seen[0]); // nothing before the first tick, and its command had not run yet
        Assert.Equal(1, seen[1].CommandsRun);
        Assert.True(seen[1].Duration > 0);
        Assert.True(seen[1].TopCount >= 1);
        Assert.Contains("NamedFeature", seen[1].FirstTop);
        Assert.Equal(1, seen[1].RanAtStart);
        Assert.True(world.LastTickDurationMicros > 0);
    }

    [Fact]
    public void CopyLastTickTopFeatures_IsBoundedByTheBuffers()
    {
        using WorldRuntime world = NewWorld();
        var feature = new NamedFeature();
        world.Updated += feature.OnUpdate;
        world.RunTick(50);

        Span<string?> none = [];
        Span<long> micros = new long[5];
        Assert.Equal(0, world.CopyLastTickTopFeatures(none, micros));
        Span<string?> one = new string?[1];
        Span<long> oneMicros = new long[3];
        Assert.Equal(1, world.CopyLastTickTopFeatures(one, oneMicros));
    }

    [Fact]
    public void AFailingTickStartingHandler_DoesNotStopTheTick()
    {
        using WorldRuntime world = NewWorld();
        world.TickStarting += _ => throw new InvalidOperationException("handler failed");
        bool commandRan = false;
        world.Post(() => commandRan = true);

        world.RunTick(50);

        Assert.True(commandRan);
    }
}
