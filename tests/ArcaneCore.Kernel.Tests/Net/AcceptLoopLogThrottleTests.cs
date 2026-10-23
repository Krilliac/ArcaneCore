using System.Net.Sockets;
using ArcaneCore.Kernel.Net;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Net;

/// <summary>
/// The accept-error log throttle must use the monotonic <c>Clock</c> (through <c>LogGate</c>) like every other Net protection,
/// not wall-clock <c>DateTime.UtcNow</c>, which a clock step (NTP correction, VM resume) can move backward or forward.
/// </summary>
public sealed class AcceptLoopLogThrottleTests
{
    // RunAsync is static with no clock parameter, so a wall-clock step cannot be injected; the regression is pinned on the source.
    [Fact]
    public void AcceptLoopSource_DoesNotThrottleOnWallClock_AndUsesTheSharedLogGate()
    {
        string source = File.ReadAllText(FindAcceptLoopSource());

        Assert.DoesNotContain("DateTime.UtcNow", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DateTimeOffset.UtcNow", source, StringComparison.Ordinal);
        Assert.Contains("LogGate", source, StringComparison.Ordinal);
    }

    // Count-semantics guard for the fix: the first warning is immediate and counts the error it reports, later
    // errors inside the interval are suppressed (LogGate's own suppressed value excludes the admitted call, so the fix logs suppressed + 1).
    [Fact]
    public async Task FirstAcceptError_IsLoggedImmediately_CountingItself_AndTheRestInTheIntervalAreSuppressed()
    {
        var log = new CapturingLogger();
        int calls = 0;
        var done = new TaskCompletionSource();
        using var stop = new CancellationTokenSource();

        ValueTask<TcpClient> Accept(CancellationToken ct)
        {
            if (Interlocked.Increment(ref calls) <= 3)
            {
                throw new SocketException((int)SocketError.TooManyOpenSockets);
            }

            done.TrySetResult();
            // Cancelled from stop.Cancel()'s own thread through the registration: no pool thread is needed to end the accept.
            var pending = new TaskCompletionSource<TcpClient>(); // inline continuation: the loop resumes on the Cancel() thread too
            ct.Register(() => pending.TrySetCanceled(ct));
            return new ValueTask<TcpClient>(pending.Task);
        }

        Task loop = AcceptLoop.RunAsync(Accept, _ => { }, log, stop.Token, backoff: TimeSpan.FromMilliseconds(1));
        // Generous budgets: a loaded CI runner can delay pool threads by seconds; a hang still fails, it just takes longer.
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        stop.Cancel();
        await loop.WaitAsync(TimeSpan.FromSeconds(30));

        string[] warnings = log.Warnings.ToArray();
        Assert.Single(warnings);
        Assert.Contains("accept failed (1 error(s) since the last report)", warnings[0], StringComparison.Ordinal);
    }

    private static string FindAcceptLoopSource()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "src", "ArcaneCore.Kernel", "Net", "AcceptLoop.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("src/ArcaneCore.Kernel/Net/AcceptLoop.cs not found above " + AppContext.BaseDirectory);
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly List<string> _warnings = [];

        public IReadOnlyList<string> Warnings
        {
            get
            {
                lock (_warnings)
                {
                    return [.. _warnings];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Warning)
            {
                return;
            }

            lock (_warnings)
            {
                _warnings.Add(formatter(state, exception));
            }
        }
    }
}
