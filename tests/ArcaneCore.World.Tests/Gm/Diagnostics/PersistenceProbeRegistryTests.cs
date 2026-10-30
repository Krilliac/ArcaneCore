using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Gm.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Gm.Audit.TicketTests;

namespace ArcaneCore.World.Tests.Gm.Diagnostics;

public sealed class PersistenceProbeRegistryTests
{
    private static DelegatePersistenceQueueProbe Probe(string name, int pending = 0, string[]? retained = null, Exception? terminal = null, Exception? last = null)
        => new(name, () => pending, () => retained ?? [], () => terminal, () => last);

    [Fact]
    public void Snapshot_ListsRegisteredQueues_InRegistrationOrder()
    {
        var registry = new PersistenceProbeRegistry();
        registry.Register(Probe("b", 2));
        registry.Register(Probe("a", 1, ["k1", "k2"]));

        IReadOnlyList<PersistenceQueueSnapshot> snapshot = registry.Snapshot();

        Assert.Equal(["b", "a"], snapshot.Select(s => s.Name));
        Assert.Equal(2, snapshot[0].Pending);
        Assert.Equal(["k1", "k2"], snapshot[1].RetainedKeys);
    }

    [Fact]
    public void Register_WithTheSameName_ReplacesTheEarlierEntry()
    {
        var registry = new PersistenceProbeRegistry();
        registry.Register(Probe("q", 1));
        registry.Register(Probe("Q", 7));

        Assert.Equal(1, registry.Count);
        Assert.Equal(7, registry.Snapshot().Single().Pending);
    }

    [Fact]
    public void AResolverThatReturnsNull_IsSkipped_UntilItsQueueExists()
    {
        var registry = new PersistenceProbeRegistry();
        IPersistenceQueueProbe? queue = null;
        registry.Register("late", () => queue);

        Assert.Empty(registry.Snapshot());

        queue = Probe("late", 3);
        Assert.Equal(3, registry.Snapshot().Single().Pending);
    }

    [Fact]
    public void AProbeThatThrows_IsReportedAsHalted_WithoutHidingTheOthers()
    {
        var registry = new PersistenceProbeRegistry();
        registry.Register("boom", () => throw new InvalidOperationException("probe broke"));
        registry.Register(Probe("fine", 4));

        IReadOnlyList<PersistenceQueueSnapshot> snapshot = registry.Snapshot();

        Assert.Equal(2, snapshot.Count);
        Assert.Equal("boom: 0 pending, retained n/a, HALTED: InvalidOperationException: probe broke", snapshot[0].Format());
        Assert.Equal("fine: 4 pending", snapshot[1].Format());
    }

    [Fact]
    public void Format_NamesRetainedKeys_AHaltedQueue_AndTheRetainedFailureOnASecondLine()
    {
        var failure = new IOException("disk full");

        var healthy = new PersistenceQueueSnapshot("a", 0, [], null, null);
        var retained = new PersistenceQueueSnapshot("b", 2, ["mute:2"], null, failure);
        var halted = new PersistenceQueueSnapshot("c", 1, [], failure, failure);

        Assert.Equal("a: 0 pending", healthy.Format());
        Assert.True(healthy.IsHealthy);
        Assert.Null(healthy.FormatFailure());
        Assert.Equal("b: 2 pending, RETAINED after failed writes: mute:2", retained.Format());
        Assert.Equal("  b retained failure: IOException: disk full", retained.FormatFailure());
        Assert.False(retained.IsHealthy);
        Assert.Equal("c: 1 pending, HALTED: IOException: disk full", halted.Format());
        Assert.Null(halted.FormatFailure());
    }

    [Fact]
    public void Register_RejectsABlankName_ANullResolver_AndANullProbe()
    {
        var registry = new PersistenceProbeRegistry();

        Assert.Throws<ArgumentException>(() => registry.Register(" ", () => null));
        Assert.Throws<ArgumentNullException>(() => registry.Register("q", null!));
        Assert.Throws<ArgumentNullException>(() => registry.Register((IPersistenceQueueProbe)null!));
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void ReRegistering_KeepsTheEntrysPlaceInTheOrder()
    {
        var registry = new PersistenceProbeRegistry();
        registry.Register(Probe("a", 1));
        registry.Register(Probe("b", 2));

        registry.Register(Probe("A", 9));

        Assert.Equal(["A", "b"], registry.Snapshot().Select(s => s.Name));
        Assert.Equal(9, registry.Snapshot()[0].Pending);
    }

    [Fact]
    public void TheResolver_RunsOnEverySnapshot_SoAQueueThatGoesAwayDisappears()
    {
        var registry = new PersistenceProbeRegistry();
        IPersistenceQueueProbe? queue = Probe("q", 1);
        int calls = 0;
        registry.Register("q", () =>
        {
            calls++;
            return queue;
        });

        Assert.Single(registry.Snapshot());
        queue = null;
        Assert.Empty(registry.Snapshot());
        Assert.Equal(2, calls);
    }

    [Fact]
    public void ASnapshot_IsAPointInTimeCopy()
    {
        int pending = 3;
        var registry = new PersistenceProbeRegistry();
        registry.Register(new DelegatePersistenceQueueProbe("q", () => pending));

        PersistenceQueueSnapshot snapshot = registry.Snapshot().Single();
        pending = 40;

        Assert.Equal(3, snapshot.Pending);
        Assert.Equal(40, registry.Snapshot().Single().Pending);
    }

    [Fact]
    public void AProbeWhoseCounterThrows_IsReportedUnderTheRegisteredName_AsHalted()
    {
        var registry = new PersistenceProbeRegistry();
        registry.Register("flaky", () => new DelegatePersistenceQueueProbe("flaky", () => throw new InvalidOperationException("counter broke")));

        PersistenceQueueSnapshot snapshot = registry.Snapshot().Single();

        Assert.Equal("flaky: 0 pending, retained n/a, HALTED: InvalidOperationException: counter broke", snapshot.Format());
        Assert.False(snapshot.IsHealthy);
    }

    [Fact]
    public void Format_JoinsSeveralRetainedKeys_AndAHaltedQueueSkipsTheSeparateFailureLine()
    {
        var failure = new IOException("disk full");

        var retained = new PersistenceQueueSnapshot("q", 0, ["a", "b", "c"], null, failure);
        var both = new PersistenceQueueSnapshot("q", 0, ["a"], failure, failure);

        Assert.Equal("q: 0 pending, RETAINED after failed writes: a, b, c", retained.Format());
        Assert.NotNull(retained.FormatFailure());
        Assert.Null(both.FormatFailure());
        Assert.Contains("RETAINED", both.Format(), StringComparison.Ordinal);
        Assert.Contains("HALTED", both.Format(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnExplicitRetainedFailure_WinsOverTheTerminalFailure()
    {
        var terminal = new InvalidOperationException("halted");
        var last = new IOException("retained write");
        var probe = new DelegatePersistenceQueueProbe("q", () => 0, terminalFailure: () => terminal, retainedFailure: () => last);

        Assert.Same(last, probe.RetainedFailure);
        Assert.Same(terminal, probe.TerminalFailure);
        Assert.Null(new DelegatePersistenceQueueProbe("q", () => 0).RetainedFailure);
    }

    [Fact]
    public void AFieldAQueueCannotReport_PrintsNa_AndIsNeverHealthy()
    {
        var pendingOnly = new DelegatePersistenceQueueProbe("rest", () => 3);
        var retainedOnly = new DelegatePersistenceQueueProbe("keyed", () => 0, () => []);
        var terminalOnly = new DelegatePersistenceQueueProbe("coord", () => 0, terminalFailure: () => null);
        var full = new DelegatePersistenceQueueProbe("full", () => 0, () => [], () => null);
        var registry = new PersistenceProbeRegistry();
        registry.Register(pendingOnly);
        registry.Register(retainedOnly);
        registry.Register(terminalOnly);
        registry.Register(full);

        IReadOnlyList<PersistenceQueueSnapshot> snapshot = registry.Snapshot();

        Assert.Null(pendingOnly.RetainedKeys);
        Assert.False(pendingOnly.ReportsTerminalFailure);
        Assert.Equal("rest: 3 pending, retained n/a, halted n/a", snapshot[0].Format());
        Assert.Equal("keyed: 0 pending, halted n/a", snapshot[1].Format());
        Assert.Equal("coord: 0 pending, retained n/a", snapshot[2].Format());
        Assert.Equal("full: 0 pending", snapshot[3].Format());
        Assert.Equal([false, false, false, true], snapshot.Select(s => s.IsHealthy));
    }

    [Fact]
    public void TheFailureText_KeepsTheExceptionTypeButTruncatesALongMessage()
    {
        var failure = new IOException(new string('a', 120) + new string('b', 200));
        var halted = new PersistenceQueueSnapshot("q", 0, [], failure, failure);

        string line = halted.Format();

        Assert.Contains("IOException: ", line, StringComparison.Ordinal);
        Assert.EndsWith(new string('a', 120) + "...", line, StringComparison.Ordinal);
        Assert.DoesNotContain("bbbb", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentRegisterAndSnapshot_NeverThrows()
    {
        var registry = new PersistenceProbeRegistry();

        await Task.WhenAll(Enumerable.Range(0, 4).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < 500; i++)
            {
                registry.Register(Probe("q" + ((t * 7) + (i % 7)), i));
                _ = registry.Snapshot();
            }
        })));

        Assert.Equal(registry.Count, registry.Snapshot().Count);
    }

    [Fact]
    public void RetainedFailure_FallsBackToTheTerminalFailure()
    {
        var terminal = new InvalidOperationException("ownership lost");
        var probe = new DelegatePersistenceQueueProbe("q", () => 0, terminalFailure: () => terminal);

        Assert.Same(terminal, probe.RetainedFailure);
        Assert.Null(probe.RetainedKeys);
    }

    [Fact]
    public async Task ArcaneQueues_ListsEveryRegisteredQueue_NotJustTheOriginalSeven()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("STAFF", "Staffer", AccountSecurity.Administrator);
        await admin.CollectAsync();
        string[] names = [.. WorldPersistenceProbes.CreateRegistry(host.WorldServices).Snapshot().Select(s => s.Name)];

        string[] lines = await LinesAsync(admin, ".arcane queues", names.Length);

        Assert.Equal(names, lines.Select(l => l[..l.IndexOf(':', StringComparison.Ordinal)]));
        Assert.Contains(lines, l => l.StartsWith("character saves: ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("gm audit: ", StringComparison.Ordinal));
    }

    [Fact]
    public void TheWorldDaemon_RegistersTheRegistry()
    {
        var services = new ServiceCollection().AddLogging();

        services.AddWorldDaemon(new ConfigurationBuilder().Build());

        Assert.Contains(services, d => d.ServiceType == typeof(PersistenceProbeRegistry) && d.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public void BuiltInRegistration_NamesEveryPersistenceQueue()
    {
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();

        PersistenceProbeRegistry registry = WorldPersistenceProbes.CreateRegistry(services);

        Assert.Equal(20, registry.Count);
        Assert.Empty(registry.Snapshot()); // nothing is loaded in an empty container: every queue is skipped, none throws
    }
}
