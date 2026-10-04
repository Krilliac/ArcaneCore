using System.Net;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Net;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.Realm.Tests.Security;

/// <summary>
/// The guard composes the connection caps, the per-address budgets and the rate-limited log
/// lines; a refusal is a null lease or a false and at most one line per interval.
/// </summary>
public sealed class NetGuardTests
{
    private static readonly IPAddress Peer = IPAddress.Parse("198.51.100.10");

    private static (NetGuard Guard, CapturingLogger Log, long[] Clock) Build(NetProtectionOptions options, int daemonPerIp = 0, int daemonTotal = 0)
    {
        var log = new CapturingLogger();
        long[] clock = [0];
        var guard = new NetGuard(options, () => daemonTotal, () => daemonPerIp, log, () => Volatile.Read(ref clock[0]));
        return (guard, log, clock);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(0, 16, 16)]
    [InlineData(2, 0, 2)]
    [InlineData(2, 16, 2)]
    [InlineData(32, 16, 16)]
    public void EffectivePerIpCap_TakesTheLowerOfTheTwoSetCaps(int daemon, int shared, int expected)
        => Assert.Equal(expected, NetGuard.EffectivePerIpCap(daemon, shared));

    [Fact]
    public void SharedPerIpCap_RefusesTheNextConnection_WithOneLogLinePerInterval()
    {
        (NetGuard guard, CapturingLogger log, long[] clock) = Build(new NetProtectionOptions
        {
            MaxConnectionsPerIp = 2, ConnectionBurstPerIp = 0, LogInterval = TimeSpan.FromSeconds(10),
        });

        IDisposable? a = guard.TryAdmit(Peer);
        IDisposable? b = guard.TryAdmit(Peer);
        Assert.NotNull(a);
        Assert.NotNull(b);
        for (int i = 0; i < 5; i++)
        {
            Assert.Null(guard.TryAdmit(Peer));
        }

        Assert.Equal(5, guard.RefusedConnections);
        string line = Assert.Single(log.Messages);
        Assert.Contains("connection cap reached", line, StringComparison.Ordinal);
        Assert.Contains("(0 more refusal(s)", line, StringComparison.Ordinal);

        clock[0] += 10_000;
        Assert.Null(guard.TryAdmit(Peer));
        Assert.Equal(2, log.Messages.Count);
        Assert.Contains("(4 more refusal(s)", log.Messages[1], StringComparison.Ordinal);

        a!.Dispose();
        Assert.NotNull(guard.TryAdmit(Peer));
        Assert.NotNull(guard.TryAdmit(IPAddress.Parse("198.51.100.11"))); // another address is not affected
    }

    [Fact]
    public void DaemonCap_StillApplies_WhenLower()
    {
        (NetGuard guard, _, _) = Build(new NetProtectionOptions { MaxConnectionsPerIp = 16, ConnectionBurstPerIp = 0 }, daemonPerIp: 1);
        Assert.NotNull(guard.TryAdmit(Peer));
        Assert.Null(guard.TryAdmit(Peer));
    }

    [Fact]
    public void ConnectionRate_RefusesBeyondTheBurst_ReleasesTheCapSlot_AndRefills()
    {
        (NetGuard guard, CapturingLogger log, long[] clock) = Build(new NetProtectionOptions
        {
            MaxConnectionsPerIp = 0, ConnectionBurstPerIp = 2, ConnectionsPerMinutePerIp = 60,
        });

        Assert.NotNull(guard.TryAdmit(Peer));
        Assert.NotNull(guard.TryAdmit(Peer));
        Assert.Null(guard.TryAdmit(Peer));
        Assert.Equal(2, guard.Limiter.Count); // the refused connection holds no cap slot
        Assert.Contains(log.Messages, m => m.Contains("connection rate exceeded", StringComparison.Ordinal));

        clock[0] += 1_000;
        Assert.NotNull(guard.TryAdmit(Peer));
    }

    [Fact]
    public void AuthFailures_AreChargedOnFailureOnly_AndRefuseBeforeTheNextAttempt()
    {
        (NetGuard guard, CapturingLogger log, long[] clock) = Build(new NetProtectionOptions
        {
            AuthFailureBurstPerIp = 2, AuthFailuresPerMinutePerIp = 6,
        });
        IpKey? key = IpKey.From(Peer);

        // Successes never touch the budget.
        for (int i = 0; i < 20; i++)
        {
            Assert.True(guard.AllowsAuthAttempt(key));
        }

        guard.RecordAuthFailure(key);
        Assert.True(guard.AllowsAuthAttempt(key));
        guard.RecordAuthFailure(key);
        Assert.False(guard.AllowsAuthAttempt(key));
        Assert.False(guard.AllowsAuthAttempt(key));
        Assert.Equal(2, guard.RefusedAuthAttempts);
        Assert.Single(log.Messages, m => m.Contains("too many failed attempts", StringComparison.Ordinal));

        clock[0] += 10_000; // one token back at 6 per minute
        Assert.True(guard.AllowsAuthAttempt(key));

        // An address-less connection is never limited (there is no key to count against).
        Assert.True(guard.AllowsAuthAttempt(null));
        guard.RecordAuthFailure(null);
    }

    /// <summary>
    /// A full table is not a reason to refuse anybody (the lockout lever of the first version): the
    /// newcomer is admitted on connect and on an attempt, the table forgets an address, and one
    /// rate-limited line tells the operator the budgets are measuring less.
    /// </summary>
    [Fact]
    public void FullTable_AdmitsTheNewcomer_LogsOnce_AndCountsForcedEvictions()
    {
        (NetGuard guard, CapturingLogger log, _) = Build(new NetProtectionOptions
        {
            MaxTrackedAddresses = 16, MaxConnectionsPerIp = 0, ConnectionBurstPerIp = 100, AuthFailureBurstPerIp = 10,
        });
        foreach (IPAddress address in TableFiller.FreeSlotAddresses(guard.Table.Capacity, "10.5"))
        {
            Assert.NotNull(guard.TryAdmit(address));
        }

        Assert.Equal(guard.Table.Capacity, guard.Table.Count);
        Assert.Equal(0, guard.ForcedEvictions);
        Assert.Empty(log.Messages);
        long refusedConnections = guard.RefusedConnections;
        long refusedAttempts = guard.RefusedAuthAttempts;

        // Every slot is busy and none is idle: a new address is admitted on connect and on an attempt.
        using IDisposable? lease = guard.TryAdmit(IPAddress.Parse("10.6.0.1"));
        Assert.NotNull(lease);
        Assert.Equal(1, guard.ForcedEvictions);
        Assert.True(guard.AllowsAuthAttempt(IpKey.From(IPAddress.Parse("10.6.0.2"))));
        Assert.Equal(2, guard.ForcedEvictions);
        Assert.Equal(refusedConnections, guard.RefusedConnections);
        Assert.Equal(refusedAttempts, guard.RefusedAuthAttempts);
        Assert.Equal(guard.Table.Capacity, guard.Table.Count);

        // One line for all of it (the clock never moved), and it says nothing was refused.
        string line = Assert.Single(log.Messages, m => m.Contains("per-address table is full", StringComparison.Ordinal));
        Assert.Contains("nothing was refused", line, StringComparison.Ordinal);
        Assert.DoesNotContain(log.Messages, m => m.Contains("refused:", StringComparison.Ordinal));
    }

    [Fact]
    public void RefusalPath_DoesNotAllocate()
    {
        (NetGuard guard, _, _) = Build(new NetProtectionOptions { MaxConnectionsPerIp = 1, ConnectionBurstPerIp = 0, LogInterval = TimeSpan.FromHours(1) });
        using IDisposable? held = guard.TryAdmit(Peer);
        Assert.Null(guard.TryAdmit(Peer)); // the one admitted log line
        IpKey? key = IpKey.From(Peer);

        // Warm up once (tiered JIT and first-call framework caches), then measure the steady state.
        for (int round = 0; round < 2; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10_000; i++)
            {
                guard.TryAdmit(Peer);
                guard.AllowsAuthAttempt(key);
                guard.RecordAuthFailure(key);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            if (round == 1)
            {
                Assert.Equal(0, allocated);
            }
        }
    }

    [Fact]
    public void FrameAndLifetimeReports_AreRateLimited()
    {
        (NetGuard guard, CapturingLogger log, long[] clock) = Build(new NetProtectionOptions { LogInterval = TimeSpan.FromSeconds(10) });
        for (int i = 0; i < 100; i++)
        {
            guard.ReportFrameTimeout("1.2.3.4:5");
            guard.ReportUnauthenticatedTimeout("1.2.3.4:5", TimeSpan.FromSeconds(30));
        }

        Assert.Equal(2, log.Messages.Count);
        clock[0] += 10_000;
        guard.ReportFrameTimeout("1.2.3.4:5");
        Assert.Contains("(99 more since the last line)", log.Messages[2], StringComparison.Ordinal);
    }

    internal sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Messages)
            {
                Messages.Add(formatter(state, exception));
            }
        }
    }
}
