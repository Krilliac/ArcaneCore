using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Safety;
using Xunit;

namespace ArcaneCore.Game.Tests;

/// <summary>The backstop reads the process-wide violation counter, so its tests run apart from every other test class.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ThreadAffinityCollection
{
    public const string Name = "ThreadAffinity";
}

/// <summary>The runtime half of [WorldThreadOnly]: a field or position change on an in-map object from another thread is recorded, not thrown.</summary>
[Collection(ThreadAffinityCollection.Name)]
public sealed class ThreadAffinityBackstopTests
{
    private const string FieldSite = "thread-affinity:WorldObject.MarkChanged";
    private const string MoveSite = "thread-affinity:Map.OnObjectMoved";

    /// <summary>The counter and the ledger are process-wide and the ledger records only the first <see cref="ThreadAffinity.RecordLimit"/> violations, so an earlier class that changed an in-map object off its world thread (for example the parallel map tests) could leave the budget spent or a same-site entry with other frames. Start every test from zero; this collection never overlaps another test.</summary>
    public ThreadAffinityBackstopTests()
    {
        ThreadAffinity.Reset();
        FaultLedger.Shared.Reset();
    }

    private static FaultSnapshot? Find(string site) => FaultLedger.Shared.Snapshot().FirstOrDefault(f => f.Site == site);

    [Fact]
    public async Task OffThreadFieldChange_IsCountedAndRecordedWithItsCallSite_ButOnThreadIsNot()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        Player player = TestWorld.CreatePlayer(7001, 0f, 0f, new FakeSession());
        world.GetMap(0).AddPlayer(player);
        world.Start();
        try
        {
            long before = ThreadAffinity.Violations;
            await world.InvokeAsync(() =>
            {
                player.SetUInt32(UpdateFields.UnitFieldHealth, 11);
                return 0;
            });
            Assert.Equal(before, ThreadAffinity.Violations);

            RecordOffThreadFieldChange(player); // the test thread is not the world thread

            Assert.Equal(before + 1, ThreadAffinity.Violations);
            FaultSnapshot recorded = Assert.IsType<FaultSnapshot>(Find(FieldSite));
            // The field helpers and MarkChanged are [StackTraceHidden]: the signature starts at the real mutator (here the test).
            Assert.StartsWith("ThreadAffinityBackstopTests.", recorded.Frames[0], StringComparison.Ordinal);
            Assert.DoesNotContain(recorded.Frames, f => f.StartsWith("WorldObject.", StringComparison.Ordinal));
            Assert.Contains(recorded.Frames, f => f.StartsWith("ThreadAffinityBackstopTests.", StringComparison.Ordinal));
            Assert.DoesNotContain(recorded.Frames, f => f.Contains("ThreadAffinity.Violated", StringComparison.Ordinal) || f.Contains("CheckWorldThread", StringComparison.Ordinal));
        }
        finally
        {
            world.Stop();
        }
    }

    [Fact]
    public async Task OffThreadPositionChange_IsRecordedToo()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        Player player = TestWorld.CreatePlayer(7002, 0f, 0f, new FakeSession());
        world.GetMap(0).AddPlayer(player);
        world.Start();
        try
        {
            await world.InvokeAsync(() => 0); // the world thread publishes its id as it starts: wait until it runs
            long before = ThreadAffinity.Violations;
            player.SetPosition(5f, 5f, 83.5f, 0f);

            Assert.True(ThreadAffinity.Violations > before);
            Assert.NotNull(Find(MoveSite));
        }
        finally
        {
            world.Stop();
        }
    }

    [Fact]
    public void ADisabledBackstop_CountsNothing()
    {
        bool was = ThreadAffinity.Enabled;
        long before = ThreadAffinity.Violations;
        try
        {
            ThreadAffinity.Enabled = false;
            ThreadAffinity.Violated("test.disabled");
            Assert.Equal(before, ThreadAffinity.Violations);
        }
        finally
        {
            ThreadAffinity.Enabled = was;
        }
    }

    // A named frame the ledger signature can show: the caller of the mutation, not the backstop's own helpers.
    private static void RecordOffThreadFieldChange(Player player) => player.SetUInt32(UpdateFields.UnitFieldHealth, 12);
}
