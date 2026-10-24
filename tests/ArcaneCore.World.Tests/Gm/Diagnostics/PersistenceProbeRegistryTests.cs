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
        Assert.Equal("boom: 0 pending, HALTED: InvalidOperationException: probe broke", snapshot[0].Format());
        Assert.Equal("fine: 4 pending", snapshot[1].Format());
    }

    [Fact]
    public void Format_NamesRetainedKeys_AHaltedQueue_AndTheLastFailureOnASecondLine()
    {
        var failure = new IOException("disk full");

        var healthy = new PersistenceQueueSnapshot("a", 0, [], null, null);
        var retained = new PersistenceQueueSnapshot("b", 2, ["mute:2"], null, failure);
        var halted = new PersistenceQueueSnapshot("c", 1, [], failure, failure);

        Assert.Equal("a: 0 pending", healthy.Format());
        Assert.True(healthy.IsHealthy);
        Assert.Null(healthy.FormatFailure());
        Assert.Equal("b: 2 pending, RETAINED after failed writes: mute:2", retained.Format());
        Assert.Equal("  b last failure: IOException: disk full", retained.FormatFailure());
        Assert.False(retained.IsHealthy);
        Assert.Equal("c: 1 pending, HALTED: IOException: disk full", halted.Format());
        Assert.Null(halted.FormatFailure());
    }

    [Fact]
    public void LastFailure_FallsBackToTheTerminalFailure()
    {
        var terminal = new InvalidOperationException("ownership lost");
        var probe = new DelegatePersistenceQueueProbe("q", () => 0, terminalFailure: () => terminal);

        Assert.Same(terminal, probe.LastFailure);
        Assert.Empty(probe.RetainedKeys);
    }

    [Fact]
    public async Task ArcaneQueues_ListsEveryRegisteredQueue_NotJustTheOriginalSeven()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("STAFF", "Staffer", AccountSecurity.Administrator);
        await admin.CollectAsync();
        string[] expected = [.. WorldPersistenceProbes.CreateRegistry(host.WorldServices).Snapshot().Select(s => s.Format())];

        string[] lines = await LinesAsync(admin, ".arcane queues", expected.Length);

        Assert.Equal(expected, lines);
        Assert.Contains(lines, l => l.StartsWith("character saves: ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("gm audit: ", StringComparison.Ordinal));
        Assert.True(expected.Length > 7, $"expected more than the original 7 queues, got {expected.Length}: {string.Join(" | ", expected)}");
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

        Assert.Equal(15, registry.Count);
        Assert.Empty(registry.Snapshot()); // nothing is loaded in an empty container: every queue is skipped, none throws
    }
}
