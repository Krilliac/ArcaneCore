using System.Collections.Concurrent;
using ArcaneCore.Data;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.Data.Reputation;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Reputation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Reputation;

/// <summary>
/// Reputation persistence under character ownership fencing: mutation-time token capture,
/// non-coalescing across distinct ownership tokens, terminal single-attempt rejection that stops
/// the host on a stale token, and token-independent post-delete cleanup.
/// </summary>
public sealed class ReputationOwnershipPersistenceTests
{
    private const uint BootyBay = 21;
    private const uint Stormwind = 72;
    private const uint Alliance = 469;

    [Fact]
    public async Task CurrentOwnershipToken_PersistsFactionsAndWatchedFactionUnderActiveFence()
    {
        await using var env = await FencedDbFixture.CreateAsync();
        int id = env.CharacterId;
        var row = new CharacterReputationRow(id, Stormwind, 1250, 0x11);

        env.Queue.SaveFactions(id, [row], env.FirstToken);
        env.Queue.SaveWatchedFaction(id, 7, env.FirstToken);
        await env.Queue.FlushCharacterAsync(id).WaitAsync(TimeSpan.FromSeconds(5));

        CharacterReputationData stored = await env.LoadAsync(id);
        Assert.Equal([row], stored.Factions);
        Assert.Equal(7, stored.WatchedFaction);
        Assert.False(env.Lifetime.Stopped);
        await env.Queue.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task StaleOwnershipToken_IsTerminalAfterOneAttempt_StopsHost_AndIsNeverRetried()
    {
        await using var env = await FencedDbFixture.CreateAsync();
        int id = env.CharacterId;
        var initial = new CharacterReputationRow(id, BootyBay, 500, 0x01);
        env.Queue.SaveFactions(id, [initial], env.FirstToken);
        env.Queue.SaveWatchedFaction(id, 2, env.FirstToken);
        await env.Queue.FlushCharacterAsync(id).WaitAsync(TimeSpan.FromSeconds(5));
        env.ResetAttempts();

        CharacterOwnershipToken second = await env.TakeoverAsync();
        Assert.NotEqual(env.FirstToken, second);

        Exception? fatalError = null;
        env.Queue.FatalFailure += ex => Interlocked.CompareExchange(ref fatalError, ex, null);

        // Queue both faction and watched-faction mutations under the now-stale first token.
        env.Queue.SaveFactions(id, [new CharacterReputationRow(id, BootyBay, 9999, 0x03)], env.FirstToken);
        env.Queue.SaveWatchedFaction(id, 5, env.FirstToken);
        await env.Queue.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));

        // Exactly 1 attempt: no 3-attempt transient retry on an ownership rejection.
        Assert.Equal(1, env.AttemptsFor(id));
        Assert.True(env.Lifetime.Stopped);
        Assert.Equal(1, env.Lifetime.StopCount);
        Assert.NotNull(fatalError);
        Assert.True(ContainsRejection(fatalError!));
        Assert.True(env.Queue.HasRetainedFailure(id));
        Assert.Equal([id], env.Queue.RetainedCharacters);

        // Neither logout retry, background sweep, login barrier, nor shutdown drain retries a rejected overlay.
        env.Queue.RequestRetry(id);
        Assert.True(env.Queue.PostRetainedSweepForTest());
        await env.Queue.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, env.AttemptsFor(id));

        InvalidOperationException flushError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => env.Queue.FlushCharacterAsync(id).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(ContainsRejection(flushError));
        Assert.Equal(1, env.AttemptsFor(id));

        InvalidOperationException stopError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => env.Queue.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains(id.ToString(), stopError.Message, StringComparison.Ordinal);
        Assert.True(ContainsRejection(stopError));
        Assert.Equal(1, env.AttemptsFor(id));

        // Storage still holds only the pre-takeover values.
        CharacterReputationData stored = await env.LoadAsync(id);
        Assert.Equal([initial], stored.Factions);
        Assert.Equal(2, stored.WatchedFaction);
    }

    [Fact]
    public async Task StaleWatchedFactionOnlyWrite_IsTerminalAfterOneAttempt_AndStopsHostThroughDiLifetime()
    {
        await using var env = await FencedDbFixture.CreateAsync(injectLifetimeViaDiOnly: true);
        int id = env.CharacterId;
        await env.TakeoverAsync();

        env.Queue.SaveWatchedFaction(id, 4, env.FirstToken);
        await env.Queue.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, env.AttemptsFor(id));
        Assert.True(env.Lifetime.Stopped);
        CharacterReputationData stored = await env.LoadAsync(id);
        Assert.Equal(-1, stored.WatchedFaction);
    }

    [Fact]
    public async Task TokenIsCapturedAtMutationTime_AndCarriedAcrossTransientRetries()
    {
        var recorder = new RecordingReputationStore { TransientFailuresRemaining = 2 };
        await using ServiceProvider services = new ServiceCollection()
            .AddScoped<ICharacterReputationStore>(_ => recorder)
            .BuildServiceProvider();
        var queue = new ReputationWriteQueue(services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);

        CharacterOwnershipToken capturedToken = Token(1, epoch: 10);
        CharacterOwnershipToken mutatedLaterToken = Token(1, epoch: 99);

        Player player = CreatePlayer(1, capturedToken);

        // Hold queue start until after Player.CharacterOwnership has changed on the live entity,
        // proving the token was captured when the mutation happened rather than read when the write runs.
        queue.SaveFactions(1, [new CharacterReputationRow(1, BootyBay, 400, 0x01)], player.CharacterOwnership);
        queue.SaveWatchedFaction(1, 3, player.CharacterOwnership);
        player.CharacterOwnership = mutatedLaterToken;

        queue.Start();
        await queue.FlushCharacterAsync(1).WaitAsync(TimeSpan.FromSeconds(5));
        await queue.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

        // 2 transient failures + 1 succeeding attempt (factions + watched) = 4 store calls, all carrying capturedToken.
        Assert.Equal(4, recorder.Calls.Count);
        Assert.All(recorder.Calls, call => Assert.Equal(capturedToken, call.Ownership));
    }

    [Fact]
    public async Task ReputationFeatureSink_CapturesPlayerOwnershipTokenAtMutationTime()
    {
        var recorder = new RecordingReputationStore();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        recorder.OnWrite = async () =>
        {
            entered.TrySetResult();
            await hold.Task.ConfigureAwait(false);
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new FactionCatalog([
            new FactionRecord(BootyBay, 0, [0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0], 0, "Booty Bay"),
        ]));
        services.AddScoped<ICharacterReputationStore>(_ => recorder);
        services.AddSingleton<ReputationFeature>();

        await using ServiceProvider provider = services.BuildServiceProvider();
        ReputationFeature feature = provider.GetRequiredService<ReputationFeature>();
        using var world = new WorldRuntime(
            new WorldRuntimeOptions { UpdateCompressionThreshold = 0, AutosaveIntervalMs = 0, CommandTimeBudgetMs = 0 },
            new NullSaveQueue(),
            NullLogger<WorldRuntime>.Instance);
        feature.Attach(world);

        CharacterOwnershipToken firstToken = Token(7, epoch: 3);
        CharacterOwnershipToken secondToken = Token(7, epoch: 4);
        Player player = CreatePlayer(7, firstToken);
        feature.Service.Track(player, feature.Service.Create(player, CharacterReputationData.Empty));

        // First mutation blocks in flight so the subsequent mutation is queued before the consumer writes it.
        Assert.True(feature.Service.ModifyReputation(player, BootyBay, 250));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Mutate under firstToken, then immediately switch player.CharacterOwnership before releasing the write.
        Assert.True(feature.Service.ModifyReputation(player, BootyBay, 750));
        Assert.True(feature.Service.SetWatchedFaction(player, 0));
        player.CharacterOwnership = secondToken;

        hold.TrySetResult();
        await feature.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await feature.DisposeAsync();

        Assert.NotEmpty(recorder.Calls);
        Assert.All(recorder.Calls, call => Assert.Equal(firstToken, call.Ownership));
    }

    [Fact]
    public async Task WritesWithDifferentOwnershipTokens_NeverCoalesceAcrossTokens()
    {
        var recorder = new RecordingReputationStore();
        await using ServiceProvider services = new ServiceCollection()
            .AddScoped<ICharacterReputationStore>(_ => recorder)
            .BuildServiceProvider();
        var queue = new ReputationWriteQueue(services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);

        CharacterOwnershipToken tokenA = Token(1, epoch: 1);
        CharacterOwnershipToken tokenB = Token(1, epoch: 2);

        // Queue two mutations under tokenA (which should coalesce with each other) and two under tokenB
        // before starting the consumer so all four are pending simultaneously.
        queue.SaveFactions(1, [new CharacterReputationRow(1, BootyBay, 100, 0x01)], tokenA);
        queue.SaveFactions(1, [new CharacterReputationRow(1, Stormwind, 200, 0x01)], tokenA);
        queue.SaveWatchedFaction(1, 1, tokenA);

        queue.SaveFactions(1, [new CharacterReputationRow(1, BootyBay, 900, 0x03), new CharacterReputationRow(1, Alliance, 400, 0x01)], tokenB);
        queue.SaveWatchedFaction(1, 2, tokenB);

        queue.Start();
        await queue.FlushCharacterAsync(1).WaitAsync(TimeSpan.FromSeconds(5));
        await queue.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

        (string Kind, CharacterReputationRow[] Rows, int? Watched, CharacterOwnershipToken? Ownership)[] calls = [.. recorder.Calls];
        Assert.Equal(4, calls.Length);

        // First overlay: tokenA factions (BootyBay=100, Stormwind=200) and tokenA watched=1.
        Assert.Equal("factions", calls[0].Kind);
        Assert.Equal(tokenA, calls[0].Ownership);
        Assert.Equal([new CharacterReputationRow(1, BootyBay, 100, 0x01), new CharacterReputationRow(1, Stormwind, 200, 0x01)], calls[0].Rows);

        Assert.Equal("watched", calls[1].Kind);
        Assert.Equal(tokenA, calls[1].Ownership);
        Assert.Equal(1, calls[1].Watched);

        // Second overlay: tokenB factions (BootyBay=900, Alliance=400) and tokenB watched=2 — Stormwind from tokenA is NOT coalesced into tokenB.
        Assert.Equal("factions", calls[2].Kind);
        Assert.Equal(tokenB, calls[2].Ownership);
        Assert.Equal([new CharacterReputationRow(1, BootyBay, 900, 0x03), new CharacterReputationRow(1, Alliance, 400, 0x01)], calls[2].Rows);

        Assert.Equal("watched", calls[3].Kind);
        Assert.Equal(tokenB, calls[3].Ownership);
        Assert.Equal(2, calls[3].Watched);
    }

    [Fact]
    public async Task DeleteCharacter_ClearsPriorOwnershipRejection_AndRunsTokenIndependentCleanup()
    {
        await using var env = await FencedDbFixture.CreateAsync();
        int id = env.CharacterId;
        env.Queue.SaveFactions(id, [new CharacterReputationRow(id, BootyBay, 500, 0x01)], env.FirstToken);
        env.Queue.SaveWatchedFaction(id, 2, env.FirstToken);
        await env.Queue.FlushCharacterAsync(id).WaitAsync(TimeSpan.FromSeconds(5));

        await env.TakeoverAsync();
        env.Queue.SaveFactions(id, [new CharacterReputationRow(id, BootyBay, 9000, 0x03)], env.FirstToken);
        await env.Queue.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(env.Queue.HasRetainedFailure(id));

        await env.RemoveCharacterRowAsync(id);
        env.Queue.DeleteCharacter(id);
        await env.Queue.FlushCharacterAsync(id).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(env.Queue.HasRetainedFailure(id));
        CharacterReputationData stored = await env.LoadAsync(id);
        Assert.Empty(stored.Factions);
        Assert.Equal(-1, stored.WatchedFaction);
        await env.Queue.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static Player CreatePlayer(int id, CharacterOwnershipToken token)
    {
        var character = new CharacterRecord
        {
            Id = id,
            AccountId = 1,
            Name = $"P{id}",
            Race = (byte)Race.Human,
            Class = (byte)Class.Warrior,
            Gender = (byte)Gender.Male,
            Level = 1,
        };
        var appearance = new PlayerAppearance(
            DisplayId: 49, FactionTemplate: 1, PowerType.Rage, BaseHealth: 60, BaseMana: 0,
            MaxHealth: 60, MaxPower: 1000, StartPower: 0, NextLevelXp: 400);
        return new Player(character, appearance, new NoopSession())
        {
            CharacterOwnership = token,
        };
    }

    private static CharacterOwnershipToken Token(int id, long epoch)
        => new("world", epoch, Guid.Parse($"00000000-0000-0000-0000-{id:000000000000}"));

    private static bool ContainsRejection(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is CharacterOwnershipRejectedException)
            {
                return true;
            }
        }

        return false;
    }

    private sealed class NoopSession : IPlayerSession
    {
        public int AccountId => 1;
        public AccountSecurity Security => AccountSecurity.Player;
        public bool WantsUpdateObjects => false;
        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload) { }
        public void ProcessWorldPackets(Player player) { }
        public void Kick() { }
        public void OnLoggedOut() { }
    }

    private sealed class RecordingReputationStore : ICharacterReputationStore
    {
        public ConcurrentQueue<(string Kind, CharacterReputationRow[] Rows, int? Watched, CharacterOwnershipToken? Ownership)> Calls { get; } = new();
        public int TransientFailuresRemaining;
        public Func<Task>? OnWrite { get; set; }

        public Task<CharacterReputationData> LoadAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.FromResult(CharacterReputationData.Empty);

        public async Task SaveFactionsAsync(int characterId, IReadOnlyList<CharacterReputationRow> upserts,
            CancellationToken cancellationToken = default, CharacterOwnershipToken? ownership = null)
        {
            if (OnWrite is { } hook)
            {
                await hook().ConfigureAwait(false);
            }

            Calls.Enqueue(("factions", [.. upserts.OrderBy(r => r.Faction)], null, ownership));
            ThrowIfTransient();
        }

        public async Task SaveWatchedFactionAsync(int characterId, int watchedFaction,
            CancellationToken cancellationToken = default, CharacterOwnershipToken? ownership = null)
        {
            if (OnWrite is { } hook)
            {
                await hook().ConfigureAwait(false);
            }

            Calls.Enqueue(("watched", [], watchedFaction, ownership));
            ThrowIfTransient();
        }

        public Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DeleteDeletedCharacterAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        private void ThrowIfTransient()
        {
            while (true)
            {
                int current = Volatile.Read(ref TransientFailuresRemaining);
                if (current <= 0)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref TransientFailuresRemaining, current - 1, current) == current)
                {
                    throw new IOException("simulated transient reputation store failure");
                }
            }
        }
    }

    private sealed class SpyHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();
        private int _stopCount;

        public int StopCount => Volatile.Read(ref _stopCount);
        public bool Stopped => StopCount > 0;
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
            Interlocked.Increment(ref _stopCount);
            _stopping.Cancel();
        }
    }

    private sealed class FencedDbFixture : IAsyncDisposable
    {
        private readonly string _path;
        private readonly DbContextOptions<CharacterDbContext> _plainOptions;
        private readonly ServiceProvider _services;
        private readonly ConcurrentDictionary<int, int> _attempts = new();

        private FencedDbFixture(
            string path,
            DbContextOptions<CharacterDbContext> plainOptions,
            ServiceProvider services,
            SpyHostApplicationLifetime lifetime,
            ConcurrentDictionary<int, int> attempts,
            int characterId,
            CharacterOwnershipToken firstToken,
            bool injectLifetimeViaDiOnly)
        {
            _path = path;
            _plainOptions = plainOptions;
            _services = services;
            Lifetime = lifetime;
            _attempts = attempts;
            CharacterId = characterId;
            FirstToken = firstToken;
            Queue = new ReputationWriteQueue(
                services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger.Instance,
                injectLifetimeViaDiOnly ? null : lifetime)
            {
                RetainedRetryInterval = Timeout.InfiniteTimeSpan,
            };
            Queue.Start();
        }

        public SpyHostApplicationLifetime Lifetime { get; }
        public int CharacterId { get; }
        public CharacterOwnershipToken FirstToken { get; }
        public ReputationWriteQueue Queue { get; }

        public int AttemptsFor(int characterId) => _attempts.GetValueOrDefault(characterId);
        public void ResetAttempts() => _attempts.Clear();

        public static async Task<FencedDbFixture> CreateAsync(bool injectLifetimeViaDiOnly = false)
        {
            string path = Path.Combine(Path.GetTempPath(), $"arcanecore-rep-fence-{Guid.NewGuid():N}.db");
            string connectionString = $"Data Source={path};Pooling=False";
            var plainOptions = new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite(connectionString).Options;

            int characterId;
            CharacterOwnershipToken firstToken;
            await using (var setup = new CharacterDbContext(plainOptions))
            {
                await SchemaBootstrapper.EnsureAsync(setup, CharacterDbContext.Schema);
                characterId = (await new EfCharacterStore(setup).CreateAsync(new CharacterRecord
                {
                    AccountId = 1, Name = "RepFenced", Race = 1, Class = 1, Level = 10,
                })).Id;
                firstToken = (await new EfCharacterOwnershipStore(setup)
                    .TryAcquireAsync("world", "node-a", Guid.NewGuid()))!;
            }

            var settings = new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["Database:ConnectionString"] = connectionString,
                ["Cluster:Fencing:Enabled"] = "true",
            };
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var lifetime = new SpyHostApplicationLifetime();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IHostApplicationLifetime>(lifetime);
            services.AddCharacterDatabase(configuration);

            var attempts = new ConcurrentDictionary<int, int>();
            services.AddScoped<ICharacterReputationStore>(provider =>
                new CountingReputationStore(new EfCharacterReputationStore(provider.GetRequiredService<CharacterDbContext>()), attempts));

            ServiceProvider built = services.BuildServiceProvider();
            built.GetRequiredService<CharacterOwnershipContext>().Current = firstToken;
            return new FencedDbFixture(path, plainOptions, built, lifetime, attempts, characterId, firstToken, injectLifetimeViaDiOnly);
        }

        public async Task<CharacterOwnershipToken> TakeoverAsync()
        {
            await using var db = new CharacterDbContext(_plainOptions);
            CharacterOwnershipToken second = (await new EfCharacterOwnershipStore(db)
                .TryTakeoverAsync(FirstToken, "node-b", Guid.NewGuid()))!;
            _services.GetRequiredService<CharacterOwnershipContext>().Current = second;
            return second;
        }

        public async Task RemoveCharacterRowAsync(int id)
        {
            await using var db = new CharacterDbContext(_plainOptions);
            await db.Characters.Where(c => c.Id == id).ExecuteDeleteAsync();
        }

        public async Task<CharacterReputationData> LoadAsync(int id)
        {
            await using var db = new CharacterDbContext(_plainOptions);
            return await new EfCharacterReputationStore(db).LoadAsync(id);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Queue.StopAsync();
            }
            catch (InvalidOperationException)
            {
            }

            await _services.DisposeAsync();
            File.Delete(_path);
        }

        private sealed class CountingReputationStore(ICharacterReputationStore inner, ConcurrentDictionary<int, int> attempts)
            : ICharacterReputationStore
        {
            public Task<CharacterReputationData> LoadAsync(int characterId, CancellationToken cancellationToken = default)
                => inner.LoadAsync(characterId, cancellationToken);

            public Task SaveFactionsAsync(int characterId, IReadOnlyList<CharacterReputationRow> upserts,
                CancellationToken cancellationToken = default, CharacterOwnershipToken? ownership = null)
            {
                attempts.AddOrUpdate(characterId, 1, (_, n) => n + 1);
                return inner.SaveFactionsAsync(characterId, upserts, cancellationToken, ownership);
            }

            public Task SaveWatchedFactionAsync(int characterId, int watchedFaction,
                CancellationToken cancellationToken = default, CharacterOwnershipToken? ownership = null)
            {
                attempts.AddOrUpdate(characterId, 1, (_, n) => n + 1);
                return inner.SaveWatchedFactionAsync(characterId, watchedFaction, cancellationToken, ownership);
            }

            public Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default)
                => inner.DeleteCharacterAsync(characterId, cancellationToken);

            public Task DeleteDeletedCharacterAsync(int characterId, CancellationToken cancellationToken = default)
            {
                attempts.AddOrUpdate(characterId, 1, (_, n) => n + 1);
                return inner.DeleteDeletedCharacterAsync(characterId, cancellationToken);
            }
        }
    }
}
