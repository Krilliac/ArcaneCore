using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Data.Characters.ServerMail;
using ArcaneCore.Data.Economy;
using ArcaneCore.Data.Stores;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.ServerMail;
using ArcaneCore.World.Economy;
using ArcaneCore.World.Items;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.ServerMail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.ServerMail;

/// <summary>
/// Server mail on a real economy store: a template's letters reach a character exactly once, whatever fails around the commit that
/// writes them with their mail_server_character record, and whatever logins run the delivery again.
/// </summary>
public sealed class ServerMailDeliveryTests
{
    private const uint Entry = 4000;
    private const uint Money = 1234;

    private static readonly ServerMailTemplate Welcome = new(1, 0, Money, Money, "Welcome", "Hello",
        [new ServerMailItem(Entry, 1)], [new ServerMailItem(Entry, 1)], []);

    private static readonly ServerMailTemplate Bundle = new(2, 0, Money, Money, "Bundle", "Two letters",
        [new ServerMailItem(Entry, 1), new ServerMailItem(Entry, 1)], [new ServerMailItem(Entry, 1), new ServerMailItem(Entry, 1)], []);

    [Fact]
    public async Task Letters_committed_before_a_crash_are_not_sent_again_after_the_restart()
    {
        await using var fixture = await Fixture.CreateAsync();
        // The letters commit, then the process loses the outcome (as if it died right after the commit): nothing it would have
        // written after the commit exists.
        fixture.Faults.FailAfterCommit = 1;
        fixture.Faults.FailReadback = true;
        await fixture.LoginAsync([Welcome]);
        Assert.Equal(1, await fixture.LettersAsync("Welcome"));

        fixture.Faults.FailReadback = false;
        await fixture.RestartAsync();
        await fixture.LoginAsync([Welcome]);

        Assert.Equal(1, await fixture.LettersAsync("Welcome"));
        Assert.Equal(1, await fixture.CountAsync<ItemInstanceRow>(i => i.ItemId == Entry));
        Assert.Equal(Money, await fixture.MoneyAsync("Welcome"));
        Assert.Equal([Welcome.Id], await fixture.SentAsync());
    }

    [Fact]
    public async Task A_failed_delivery_stays_owed_and_the_next_login_sends_it_once()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Faults.FailBeforeCommit = 1; // the storage write fails: nothing is committed
        await fixture.LoginAsync([Welcome]);
        Assert.Equal(0, await fixture.LettersAsync("Welcome"));
        Assert.Empty(await fixture.SentAsync());

        await fixture.LoginAsync([Welcome]); // retried at the next login
        await fixture.LoginAsync([Welcome]);

        Assert.Equal(1, await fixture.LettersAsync("Welcome"));
        Assert.Equal(1, await fixture.CountAsync<ItemInstanceRow>(i => i.ItemId == Entry));
        Assert.Equal([Welcome.Id], await fixture.SentAsync());
    }

    [Fact]
    public async Task A_relog_while_the_first_delivery_is_in_flight_sends_the_reward_once()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Faults.HoldFirstCommit = true;
        await fixture.StartLoginAsync([Welcome]); // first login: its commit is held before storage
        await fixture.Faults.FirstCommitEntered.Task.WaitAsync(Fixture.Budget);
        await fixture.StartLoginAsync([Welcome]); // relog: the sent records are read before the first commit lands
        fixture.Faults.ReleaseFirstCommit.TrySetResult(true);
        await fixture.SettleAsync();
        await fixture.LoginAsync([Welcome]);

        Assert.Equal(1, await fixture.LettersAsync("Welcome"));
        Assert.Equal(1, await fixture.CountAsync<ItemInstanceRow>(i => i.ItemId == Entry));
        Assert.Equal([Welcome.Id], await fixture.SentAsync());
    }

    [Fact]
    public async Task Every_letter_of_a_multi_item_template_commits_together_and_once()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.LoginAsync([Bundle]);
        await fixture.LoginAsync([Bundle]);

        Assert.Equal(2, await fixture.LettersAsync("Bundle")); // one item per letter
        Assert.Equal(2, await fixture.CountAsync<ItemInstanceRow>(i => i.ItemId == Entry));
        Assert.Equal(Money, await fixture.MoneyAsync("Bundle")); // the money rides on the first letter only
        Assert.Equal([Bundle.Id], await fixture.SentAsync());
    }

    [Fact]
    public async Task A_request_without_a_template_is_refused_and_sends_nothing()
    {
        // Without a template id there is no sent record to commit with the letters, so nothing may be sent.
        await using var fixture = await Fixture.CreateAsync();
        ServerMailRequest request = ServerMailRules.Letter(Welcome, fixture.CharacterId, Team.Alliance) with { TemplateId = 0 };
        Assert.False(await fixture.SendAsync(request));
        Assert.Equal(0, await fixture.LettersAsync("Welcome"));
        Assert.Empty(await fixture.SentAsync());

        Assert.True(await fixture.SendAsync(request with { TemplateId = Welcome.Id }));
        Assert.Equal(1, await fixture.LettersAsync("Welcome"));
        Assert.Equal([Welcome.Id], await fixture.SentAsync());
    }

    private sealed class NoFacts : IServerMailFacts
    {
        public uint Level => 60;
        public uint PlayedSeconds => 0;
        public Team Team => Team.Alliance;
        public Race Race => Race.Human;
        public Class Class => Class.Warrior;
        public (QuestStatus Status, bool Rewarded)? Quest(uint questId) => null;
        public ReputationRank? Rank(uint factionId) => null;
    }

    /// <summary>Faults injected around the real EF economy store's commit.</summary>
    internal sealed class StoreFaults
    {
        private int _commits;

        /// <summary>The next N commits throw before reaching storage.</summary>
        public int FailBeforeCommit { get; set; }

        /// <summary>The next N commits reach storage and commit, then throw (the acknowledgement is lost).</summary>
        public int FailAfterCommit { get; set; }

        /// <summary>The outcome read-back after a failed commit throws, so the outcome stays unknown.</summary>
        public bool FailReadback { get; set; }

        public bool HoldFirstCommit { get; set; }

        public TaskCompletionSource<bool> FirstCommitEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ReleaseFirstCommit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsFirstCommit() => Interlocked.Increment(ref _commits) == 1;
    }

    private sealed class FaultyEconomyStore(IEconomyStore inner, StoreFaults faults) : IEconomyStore
    {
        public async Task<EconomyCommitResult> CommitAsync(EconomyCommitRequest request, CancellationToken cancellationToken = default)
        {
            if (faults.HoldFirstCommit && faults.IsFirstCommit())
            {
                faults.FirstCommitEntered.TrySetResult(true);
                await faults.ReleaseFirstCommit.Task.WaitAsync(cancellationToken);
            }

            if (faults.FailBeforeCommit > 0)
            {
                faults.FailBeforeCommit--;
                throw new InvalidOperationException("injected: the commit failed before storage");
            }

            EconomyCommitResult result = await inner.CommitAsync(request, cancellationToken);
            if (faults.FailAfterCommit > 0)
            {
                faults.FailAfterCommit--;
                throw new InvalidOperationException($"injected: the commit returned {result} but the acknowledgement was lost");
            }

            return result;
        }

        public Task<bool> IsCommittedAsync(Guid operationId, CancellationToken cancellationToken = default)
            => faults.FailReadback ? throw new InvalidOperationException("injected: the outcome cannot be read") : inner.IsCommittedAsync(operationId, cancellationToken);

        public Task<IReadOnlyList<MailRecord>> GetMailsAsync(int receiverId, CancellationToken cancellationToken = default) => inner.GetMailsAsync(receiverId, cancellationToken);

        public Task<IReadOnlyList<MailRecord>> GetExpiredMailsAsync(long now, int max, CancellationToken cancellationToken = default) => inner.GetExpiredMailsAsync(now, max, cancellationToken);

        public Task<IReadOnlyList<MailRecord>> GetMailsInvolvingAsync(int characterId, CancellationToken cancellationToken = default) => inner.GetMailsInvolvingAsync(characterId, cancellationToken);

        public Task<string?> GetItemTextAsync(uint itemTextId, CancellationToken cancellationToken = default) => inner.GetItemTextAsync(itemTextId, cancellationToken);

        public Task<IReadOnlyList<AuctionRecord>> GetAuctionsAsync(CancellationToken cancellationToken = default) => inner.GetAuctionsAsync(cancellationToken);

        public Task<IReadOnlyDictionary<uint, ItemInstanceData>> GetEscrowItemsAsync(IReadOnlyCollection<uint> itemGuids, CancellationToken cancellationToken = default) => inner.GetEscrowItemsAsync(itemGuids, cancellationToken);

        public Task<AuctionSnapshot> GetAuctionSnapshotAsync(AuctionSnapshotFilter filter, CancellationToken cancellationToken = default) => inner.GetAuctionSnapshotAsync(filter, cancellationToken);

        public Task<EconomyIdSeed> GetIdSeedAsync(CancellationToken cancellationToken = default) => inner.GetIdSeedAsync(cancellationToken);
    }

    private sealed class Templates(IReadOnlyList<ItemTemplate> templates) : IItemTemplateSource
    {
        public Task<IReadOnlyList<ItemTemplate>> LoadTemplatesAsync(CancellationToken cancellationToken = default) => Task.FromResult(templates);

        public Task<IReadOnlyList<StartingItem>> LoadStartingItemsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<StartingItem>>([]);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);
        private readonly string _path;
        private readonly DbContextOptions<CharacterDbContext> _options;
        private ServiceProvider _services = null!;
        private WorldRuntime _world = null!;

        private Fixture(string path, DbContextOptions<CharacterDbContext> options, int characterId)
        {
            _path = path;
            _options = options;
            CharacterId = characterId;
        }

        public int CharacterId { get; }

        public StoreFaults Faults { get; } = new();

        public EconomyFeature Economy { get; private set; } = null!;

        public ServerMailFeature Feature { get; private set; } = null!;

        public static async Task<Fixture> CreateAsync()
        {
            string path = Path.Combine(Path.GetTempPath(), $"arcanecore-servermail-{Guid.NewGuid():N}.db");
            var options = new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
            int id;
            await using (var db = new CharacterDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                id = (await new EfCharacterStore(db).CreateAsync(new CharacterRecord { AccountId = 1, Name = "Receiver", Race = 1, Class = 1, Level = 20 })).Id;
            }

            var fixture = new Fixture(path, options, id);
            fixture.Open();
            return fixture;
        }

        /// <summary>A process start on the same database: a new world, economy and server mail feature.</summary>
        private void Open()
        {
            ItemTemplate template = new() { Entry = Entry, Name = "Reward", Quality = 2, Class = 4, Stackable = 1, MaxDurability = 30 };
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            services.AddLogging();
            services.AddSingleton(Faults);
            services.AddScoped(_ => new CharacterDbContext(_options));
            services.AddScoped<IEconomyStore>(provider => new FaultyEconomyStore(new EfEconomyStore(provider.GetRequiredService<CharacterDbContext>()),
                provider.GetRequiredService<StoreFaults>()));
            services.AddScoped<IItemStore>(provider => new EfItemStore(provider.GetRequiredService<CharacterDbContext>()));
            services.AddScoped<IServerMailStore>(provider => new EfServerMailStore(provider.GetRequiredService<CharacterDbContext>()));
            services.AddSingleton<IItemTemplateSource>(new Templates([template]));
            services.AddSingleton(provider => new CharacterSaveQueue(provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<CharacterSaveQueue>.Instance));
            services.AddSingleton<ItemsFeature>();
            services.AddSingleton(provider => new EconomyFeature(provider, provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<EconomyFeature>.Instance));
            services.AddSingleton(provider => new ServerMailFeature(provider, provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<ServerMailFeature>.Instance));
            _services = services.BuildServiceProvider();
            _world = new WorldRuntime(new WorldRuntimeOptions { TickIntervalMs = 5, AutosaveIntervalMs = 0 },
                _services.GetRequiredService<CharacterSaveQueue>(), NullLogger<WorldRuntime>.Instance);
            _services.GetRequiredService<ItemsFeature>().EnsureLoadedAsync().GetAwaiter().GetResult();
            Economy = _services.GetRequiredService<EconomyFeature>();
            Economy.Options.ExpirySweepSeconds = 0;
            Economy.Attach(_world);
            Feature = _services.GetRequiredService<ServerMailFeature>();
            _world.Start();
        }

        /// <summary>What a login does (ServerMailFeature.OnLoggedIn): read the sent records, then mail every owed template; then settle.</summary>
        public async Task LoginAsync(IReadOnlyList<ServerMailTemplate> templates)
        {
            await StartLoginAsync(templates);
            await SettleAsync();
        }

        /// <summary>A login without waiting for its economy operations.</summary>
        public async Task StartLoginAsync(IReadOnlyList<ServerMailTemplate> templates)
        {
            IReadOnlyCollection<uint> sent;
            using (IServiceScope scope = _services.CreateScope())
            {
                sent = await scope.ServiceProvider.GetRequiredService<IServerMailStore>().GetSentAsync(CharacterId);
            }

            await _world.InvokeAsync(() =>
            {
                Feature.Send(Economy, CharacterId, Team.Alliance, ServerMailRules.Owed(templates, sent, new NoFacts()).ToList());
                return true;
            }).WaitAsync(Budget);
        }

        /// <summary>Send one request on the world thread and wait for its outcome.</summary>
        public async Task<bool> SendAsync(ServerMailRequest request)
        {
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await _world.InvokeAsync(() =>
            {
                Economy.SendServerMail(request, ok => done.TrySetResult(ok));
                return true;
            }).WaitAsync(Budget);
            return await done.Task.WaitAsync(Budget);
        }

        /// <summary>Wait for the economy operations (the letters and their sent record are one of them).</summary>
        public Task SettleAsync() => Economy.DrainAsync().WaitAsync(Budget);

        /// <summary>A restart: stop this process's world and features and start new ones on the same database.</summary>
        public async Task RestartAsync()
        {
            await CloseAsync();
            Open();
        }

        public async Task<int> CountAsync<T>(System.Linq.Expressions.Expression<Func<T, bool>>? where = null)
            where T : class
        {
            await using var db = new CharacterDbContext(_options);
            IQueryable<T> set = db.Set<T>().AsNoTracking();
            return await (where is null ? set : set.Where(where)).CountAsync();
        }

        public Task<int> LettersAsync(string subject) => CountAsync<MailRow>(m => m.ReceiverId == CharacterId && m.Subject == subject);

        public async Task<long> MoneyAsync(string subject)
        {
            await using var db = new CharacterDbContext(_options);
            List<uint> money = await db.Set<MailRow>().AsNoTracking().Where(m => m.ReceiverId == CharacterId && m.Subject == subject)
                .Select(m => m.Money).ToListAsync();
            return money.Sum(m => (long)m);
        }

        public async Task<IReadOnlyCollection<uint>> SentAsync()
        {
            await using var db = new CharacterDbContext(_options);
            return await new EfServerMailStore(db).GetSentAsync(CharacterId);
        }

        private async Task CloseAsync()
        {
            await Economy.DisposeAsync();
            _world.Dispose();
            await _services.GetRequiredService<CharacterSaveQueue>().StopAsync();
            await _services.DisposeAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await CloseAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                File.Delete(_path);
            }
            catch (IOException)
            {
            }
        }
    }
}
