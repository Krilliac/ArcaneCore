using System.Collections.Concurrent;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Reputation;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.World.Reputation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Reputation;

/// <summary>
/// The reputation write queue over real EF/SQLite storage with injected failures: a write that
/// exhausts its attempts is retained per character and carried forward by the next trigger
/// (docs/integration/reputation.md "Failure durability").
/// </summary>
public sealed class ReputationWriteDurabilityTests
{
    private const uint BootyBay = 21;
    private const uint Stormwind = 72;
    private const uint Alliance = 469;

    [Fact]
    public async Task FailedGain_IsCarriedByNextWriteForSameCharacter()
    {
        await using var fixture = await Fixture.CreateAsync();
        int id = fixture.Ids[0];
        fixture.Control.FailWrites = true;
        fixture.Queue.SaveFactions(id, [new(id, BootyBay, 3100, 0x03)]);
        await fixture.Queue.FlushAsync();
        Assert.True(fixture.Control.AttemptsFor(id) > 0, "the failing store was never reached");

        fixture.Control.FailWrites = false;
        fixture.Queue.SaveWatchedFaction(id, 0);
        await fixture.Queue.FlushAsync();

        CharacterReputationData stored = await fixture.LoadAsync(id);
        Assert.Equal([new CharacterReputationRow(id, BootyBay, 3100, 0x03)], stored.Factions);
        Assert.Equal(0, stored.WatchedFaction);
    }

    [Fact]
    public async Task FailedWatchedFaction_IsCarriedByNextWrite()
    {
        await using var fixture = await Fixture.CreateAsync();
        int id = fixture.Ids[0];
        fixture.Control.FailWrites = true;
        fixture.Queue.SaveWatchedFaction(id, 0);
        await fixture.Queue.FlushAsync();
        Assert.True(fixture.Control.AttemptsFor(id) > 0, "the failing store was never reached");

        fixture.Control.FailWrites = false;
        fixture.Queue.SaveFactions(id, [new(id, BootyBay, 5, 0x01)]);
        await fixture.Queue.FlushAsync();

        CharacterReputationData stored = await fixture.LoadAsync(id);
        Assert.Equal(0, stored.WatchedFaction);
        Assert.Equal([new CharacterReputationRow(id, BootyBay, 5, 0x01)], stored.Factions);
    }

    [Fact]
    public async Task RepeatedFailures_LaterWriteCarriesEveryRetainedRow()
    {
        await using var fixture = await Fixture.CreateAsync();
        int id = fixture.Ids[0];
        fixture.Control.FailWrites = true;
        fixture.Queue.SaveFactions(id, [new(id, BootyBay, 3100, 0x03)]);
        fixture.Queue.SaveFactions(id, [new(id, Stormwind, 900, 0x11)]);
        await fixture.Queue.FlushAsync();

        fixture.Control.FailWrites = false;
        fixture.Queue.SaveFactions(id, [new(id, Alliance, 42, 0x01)]);
        await fixture.Queue.FlushAsync();

        Assert.Equal(
            [new(id, BootyBay, 3100, 0x03), new(id, Stormwind, 900, 0x11), new(id, Alliance, 42, 0x01)],
            (await fixture.LoadAsync(id)).Factions);
    }

    [Fact]
    public async Task Stop_RetriesRetainedWrites_AndARestartedProviderLoadsGainAndWatched()
    {
        await using var fixture = await Fixture.CreateAsync();
        int id = fixture.Ids[0];
        fixture.Control.FailWrites = true;
        fixture.Queue.SaveFactions(id, [new(id, BootyBay, 3100, 0x03)]);
        fixture.Queue.SaveWatchedFaction(id, 0);
        await fixture.Queue.FlushAsync();

        fixture.Control.FailWrites = false;
        await fixture.Queue.StopAsync();

        CharacterReputationData restarted = await fixture.LoadAfterRestartAsync(id);
        Assert.Equal([new CharacterReputationRow(id, BootyBay, 3100, 0x03)], restarted.Factions);
        Assert.Equal(0, restarted.WatchedFaction);
    }

    [Fact]
    public async Task Stop_WithStorageStillDown_ThrowsNamingTheCharacter()
    {
        await using var fixture = await Fixture.CreateAsync();
        int id = fixture.Ids[0];
        fixture.Control.FailWrites = true;
        fixture.Queue.SaveFactions(id, [new(id, BootyBay, 3100, 0x03)]);
        await fixture.Queue.FlushAsync();

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(fixture.Queue.StopAsync);
        Assert.Contains(id.ToString(), ex.Message);
        Assert.IsType<IOException>(ex.InnerException);
    }

    [Fact]
    public async Task CommitThenThrow_RetryIsIdempotent()
    {
        await using var fixture = await Fixture.CreateAsync();
        int id = fixture.Ids[0];
        fixture.Control.LoseAcknowledgement = true;
        fixture.Queue.SaveFactions(id, [new(id, BootyBay, 3100, 0x03)]);
        await fixture.Queue.FlushAsync();

        Assert.True(fixture.Control.AttemptsFor(id) >= 2, "the lost acknowledgement should have forced a retry");
        Assert.Equal([new CharacterReputationRow(id, BootyBay, 3100, 0x03)], (await fixture.LoadAsync(id)).Factions);
        await using CharacterDbContext db = fixture.NewContext();
        Assert.Equal(1, await db.Set<CharacterReputationEntity>().CountAsync());
    }

    [Fact]
    public async Task FlushAsync_DoesNotRetryAnotherCharactersRetainedFailure()
    {
        await using var fixture = await Fixture.CreateAsync();
        int a = fixture.Ids[0];
        int b = fixture.Ids[1];
        fixture.Control.FailWrites = true;
        fixture.Queue.SaveFactions(a, [new(a, BootyBay, 3100, 0x03)]);
        await fixture.Queue.FlushAsync();
        int attemptsForA = fixture.Control.AttemptsFor(a);
        Assert.True(attemptsForA > 0);

        fixture.Control.FailWrites = false;
        fixture.Queue.SaveFactions(b, [new(b, BootyBay, 7, 0x01)]);
        await fixture.Queue.FlushAsync();
        await fixture.Queue.FlushAsync();

        Assert.Equal(attemptsForA, fixture.Control.AttemptsFor(a));
        Assert.Equal(1, fixture.Control.AttemptsFor(b));
        Assert.Empty((await fixture.LoadAsync(a)).Factions);
    }

    [Fact]
    public async Task DeleteWhileWriteInFlight_DoesNotResurrect()
    {
        await using var fixture = await Fixture.CreateAsync();
        int id = fixture.Ids[0];
        fixture.Control.HoldNextWrite();
        fixture.Queue.SaveFactions(id, [new(id, BootyBay, 3100, 0x03)]);
        fixture.Queue.SaveWatchedFaction(id, 0);
        await fixture.Control.Entered.Task.WaitAsync(Fixture.Budget);
        fixture.Queue.DeleteCharacter(id);
        fixture.Control.Release.TrySetResult();
        await fixture.Queue.FlushAsync().WaitAsync(Fixture.Budget);

        CharacterReputationData stored = await fixture.LoadAsync(id);
        Assert.Empty(stored.Factions);
        Assert.Equal(-1, stored.WatchedFaction);
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        public static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);
        private readonly string _path;
        private readonly DbContextOptions<CharacterDbContext> _options;
        private readonly ServiceProvider _services;

        private Fixture(string path, DbContextOptions<CharacterDbContext> options, ServiceProvider services, Control control, int[] ids)
        {
            _path = path;
            _options = options;
            _services = services;
            Control = control;
            Ids = ids;
            Queue = new ReputationWriteQueue(services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);
            Queue.Start();
        }

        public Control Control { get; }
        public int[] Ids { get; }
        public ReputationWriteQueue Queue { get; }
        public CharacterDbContext NewContext() => new(_options);

        public static async Task<Fixture> CreateAsync()
        {
            string path = Path.Combine(Path.GetTempPath(), $"arcanecore-reputation-durability-{Guid.NewGuid():N}.db");
            var options = new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
            var ids = new List<int>();
            await using (var db = new CharacterDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                ICharacterStore characters = new EfCharacterStore(db);
                foreach (string name in new[] { "Durablea", "Durableb" })
                {
                    ids.Add((await characters.CreateAsync(new CharacterRecord { AccountId = 79, Name = name, Race = 1, Class = 1, Level = 10 })).Id);
                }
            }

            var control = new Control();
            var services = new ServiceCollection();
            services.AddScoped(_ => new CharacterDbContext(options));
            services.AddScoped<ICharacterReputationStore>(provider => new FaultingStore(
                new EfCharacterReputationStore(provider.GetRequiredService<CharacterDbContext>()), control));
            return new Fixture(path, options, services.BuildServiceProvider(), control, [.. ids]);
        }

        public async Task<CharacterReputationData> LoadAsync(int id)
        {
            await using CharacterDbContext db = NewContext();
            return await new EfCharacterReputationStore(db).LoadAsync(id);
        }

        /// <summary>Simulated process restart: a brand-new provider and context over the same database file.</summary>
        public async Task<CharacterReputationData> LoadAfterRestartAsync(int id)
        {
            var services = new ServiceCollection();
            services.AddScoped(_ => new CharacterDbContext(_options));
            services.AddScoped<ICharacterReputationStore>(provider => new EfCharacterReputationStore(provider.GetRequiredService<CharacterDbContext>()));
            await using ServiceProvider restarted = services.BuildServiceProvider();
            await using AsyncServiceScope scope = restarted.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ICharacterReputationStore>().LoadAsync(id);
        }

        public async ValueTask DisposeAsync()
        {
            Control.FailWrites = false;
            Control.Release.TrySetResult();
            try
            {
                await Queue.StopAsync();
            }
            catch (InvalidOperationException)
            {
                // a test that proves the shutdown failure leaves the failure cached
            }

            await _services.DisposeAsync();
            File.Delete(_path);
        }
    }

    internal sealed class Control
    {
        private readonly ConcurrentDictionary<int, int> _attempts = new();
        private int _hold;
        public volatile bool FailWrites;
        public volatile bool LoseAcknowledgement;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int AttemptsFor(int id) => _attempts.GetValueOrDefault(id);

        public void HoldNextWrite() => Volatile.Write(ref _hold, 1);

        public async Task BeforeAsync(int id)
        {
            _attempts.AddOrUpdate(id, 1, (_, n) => n + 1);
            if (Interlocked.Exchange(ref _hold, 0) == 1)
            {
                Entered.TrySetResult();
                await Release.Task.ConfigureAwait(false);
            }

            if (FailWrites)
            {
                throw new IOException("controlled reputation storage failure");
            }
        }
    }

    private sealed class FaultingStore(ICharacterReputationStore inner, Control control) : ICharacterReputationStore
    {
        public Task<CharacterReputationData> LoadAsync(int characterId, CancellationToken cancellationToken = default)
            => inner.LoadAsync(characterId, cancellationToken);

        public async Task SaveFactionsAsync(int characterId, IReadOnlyList<CharacterReputationRow> upserts, CancellationToken cancellationToken = default)
        {
            await control.BeforeAsync(characterId);
            await inner.SaveFactionsAsync(characterId, upserts, cancellationToken);
            LoseAcknowledgementOnce();
        }

        public async Task SaveWatchedFactionAsync(int characterId, int watchedFaction, CancellationToken cancellationToken = default)
        {
            await control.BeforeAsync(characterId);
            await inner.SaveWatchedFactionAsync(characterId, watchedFaction, cancellationToken);
            LoseAcknowledgementOnce();
        }

        public async Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default)
        {
            await control.BeforeAsync(characterId);
            await inner.DeleteCharacterAsync(characterId, cancellationToken);
            LoseAcknowledgementOnce();
        }

        private void LoseAcknowledgementOnce()
        {
            if (control.LoseAcknowledgement)
            {
                control.LoseAcknowledgement = false;
                throw new IOException("controlled acknowledgement loss after the write committed");
            }
        }
    }
}
