using ArcaneCore.Game.Maps.Collision;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// The process-wide path-job pool is started by whichever subsystem asks first (creatures or playerbots) with its own thread count; a
/// later starter that asks for another count gets the running pool and a warning, once per asked count.
/// </summary>
public sealed class NavJobSystemSharedPoolTests
{
    private sealed class WarningLog : ILogger
    {
        private readonly object _gate = new();
        private readonly List<string> _warnings = [];

        public int Warnings
        {
            get
            {
                lock (_gate) return _warnings.Count;
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Warning) return;
            lock (_gate) _warnings.Add(formatter(state, exception));
        }
    }

    [Fact]
    public void ASecondStarterWithAnotherThreadCount_IsWarnedOnce_AndGetsTheRunningPool()
    {
        NavJobSystem running = NavJobSystem.Shared(4);
        int have = running.Threads;
        int asked = have == 64 ? 63 : have + 1;
        var log = new WarningLog();

        Assert.Same(running, NavJobSystem.Shared(asked, log));
        Assert.Same(running, NavJobSystem.Shared(asked, log));
        Assert.Equal(1, log.Warnings);

        Assert.Same(running, NavJobSystem.Shared(have, log)); // the same count: nothing to say
        Assert.Equal(1, log.Warnings);
    }
}
