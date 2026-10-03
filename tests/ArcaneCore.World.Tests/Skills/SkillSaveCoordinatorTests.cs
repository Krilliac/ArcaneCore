using ArcaneCore.Kernel.Skills;
using ArcaneCore.World.Skills;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Skills;

/// <summary>The single-writer, retain-on-failure, latest-snapshot-wins skill save queue.</summary>
public sealed class SkillSaveCoordinatorTests
{
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(20);

    private static (SkillSaveCoordinator Coordinator, InMemoryCharacterSkillStore Store) Create()
    {
        var store = new InMemoryCharacterSkillStore();
        ServiceProvider services = new ServiceCollection().AddSingleton<ICharacterSkillStore>(store).BuildServiceProvider();
        var coordinator = new SkillSaveCoordinator(services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance, Fast);
        coordinator.Start();
        return (coordinator, store);
    }

    private static CharacterSkillSnapshot Snapshot(ushort value) => new([new((ushort)SkillIds.Swords, value, 50)], []);

    [Fact]
    public async Task Enqueue_WritesTheSnapshot_AndFlushWaitsForIt()
    {
        (SkillSaveCoordinator coordinator, InMemoryCharacterSkillStore store) = Create();
        await using (coordinator)
        {
            coordinator.Enqueue(7, Snapshot(5));
            await coordinator.FlushCharacterAsync(7, TimeSpan.FromSeconds(5));
            Assert.Equal(5, Assert.Single((await store.LoadAsync(7)).Skills).Value);
            Assert.Equal(0, coordinator.Pending);
            await coordinator.FlushAllAsync(TimeSpan.FromSeconds(5));   // nothing pending: returns at once
        }
    }

    [Fact]
    public async Task AFailedWriteIsRetained_AndRetriedUntilItLands()
    {
        (SkillSaveCoordinator coordinator, InMemoryCharacterSkillStore store) = Create();
        await using (coordinator)
        {
            store.FailWrites = true;
            coordinator.Enqueue(7, Snapshot(9));
            await Assert.ThrowsAsync<TimeoutException>(() => coordinator.FlushCharacterAsync(7, TimeSpan.FromMilliseconds(300)));
            Assert.Equal(1, coordinator.Pending);                    // never dropped
            Assert.NotNull(coordinator.LastFailure(7));
            Assert.Empty((await store.LoadAsync(7)).Skills);

            store.FailWrites = false;
            await coordinator.FlushCharacterAsync(7, TimeSpan.FromSeconds(5));
            Assert.Equal(9, Assert.Single((await store.LoadAsync(7)).Skills).Value);
            Assert.Null(coordinator.LastFailure(7));
        }
    }

    [Fact]
    public async Task ANewerSnapshotSupersedesAFailedOne_AndOnlyTheNewerIsWritten()
    {
        (SkillSaveCoordinator coordinator, InMemoryCharacterSkillStore store) = Create();
        await using (coordinator)
        {
            store.FailWrites = true;
            coordinator.Enqueue(7, Snapshot(9));
            await Assert.ThrowsAsync<TimeoutException>(() => coordinator.FlushCharacterAsync(7, TimeSpan.FromMilliseconds(200)));

            coordinator.Enqueue(7, Snapshot(12));
            store.FailWrites = false;
            await coordinator.FlushCharacterAsync(7, TimeSpan.FromSeconds(5));
            Assert.Equal(12, Assert.Single((await store.LoadAsync(7)).Skills).Value);
            Assert.Equal(1, store.Writes);
        }
    }

    [Fact]
    public async Task Forget_DropsAPendingSnapshotAndReleasesWaiters()
    {
        (SkillSaveCoordinator coordinator, InMemoryCharacterSkillStore store) = Create();
        await using (coordinator)
        {
            store.FailWrites = true;
            coordinator.Enqueue(7, Snapshot(9));
            Task flush = coordinator.FlushCharacterAsync(7, TimeSpan.FromSeconds(5));
            await Task.Delay(100);
            coordinator.Forget(7);
            await flush.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, coordinator.Pending);
            store.FailWrites = false;
            await Task.Delay(100);
            Assert.Empty((await store.LoadAsync(7)).Skills);
        }
    }

    [Fact]
    public async Task OtherCharactersAreWrittenIndependently()
    {
        (SkillSaveCoordinator coordinator, InMemoryCharacterSkillStore store) = Create();
        await using (coordinator)
        {
            coordinator.Enqueue(1, Snapshot(3));
            coordinator.Enqueue(2, Snapshot(4));
            await coordinator.FlushAllAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(3, Assert.Single((await store.LoadAsync(1)).Skills).Value);
            Assert.Equal(4, Assert.Single((await store.LoadAsync(2)).Skills).Value);
        }
    }

    [Fact]
    public async Task DisposeIsIdempotent_AndAnUnstartedCoordinatorRefusesToWait()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var unstarted = new SkillSaveCoordinator(services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);
        unstarted.Enqueue(1, Snapshot(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => unstarted.FlushAllAsync(TimeSpan.FromMilliseconds(50)));
        await unstarted.DisposeAsync();
        await unstarted.DisposeAsync();
    }
}
