using System.Collections.Concurrent;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Data;
using ArcaneCore.Data.Characters;
using ArcaneCore.World.Clustering;
using Microsoft.Extensions.Configuration;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Honor;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Honor;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Tests.Honor;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Characters;

/// <summary>
/// A suppressed shutdown lane (another world holds the characters database writer lock) refuses every attempt of the queues that use it, whether or not the queue is stopping:
/// the host's save-all runs while the consumers are still live, so a refusal that waited for the queue's own stop would write every online character into the other world's database.
/// </summary>
[Collection(Net.PacketCaptureCollection.Name)] // a host stop drains every open packet trace in the process
public sealed class WorldWriterGuardSuppressionTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private sealed class CountingStore : ICharacterStore
    {
        private readonly InMemoryCharacterStore _inner = new();
        private int _saves;

        public int Saves => Volatile.Read(ref _saves);

        public ConcurrentQueue<CharacterState> Saved { get; } = new();

        public Task SaveStateAsync(CharacterState state, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _saves);
            Saved.Enqueue(state);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CharacterRecord>> GetByAccountAsync(int accountId, CancellationToken cancellationToken = default) => _inner.GetByAccountAsync(accountId, cancellationToken);
        public Task<CharacterRecord?> GetByIdAsync(int id, CancellationToken cancellationToken = default) => _inner.GetByIdAsync(id, cancellationToken);
        public Task<bool> IsNameTakenAsync(string name, CancellationToken cancellationToken = default) => _inner.IsNameTakenAsync(name, cancellationToken);
        public Task<int> CountByAccountAsync(int accountId, CancellationToken cancellationToken = default) => _inner.CountByAccountAsync(accountId, cancellationToken);
        public Task<CharacterRecord> CreateAsync(CharacterRecord character, CancellationToken cancellationToken = default) => _inner.CreateAsync(character, cancellationToken);
        public Task<bool> DeleteAsync(int id, int accountId, CancellationToken cancellationToken = default) => _inner.DeleteAsync(id, accountId, cancellationToken);
        public Task<IReadOnlyList<ActionButton>> GetActionButtonsAsync(int characterId, CancellationToken cancellationToken = default) => _inner.GetActionButtonsAsync(characterId, cancellationToken);
        public Task<IReadOnlyList<CharacterIdentity>> GetAllIdentitiesAsync(CancellationToken cancellationToken = default) => _inner.GetAllIdentitiesAsync(cancellationToken);
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        private int _stops;

        public int Stops => Volatile.Read(ref _stops);

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => Interlocked.Increment(ref _stops);
    }

    private static CharacterState State(int id, uint money) => new(id, 0, 12, 0, 0, 0, 0, 1, 0, Money: money);

    // OnLockConflict records a fault in the process-wide ledger; the tests that call it forget it again.
    private static void ForgetConflictFault() => FaultLedger.Shared.Reset();

    [Fact]
    public async Task ARunningSaveQueue_OnASuppressedSavesLane_NeverWrites_AndRetainsTheSnapshot()
    {
        var store = new CountingStore();
        await using ServiceProvider services = new ServiceCollection().AddSingleton<ICharacterStore>(store).BuildServiceProvider();
        var drain = new ShutdownDrain();
        var queue = new CharacterSaveQueue(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance, drain: drain);
        drain.Saves.Suppress("another world holds the characters database writer lock");

        try
        {
            queue.Enqueue(State(1, money: 44)); // the queue was never stopped: its consumer is live
            await Assert.ThrowsAsync<StopDrainExhaustedException>(() => queue.FlushCharacterAsync(1).WaitAsync(Budget));

            Assert.Equal(0, store.Saves);
            Assert.True(drain.Saves.Refused >= 1);
        }
        finally
        {
            try { await queue.StopAsync().WaitAsync(Budget); }
            catch (Exception ex) when (ex is not TimeoutException) { } // the retained snapshot is named by the stop: expected
        }

        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public async Task ARunningSaveQueue_WhoseLaneIsNotSuppressed_StillWrites()
    {
        var store = new CountingStore();
        await using ServiceProvider services = new ServiceCollection().AddSingleton<ICharacterStore>(store).BuildServiceProvider();
        var queue = new CharacterSaveQueue(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance, drain: new ShutdownDrain());

        queue.Enqueue(State(1, money: 44));
        await queue.FlushCharacterAsync(1).WaitAsync(Budget);
        await queue.StopAsync().WaitAsync(Budget);

        Assert.Equal(1, store.Saves);
    }

    [Fact]
    public async Task ARunningFeatureQueue_OnASuppressedFeaturesLane_NeverWrites_AndRetainsTheRows()
    {
        var store = new MemoryHonorStore();
        await using ServiceProvider services = new ServiceCollection().AddSingleton<IHonorStore>(store).BuildServiceProvider();
        var drain = new ShutdownDrain();
        var queue = new HonorWriteQueue(services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance, drain.Features) { RetainedRetryInterval = Timeout.InfiniteTimeSpan };
        drain.Features.Suppress("another world holds the characters database writer lock");
        queue.Start(); // running, never stopped

        queue.AppendCp(1, new HonorCpRecord(4, 1, 10f, 100, (byte)HonorKind.Honorable));
        try { await queue.FlushAsync().WaitAsync(Budget); }
        catch (Exception ex) when (ex is not TimeoutException) { } // a flush may report the retained failure

        Assert.Equal(0, store.WriteAttempts);
        Assert.Equal([1], queue.RetainedCharacters);

        try { await queue.StopAsync().WaitAsync(Budget); }
        catch (Exception ex) when (ex is not TimeoutException) { }
        Assert.Equal(0, store.WriteAttempts);
    }

    [Fact]
    public async Task AWorldHostStop_AfterALockConflict_WritesNoSnapshotOfAnOnlinePlayer()
    {
        var store = new CountingStore();
        var drain = new ShutdownDrain();
        var lifetime = new FakeLifetime();
        await using ServiceProvider provider = new ServiceCollection().AddSingleton<ICharacterStore>(store).BuildServiceProvider();
        IServiceScopeFactory scopes = provider.GetRequiredService<IServiceScopeFactory>();
        var saveQueue = new CharacterSaveQueue(scopes, NullLogger<CharacterSaveQueue>.Instance, drain: drain);
        using var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saveQueue, NullLogger<WorldRuntime>.Instance);
        using var guard = new WorldWriterGuard(logger: NullLogger<WorldWriterGuard>.Instance, lifetime: lifetime, setExitCode: _ => { }, drain: drain);
        var host = new WorldHost(world, saveQueue, new CharacterDirectory(), scopes, NullLogger<WorldHost>.Instance, Options.Create(new CharacterFencingOptions { Enabled = false }), writerGuard: guard, drain: drain);

        try
        {
            var character = new CharacterRecord { Id = 7, AccountId = 1, Name = "Suppressedplayer", Race = 1, Class = 1, Gender = 0, Level = 60, MapId = 0 };
            var player = new Player(character, new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), new NullPlayerSession());
            player.Relocate(10f, 10f, 10f, 0f, 0);
            world.AddPlayer(player); // before the world thread exists, so the map is built on this thread
            await host.StartAsync(CancellationToken.None);

            guard.OnLockConflict("PostgreSQL advisory lock 1");
            Assert.Equal(1, lifetime.Stops);

            try { await host.StopAsync(CancellationToken.None).WaitAsync(Budget); }
            catch (Exception ex) when (ex is AggregateException) { } // the retained snapshot is named by the stop: expected

            Assert.Equal(0, store.Saves); // SaveAll queued the snapshot while the consumer was live: it must not have been written
            Assert.True(drain.Saves.Refused >= 1);
        }
        finally
        {
            ForgetConflictFault();
        }
    }

    [Fact]
    public async Task TheContainersActiveGuard_IsBuiltWithTheHostLifetimeAndTheShutdownDrain()
    {
        var lifetime = new FakeLifetime();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Sqlite",
            ["Database:ConnectionString"] = "Data Source=" + Path.Combine(Path.GetTempPath(), "arcanecore-guard-di-" + Guid.NewGuid().ToString("N") + ".db"),
        }).Build();
        await using ServiceProvider provider = new ServiceCollection().AddLogging().AddSingleton<IHostApplicationLifetime>(lifetime)
            .AddCharacterDatabase(configuration).AddWorldDaemon(configuration).BuildServiceProvider();
        WorldWriterGuard guard = provider.GetRequiredService<WorldWriterGuard>();
        ShutdownDrain drain = provider.GetRequiredService<ShutdownDrain>();
        int exitCode = ArcaneCore.Kernel.Ops.ExitCodes.Current;
        try
        {
            guard.OnLockConflict("PostgreSQL advisory lock 1");

            Assert.Equal(1, lifetime.Stops); // the container handed it the host's IHostApplicationLifetime
            Assert.NotNull(drain.Saves.SuppressedReason); // and the same ShutdownDrain the queues use
            Assert.NotNull(drain.Features.SuppressedReason);
            Assert.True(provider.GetRequiredService<ArcaneCore.Data.Characters.Ownership.CharacterDatabaseWriterFence>().IsTripped); // and the context-wide fence the guard was handed
            Assert.Equal(ArcaneCore.Kernel.Ops.ExitCodes.Failure, ArcaneCore.Kernel.Ops.ExitCodes.Current);
        }
        finally
        {
            ArcaneCore.Kernel.Ops.ExitCodes.Current = exitCode;
            ForgetConflictFault();
        }
    }
}
