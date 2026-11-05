using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Cluster;
using ArcaneCore.Data.Instances;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Social;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Kernel.Instances;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.Kernel.Social;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Instances;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Reputation;
using ArcaneCore.World.Social;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Persistence;

/// <summary>
/// The write queues that decide on a fence refusal must all recognise one that EF wrapped (a <c>DbUpdateException</c> around what a commit-time check or an interceptor threw): the instance queue and the
/// social queue used to match the exact exception type while the reputation queue walked the inner exceptions, so a wrapped refusal was retained and retried as an ordinary failure by two of the three. The
/// instance and social cases run the real <see cref="EfInstanceStore"/> and <see cref="EfSocialStore"/> (a realm lease the process no longer holds refuses the write with <c>RealmLeaseFenceLostException</c>);
/// the wrapped shape rethrows that refusal inside a <see cref="DbUpdateException"/>. A refusal is terminal while the source owns its writes, and dropped and counted once the handoff has sealed the source.
/// </summary>
public sealed class WrappedOwnershipRefusalTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    public enum QueueKind
    {
        Instance,
        Social,
        Reputation,
    }

    public static TheoryData<QueueKind, bool> Cases => new()
    {
        { QueueKind.Instance, false },
        { QueueKind.Instance, true },
        { QueueKind.Social, false },
        { QueueKind.Social, true },
        { QueueKind.Reputation, false },
        { QueueKind.Reputation, true },
    };

    private static string QueueName(QueueKind kind) => kind switch
    {
        QueueKind.Instance => "InstanceWriteQueue",
        QueueKind.Social => "SocialWriteQueue",
        _ => "ReputationWriteQueue",
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ARefusalOfTheSource_IsTerminal_WhetherOrNotEfWrappedIt(QueueKind kind, bool wrapped)
    {
        await using var env = await Env.CreateAsync(wrapped);
        var fatal = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        IWriteQueue queue = env.Start(kind, error => fatal.TrySetResult(error));

        queue.Write();
        Exception failure = await fatal.Task.WaitAsync(Budget);

        Assert.NotNull(OwnershipRefusal.Find(failure));
        Assert.Equal(0, env.Retirement.DroppedCount(QueueName(kind)));
        await queue.StopExpectingFailureAsync();
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ARefusalOfASealedSource_IsDroppedAndCounted_WhetherOrNotEfWrappedIt(QueueKind kind, bool wrapped)
    {
        await using var env = await Env.CreateAsync(wrapped);
        var fatal = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        IWriteQueue queue = env.Start(kind, error => fatal.TrySetResult(error));
        env.Retirement.Seal(); // the handoff flushed and has not decided: the takeover may land at any moment

        queue.Write();
        await WorldTestHost.WaitForAsync(() => env.Retirement.DroppedCount(QueueName(kind)) == 1, "the refused write to be dropped and counted");

        Assert.False(fatal.Task.IsCompleted);
        Assert.False(queue.IsTerminal);
        await queue.StopCleanAsync(); // must not throw
    }

    [Fact]
    public void Find_ReturnsTheRefusal_ItselfWrappedOrInsideAnAggregate_AndNullOtherwise()
    {
        var refusal = new CharacterOwnershipRejectedException("world", 7);

        Assert.Same(refusal, OwnershipRefusal.Find(refusal));
        Assert.Same(refusal, OwnershipRefusal.Find(new DbUpdateException("wrapped", refusal)));
        Assert.Same(refusal, OwnershipRefusal.Find(new InvalidOperationException("outer", new DbUpdateException("middle", refusal))));
        Assert.Same(refusal, OwnershipRefusal.Find(new AggregateException(new TimeoutException(), new DbUpdateException("wrapped", refusal))));
        Assert.Null(OwnershipRefusal.Find(new InvalidOperationException("not a refusal", new IOException("neither"))));
        Assert.Null(OwnershipRefusal.Find(new CharacterClaimRejectedException(5, "world")));
        Assert.Null(OwnershipRefusal.Find(null));
        Assert.True(OwnershipRefusal.TryFind(new DbUpdateException("wrapped", refusal), out CharacterOwnershipRejectedException? found));
        Assert.Same(refusal, found);
        Assert.False(OwnershipRefusal.TryFind(new IOException(), out _));
    }

    private interface IWriteQueue
    {
        bool IsTerminal { get; }

        void Write();

        Task StopCleanAsync();

        Task StopExpectingFailureAsync();
    }

    private sealed class Env : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly ServiceProvider _services;

        private Env(string directory, ServiceProvider services)
        {
            _directory = directory;
            _services = services;
        }

        public CharacterRetirementRegistry Retirement { get; } = new();

        public static async Task<Env> CreateAsync(bool wrapped)
        {
            string directory = Path.Combine(Path.GetTempPath(), "arcanecore-wrappedrefusal-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            DbContextOptions<CharacterDbContext> options = new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory, "characters.sqlite"), Pooling = false, DefaultTimeout = 5,
            }.ToString()).Options;
            await using (var setup = new CharacterDbContext(options))
            {
                await SchemaBootstrapper.EnsureAsync(setup, CharacterDbContext.Schema, cancellationToken: default);
            }

            var fences = new StaleLeases();
            var services = new ServiceCollection();
            services.AddScoped(_ => new CharacterDbContext(options));
            services.AddScoped<IInstanceStore>(sp => new EfWrappingInstanceStore(new EfInstanceStore(sp.GetRequiredService<CharacterDbContext>(), fences), wrapped));
            services.AddScoped<ISocialStore>(sp => new EfWrappingSocialStore(new EfSocialStore(sp.GetRequiredService<CharacterDbContext>(), fences), wrapped));
            services.AddScoped<ICharacterReputationStore>(_ => new RefusingReputationStore(wrapped));
            return new Env(directory, services.BuildServiceProvider());
        }

        public IWriteQueue Start(QueueKind kind, Action<Exception> onFatal)
        {
            IServiceScopeFactory scopes = _services.GetRequiredService<IServiceScopeFactory>();
            switch (kind)
            {
                case QueueKind.Instance:
                {
                    var queue = new InstanceWriteQueue(scopes, NullLogger.Instance)
                    {
                        RetryDelay = TimeSpan.Zero,
                        RetainedRetryInterval = Timeout.InfiniteTimeSpan,
                        Retirement = Retirement,
                    };
                    queue.FatalFailure += onFatal;
                    queue.Start();
                    return new InstanceAdapter(queue);
                }

                case QueueKind.Social:
                {
                    var queue = new SocialWriteQueue(scopes, NullLogger.Instance, new SocialWriteQueueOptions { RetryDelayMs = 1 })
                    {
                        RetainedRetryInterval = Timeout.InfiniteTimeSpan,
                        Retirement = Retirement,
                    };
                    queue.FatalFailure += onFatal;
                    queue.Start();
                    return new SocialAdapter(queue);
                }

                default:
                {
                    var queue = new ReputationWriteQueue(scopes, NullLogger.Instance)
                    {
                        RetainedRetryInterval = Timeout.InfiniteTimeSpan,
                        Retirement = Retirement,
                    };
                    queue.FatalFailure += onFatal;
                    queue.Start();
                    return new ReputationAdapter(queue);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class InstanceAdapter(InstanceWriteQueue queue) : IWriteQueue
    {
        public bool IsTerminal => queue.TerminalFailure is not null;

        public void Write() => queue.EnqueueInstanceSaveForTest(900, 409, 1_700_000_000);

        public Task StopCleanAsync() => queue.StopAsync().WaitAsync(Budget);

        public async Task StopExpectingFailureAsync() => await Assert.ThrowsAsync<InvalidOperationException>(() => queue.StopAsync().WaitAsync(Budget));
    }

    private sealed class SocialAdapter(SocialWriteQueue queue) : IWriteQueue
    {
        public bool IsTerminal => queue.TerminalFailure is not null;

        public void Write() => queue.SaveGuild(new GuildData(900, "Guild", 1, "", "", 0, 0, 0, 0, 0, 0, [], []));

        public Task StopCleanAsync() => queue.StopAsync().WaitAsync(Budget);

        public async Task StopExpectingFailureAsync() => await Assert.ThrowsAsync<InvalidOperationException>(() => queue.StopAsync().WaitAsync(Budget));
    }

    private sealed class ReputationAdapter(ReputationWriteQueue queue) : IWriteQueue
    {
        // The reputation queue keeps a refused write retained and says so through FatalFailure; it has no separate terminal property.
        public bool IsTerminal => queue.HasRetainedFailure(1);

        public void Write() => queue.SaveWatchedFaction(1, 3, new CharacterOwnershipToken("world", 1, Guid.NewGuid()));

        public Task StopCleanAsync() => queue.StopAsync().WaitAsync(Budget);

        public async Task StopExpectingFailureAsync() => await Assert.ThrowsAsync<InvalidOperationException>(() => queue.StopAsync().WaitAsync(Budget));
    }

    /// <summary>A process that holds a realm lease in its own memory that the lease table no longer has: the stores' commit-time check refuses every write with <see cref="RealmLeaseFenceLostException"/>.</summary>
    private sealed class StaleLeases : IRealmLeaseFenceSource
    {
        private readonly Guid _incarnation = Guid.NewGuid();

        public RealmLeaseFence? FenceFor(string scope)
            => scope is RealmAuthorityScopes.Instances or RealmAuthorityScopes.Social ? new RealmLeaseFence(scope, "node-a", _incarnation, 1) : null;
    }

    private static Exception Shape(Exception refusal, bool wrapped) => wrapped ? new DbUpdateException("the save failed", refusal) : refusal;

    /// <summary>The real instance store; a refusal it raises is rethrown inside a <see cref="DbUpdateException"/> when <c>wrapped</c>.</summary>
    private sealed class EfWrappingInstanceStore(IInstanceStore inner, bool wrapped) : IInstanceStore
    {
        public Task<InstanceStoreSnapshot> LoadAsync(CancellationToken cancellationToken = default) => inner.LoadAsync(cancellationToken);

        public async Task SaveInstanceAsync(InstanceRecord instance, CancellationToken cancellationToken = default)
        {
            try
            {
                await inner.SaveInstanceAsync(instance, cancellationToken).ConfigureAwait(false);
            }
            catch (CharacterOwnershipRejectedException refusal)
            {
                throw Shape(refusal, wrapped);
            }
        }

        public Task SaveInstanceDataAsync(uint instanceId, string data, CancellationToken cancellationToken = default) => inner.SaveInstanceDataAsync(instanceId, data, cancellationToken);

        public Task DeleteInstanceAsync(uint instanceId, CancellationToken cancellationToken = default) => inner.DeleteInstanceAsync(instanceId, cancellationToken);

        public Task SaveBindAsync(CharacterInstanceBindRecord bind, CancellationToken cancellationToken = default) => inner.SaveBindAsync(bind, cancellationToken);

        public Task DeleteBindAsync(int characterId, uint instanceId, CancellationToken cancellationToken = default) => inner.DeleteBindAsync(characterId, instanceId, cancellationToken);

        public Task SaveResetTimeAsync(InstanceResetRecord reset, CancellationToken cancellationToken = default) => inner.SaveResetTimeAsync(reset, cancellationToken);

        public Task SaveLastInstanceAsync(CharacterLastInstanceRecord last, CancellationToken cancellationToken = default) => inner.SaveLastInstanceAsync(last, cancellationToken);

        public Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default) => inner.DeleteCharacterAsync(characterId, cancellationToken);
    }

    /// <summary>The real social store; a refusal it raises is rethrown inside a <see cref="DbUpdateException"/> when <c>wrapped</c>.</summary>
    private sealed class EfWrappingSocialStore(ISocialStore inner, bool wrapped) : ISocialStore
    {
        public Task<IReadOnlyList<SocialEntry>> GetSocialAsync(int characterId, CancellationToken cancellationToken = default) => inner.GetSocialAsync(characterId, cancellationToken);

        public Task SetSocialAsync(int characterId, int otherId, SocialFlags flags, CancellationToken cancellationToken = default) => inner.SetSocialAsync(characterId, otherId, flags, cancellationToken);

        public Task<IReadOnlyList<GuildData>> GetGuildsAsync(CancellationToken cancellationToken = default) => inner.GetGuildsAsync(cancellationToken);

        public async Task SaveGuildAsync(GuildData guild, CancellationToken cancellationToken = default)
        {
            try
            {
                await inner.SaveGuildAsync(guild, cancellationToken).ConfigureAwait(false);
            }
            catch (CharacterOwnershipRejectedException refusal)
            {
                throw Shape(refusal, wrapped);
            }
        }

        public Task DeleteGuildAsync(int guildId, CancellationToken cancellationToken = default) => inner.DeleteGuildAsync(guildId, cancellationToken);

        public Task PurgeCharacterAsync(int characterId, CancellationToken cancellationToken = default) => inner.PurgeCharacterAsync(characterId, cancellationToken);
    }

    /// <summary>A reputation store whose every write is refused by the fence, as the real one is for a stale token.</summary>
    private sealed class RefusingReputationStore(bool wrapped) : ICharacterReputationStore
    {
        public Task<CharacterReputationData> LoadAsync(int characterId, CancellationToken cancellationToken = default) => Task.FromResult(CharacterReputationData.Empty);

        public Task SaveFactionsAsync(int characterId, IReadOnlyList<CharacterReputationRow> upserts, CancellationToken cancellationToken = default, CharacterOwnershipToken? ownership = null)
            => Task.FromException(Shape(new CharacterOwnershipRejectedException("world", 1), wrapped));

        public Task SaveWatchedFactionAsync(int characterId, int watchedFaction, CancellationToken cancellationToken = default, CharacterOwnershipToken? ownership = null)
            => Task.FromException(Shape(new CharacterOwnershipRejectedException("world", 1), wrapped));

        public Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteDeletedCharacterAsync(int characterId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
