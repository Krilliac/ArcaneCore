using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Instances;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Instances;
using ArcaneCore.World.Instances;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Instances;

/// <summary>
/// Instance script data written while the instance's own row is still a retained (not durable) write. The real
/// <see cref="EfInstanceStore.SaveInstanceDataAsync"/> only updates an existing row (vmangos InstanceData::SaveToDB,
/// Maps/InstanceData.cpp:38, <c>UPDATE instance SET data</c>), so the data write must wait behind the retained row insert:
/// run first it updates no row and reports success, and the encounter state is lost once the row lands without it.
/// </summary>
public sealed class InstanceDataBehindRetainedSaveTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task ScriptDataQueuedBehindARetainedInstanceRow_IsDurableOnceTheRowLands()
    {
        string directory = Path.Combine(Path.GetTempPath(), "arcanecore-instancedata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            DbContextOptions<CharacterDbContext> options = new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory, "characters.sqlite"), Pooling = false, DefaultTimeout = 5,
            }.ToString()).Options;
            await using (var setup = new CharacterDbContext(options))
            {
                await SchemaBootstrapper.EnsureAsync(setup, CharacterDbContext.Schema, cancellationToken: default);
            }

            var faults = new RowInsertFaults { Failing = true };
            var registrations = new ServiceCollection();
            registrations.AddScoped(_ => new CharacterDbContext(options));
            registrations.AddScoped<IInstanceStore>(sp => new RowInsertFaultingStore(new EfInstanceStore(sp.GetRequiredService<CharacterDbContext>()), faults));
            await using ServiceProvider provider = registrations.BuildServiceProvider();
            var queue = new InstanceWriteQueue(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance)
            {
                RetryDelay = TimeSpan.Zero,
                RetainedRetryInterval = Timeout.InfiniteTimeSpan,
            };
            queue.Start();

            // The new instance's row insert fails all three inline attempts and is retained; the script saves right behind it.
            queue.EnqueueInstanceSaveForTest(900, 409, 1_700_007_200, "boss1=done door=open");
            await queue.FlushAsync().WaitAsync(Budget);
            Assert.True(faults.Attempts >= 3);

            // The database recovers and the retained-write sweep lands the row.
            faults.Failing = false;
            Assert.True(queue.PostRetainedSweepForTest());
            await queue.FlushAsync().WaitAsync(Budget);
            await queue.StopAsync().WaitAsync(Budget);

            await using var verify = new CharacterDbContext(options);
            InstanceRecord row = Assert.Single((await new EfInstanceStore(verify).LoadAsync()).Instances);
            Assert.Equal(409u, row.MapId);
            Assert.Equal("boss1=done door=open", row.Data);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class RowInsertFaults
    {
        public volatile bool Failing;
        public int Attempts;
    }

    /// <summary>The real store, except that instance row saves fail as SQLite busy while <see cref="RowInsertFaults.Failing"/> is set.</summary>
    private sealed class RowInsertFaultingStore(IInstanceStore inner, RowInsertFaults faults) : IInstanceStore
    {
        public Task<InstanceStoreSnapshot> LoadAsync(CancellationToken cancellationToken = default) => inner.LoadAsync(cancellationToken);

        public Task SaveInstanceAsync(InstanceRecord instance, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref faults.Attempts);
            return faults.Failing
                ? throw new SqliteException("SQLite Error 5: 'database is locked'.", 5)
                : inner.SaveInstanceAsync(instance, cancellationToken);
        }

        public Task SaveInstanceDataAsync(uint instanceId, string data, CancellationToken cancellationToken = default)
            => inner.SaveInstanceDataAsync(instanceId, data, cancellationToken);

        public Task DeleteInstanceAsync(uint instanceId, CancellationToken cancellationToken = default) => inner.DeleteInstanceAsync(instanceId, cancellationToken);

        public Task SaveBindAsync(CharacterInstanceBindRecord bind, CancellationToken cancellationToken = default) => inner.SaveBindAsync(bind, cancellationToken);

        public Task DeleteBindAsync(int characterId, uint instanceId, CancellationToken cancellationToken = default)
            => inner.DeleteBindAsync(characterId, instanceId, cancellationToken);

        public Task SaveResetTimeAsync(InstanceResetRecord reset, CancellationToken cancellationToken = default) => inner.SaveResetTimeAsync(reset, cancellationToken);

        public Task SaveLastInstanceAsync(CharacterLastInstanceRecord last, CancellationToken cancellationToken = default)
            => inner.SaveLastInstanceAsync(last, cancellationToken);

        public Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default) => inner.DeleteCharacterAsync(characterId, cancellationToken);
    }
}
