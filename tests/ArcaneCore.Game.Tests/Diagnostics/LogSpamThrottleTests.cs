using System.Reflection;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Logging;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.Game.Tests.Diagnostics;

/// <summary>
/// Per-packet and per-tick log lines must not scale with the event rate: a repeated fault or invariant heal writes its first
/// line(s) at the usual level, and the repeats are throttled and counted (docs/ops/exception-audit.md).
/// </summary>
public sealed class LogSpamThrottleTests
{
    private const int Repeats = 50;

    private sealed class CapturingLogger : ILogger<WorldRuntime>
    {
        private readonly Lock _gate = new();
        public List<(LogLevel Level, string Text)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
            {
                Lines.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    private sealed class ThrowingBeforeObserver : IClientMovementObserver
    {
        public void BeforeApply(MovementObserverContext context, in MovementInfo previous, ref MovementInfo incoming)
            => throw new InvalidOperationException("log spam test: before observer");
    }

    private sealed class ThrowingAfterObserver : IClientMovementObserver
    {
        public void AfterApply(MovementObserverContext context, in MovementInfo previous)
            => throw new InvalidOperationException("log spam test: after observer");
    }

    private static (WorldRuntime World, Player Player, CapturingLogger Logger) Scene()
    {
        var logger = new CapturingLogger();
        var world = new WorldRuntime(
            new WorldRuntimeOptions { UpdateCompressionThreshold = 0, AutosaveIntervalMs = 0, CommandTimeBudgetMs = 0 },
            new RecordingSaveQueue(),
            logger);
        Player player = TestWorld.CreatePlayer(1, 100, 100, new FakeSession(1));
        world.AddPlayer(player);
        logger.Lines.Clear();
        return (world, player, logger);
    }

    [Fact]
    public void MovementObserverFault_RepeatedEveryPacket_IsThrottledByTheFaultLedger()
    {
        (WorldRuntime world, Player player, CapturingLogger logger) = Scene();
        IClientMovementObserver[] observers = [new ThrowingBeforeObserver(), new ThrowingAfterObserver()];
        var context = new MovementObserverContext(player, world, WorldOpcode.MsgMoveHeartbeat);
        MovementInfo previous = player.Movement;
        for (int i = 0; i < Repeats; i++)
        {
            MovementInfo incoming = previous;
            MovementObservers.Before(observers, context, in previous, ref incoming, logger);
            MovementObservers.After(observers, context, in previous, logger);
        }

        List<string> before = logger.Lines.Where(l => l.Text.Contains("failed before applying", StringComparison.Ordinal)).Select(l => l.Text).ToList();
        List<string> after = logger.Lines.Where(l => l.Text.Contains("failed after applying", StringComparison.Ordinal)).Select(l => l.Text).ToList();

        // The first lines keep their level and carry the ledger's count; the repeats inside the window are held back.
        Assert.InRange(before.Count, 1, Repeats / 2);
        Assert.InRange(after.Count, 1, Repeats / 2);
        Assert.All(before.Concat(after), text => Assert.Contains("count=", text, StringComparison.Ordinal));
        Assert.All(logger.Lines, line => Assert.Equal(LogLevel.Error, line.Level));
    }

    [Fact]
    public void OrphanVisibleHeal_RepeatedEveryTick_LogsOneLinePerWindow_AndCountsTheRest()
    {
        (WorldRuntime world, Player player, CapturingLogger logger) = Scene();
        Map map = world.GetMap(0);
        for (int i = 0; i < Repeats; i++)
        {
            player.VisibleObjects.Add(ObjectGuid.WithEntry(HighGuid.Unit, 9999, (uint)(900_000 + i)));
            Assert.True(map.VerifyVisibleSetInvariants(player));
        }

        List<(LogLevel Level, string Text)> heals = logger.Lines.Where(l => l.Text.Contains("visibility invariant healed", StringComparison.Ordinal)).ToList();
        (LogLevel level, _) = Assert.Single(heals);
        Assert.Equal(LogLevel.Warning, level);

        FieldInfo? field = typeof(Map).GetField("_orphanLogGate", BindingFlags.NonPublic | BindingFlags.Instance);
        var gate = Assert.IsType<LogGate>(field?.GetValue(map));
        Assert.Equal(Repeats - 1, gate.Suppressed);
        Assert.Equal(Repeats, map.OrphanVisibleHeals);
    }

    [Fact]
    public void OrphanVisibleHeal_TwoHealsInOneWindow_AreBothCounted_ThoughOnlyOneLineIsLogged()
    {
        (WorldRuntime world, Player player, CapturingLogger logger) = Scene();
        Map map = world.GetMap(0);
        long before = map.OrphanVisibleHeals;
        for (uint i = 0; i < 2; i++)
        {
            player.VisibleObjects.Add(ObjectGuid.WithEntry(HighGuid.Unit, 9999, 800_000 + i));
            Assert.True(map.VerifyVisibleSetInvariants(player));
        }

        Assert.Equal(2, map.OrphanVisibleHeals - before);
        Assert.Single(logger.Lines, l => l.Text.Contains("visibility invariant healed", StringComparison.Ordinal));
    }
}