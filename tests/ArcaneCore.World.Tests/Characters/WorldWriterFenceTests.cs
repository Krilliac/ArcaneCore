using System.Globalization;
using ArcaneCore.Data;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.Data.Stores;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Characters.Pets;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Clustering;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Skills;
using ArcaneCore.World.WorldState;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Characters;

/// <summary>
/// The writer-lock fence is context-wide: after a lock conflict no writer reaches the characters database, whether or not it has a drain lane (the skill coordinator, the
/// state save queues, the battleground entry-point rows, persistent pets), because every one of them goes through the pooled <c>CharacterDbContext</c>. The database is
/// watched from an independent connection (<c>PRAGMA data_version</c> moves on every commit of another connection).
/// </summary>
[Collection(Net.PacketCaptureCollection.Name)] // a host stop drains every open packet trace in the process
public sealed class WorldWriterFenceTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }

    private static long DataVersion(SqliteConnection observer)
    {
        using SqliteCommand command = observer.CreateCommand();
        command.CommandText = "PRAGMA data_version";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static async Task AssertFencedAsync(Func<Task> action)
    {
        Exception? thrown = await Record.ExceptionAsync(action);
        Assert.NotNull(thrown);
        for (Exception? e = thrown; e is not null; e = e.InnerException)
        {
            if (e is CharacterOwnershipRejectedException) return;
        }

        Assert.Fail("expected a CharacterOwnershipRejectedException but got " + thrown);
    }

    [Fact]
    public async Task AfterALockConflict_NoWriterReachesTheDatabase_WhicheverOneTries()
    {
        string path = Path.Combine(Path.GetTempPath(), "arcanecore-writer-fence-" + Guid.NewGuid().ToString("N") + ".db");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Sqlite",
            ["Database:ConnectionString"] = "Data Source=" + path,
        }).Build();
        await using ServiceProvider provider = new ServiceCollection().AddLogging().AddSingleton<IHostApplicationLifetime>(new FakeLifetime()).AddSingleton<IConfiguration>(configuration)
            .AddCharacterDatabase(configuration).AddWorldDaemon(configuration).BuildServiceProvider();
        await provider.GetRequiredService<CharacterDbInitializer>().InitializeAsync();

        int characterId;
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            characterId = (await new EfCharacterStore(scope.ServiceProvider.GetRequiredService<CharacterDbContext>())
                .CreateAsync(new CharacterRecord { AccountId = 1, Name = "Fenceplayer", Race = 1, Class = 1, Gender = 0, Level = 60, MapId = 0 })).Id;
        }

        IServiceScopeFactory scopes = provider.GetRequiredService<IServiceScopeFactory>();
        var drain = provider.GetRequiredService<ShutdownDrain>();
        var fence = provider.GetRequiredService<CharacterDatabaseWriterFence>();
        WorldWriterGuard guard = provider.GetRequiredService<WorldWriterGuard>();
        var saveQueue = new CharacterSaveQueue(scopes, NullLogger<CharacterSaveQueue>.Instance, drain: drain);
        await using var skills = new SkillSaveCoordinator(scopes, NullLogger.Instance, TimeSpan.FromMilliseconds(50));
        skills.Start();
        var silithyst = new StateSaveQueue<int, SilithystState>(async state =>
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            if (scope.ServiceProvider.GetService<ISilithystStore>() is { } store) await store.SaveAsync(state);
        }, NullLogger.Instance, "Silithyst");
        using var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saveQueue, NullLogger<WorldRuntime>.Instance);
        var host = new WorldHost(world, saveQueue, new CharacterDirectory(), scopes, NullLogger<WorldHost>.Instance, Options.Create(new CharacterFencingOptions { Enabled = false }), writerGuard: guard, drain: drain);
        using var observer = new SqliteConnection("Data Source=" + path + ";Pooling=False");
        observer.Open();
        int exitCode = ArcaneCore.Kernel.Ops.ExitCodes.Current;

        async Task WriteAllAsync(int tag, bool fenced)
        {
            skills.Enqueue(characterId, new CharacterSkillSnapshot([new CharacterSkillRow(95, (ushort)(1 + tag), 300)], []));
            try { await skills.FlushAllAsync(Budget); }
            catch (Exception ex) when (ex is not TimeoutException) { } // terminal failure once the fence is tripped: expected
            silithyst.Save(0, new SilithystState((uint)tag, 2, 3));
            await silithyst.FlushAsync(); // a failed write is logged by the queue, never thrown

            // The entry-point rows and the pets go through their stores exactly as the feature's write loop and the pet service do.
            async Task StoresAsync()
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IBattlegroundEntryPointStore>()
                    .SaveAsync(new BattlegroundEntryPointRecord(characterId, 1, 469, 0, tag, 2, 3, 0));
                await scope.ServiceProvider.GetRequiredService<IPersistentPetStore>()
                    .SaveCurrentAsync(PersistentPetSnapshot.Empty(characterId, 1, 416, (byte)(1 + tag)));
            }

            if (fenced) await AssertFencedAsync(StoresAsync);
            else await StoresAsync();
        }

        try
        {
            var character = new CharacterRecord { Id = characterId, AccountId = 1, Name = "Fenceplayer", Race = 1, Class = 1, Gender = 0, Level = 60, MapId = 0 };
            var player = new Player(character, new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), new NullPlayerSession());
            player.Relocate(10f, 10f, 10f, 0f, 0);
            world.AddPlayer(player); // before the world thread exists, so the map is built on this thread
            await host.StartAsync(CancellationToken.None);

            // The writers really write while the fence is open (a database that never changes would pass the check below for the wrong reason).
            long open = DataVersion(observer);
            await WriteAllAsync(1, fenced: false);
            Assert.True(DataVersion(observer) > open, "the writers wrote nothing before the conflict: the check below proves nothing");

            long before = DataVersion(observer);
            guard.OnLockConflict("PostgreSQL advisory lock 1");
            Assert.True(fence.IsTripped);

            await WriteAllAsync(2, fenced: true);

            try { await host.StopAsync(CancellationToken.None).WaitAsync(Budget); }
            catch (Exception ex) when (ex is AggregateException) { } // the retained snapshots are named by the stop: expected

            Assert.Equal(before, DataVersion(observer));
            Assert.True(fence.IsTripped);
        }
        finally
        {
            ArcaneCore.Kernel.Ops.ExitCodes.Current = exitCode;
            FaultLedger.Shared.Reset();
        }
    }
}
