using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Cluster;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Data.Characters.Playerbots;
using ArcaneCore.Data.Characters.WorldState;
using ArcaneCore.Data.Creatures;
using ArcaneCore.Data.Economy;
using ArcaneCore.Data.GameObjects;
using ArcaneCore.Data.Gm;
using ArcaneCore.Data.Honor;
using ArcaneCore.Data.Instances;
using ArcaneCore.Data.Loot;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Social;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Gm;
using ArcaneCore.Kernel.Honor;
using ArcaneCore.Kernel.Instances;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Kernel.WorldData.WorldState;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.Data.Tests.Cluster;

/// <summary>
/// Writer-lock design step C6 (sections 2b and 4), store level: every realm-singleton store write and every boot sweep that assumes sole ownership is gated on the lease of its scope.
/// For each site, on every available provider: a node that does not hold the lease is refused (a write: <see cref="RealmLeaseNotHeldException"/>) or skips (a sweep) and changes nothing;
/// the holder writes; a node whose lease was lost (the keeper reports it no longer holds the scope while the stores still see the old token) is refused like a non-holder; and where the
/// store verifies the lease row inside the write's transaction, a holder whose lease was taken over is refused with <see cref="RealmLeaseFenceLostException"/>.
/// </summary>
public sealed class LeaseHeldStoreTests : IAsyncLifetime
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] Scopes =
    [RealmAuthorityScopes.Social, RealmAuthorityScopes.Economy, RealmAuthorityScopes.Instances, RealmAuthorityScopes.Maintenance, RealmAuthorityScopes.WorldState];

    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private sealed class Source(Func<string, RealmLeaseFence?> fence, Func<string, bool> holds) : IRealmLeaseFenceSource
    {
        public RealmLeaseFence? FenceFor(string scope) => fence(scope);

        public bool Holds(string scope) => holds(scope);
    }

    private sealed record Rig(
        DatabaseConnectionOptions Connection, int Character, int OtherCharacter, IRealmLeaseFenceSource NonHolder, IRealmLeaseFenceSource Holder, IRealmLeaseFenceSource Lost);

    /// <summary>
    /// A characters database with two characters and a lease row per scope. The holder is node A at fencing token 1; with <paramref name="takeOver"/> node B has taken every scope over
    /// (token 2), so A's source is the stale holder. The lost source carries A's fences but reports that it holds nothing, as the keeper does after a loss.
    /// </summary>
    private async Task<Rig> CreateAsync(DatabaseProvider provider, bool takeOver = false)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        int character;
        int other;
        await using (CharacterDbContext setup = TestContexts.Create<CharacterDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(setup, CharacterDbContext.Schema);
            var characters = new EfCharacterStore(setup);
            character = (await characters.CreateAsync(new CharacterRecord { AccountId = 1, Name = "Leader" })).Id;
            other = (await characters.CreateAsync(new CharacterRecord { AccountId = 1, Name = "Second" })).Id;
        }

        var services = new ServiceCollection();
        services.AddScoped(_ => TestContexts.Create<CharacterDbContext>(connection));
        var leases = new EfRealmAuthorityLeaseStore(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
        Guid incarnationA = Guid.NewGuid();
        var fences = new Dictionary<string, RealmLeaseFence>();
        foreach (string scope in Scopes)
        {
            AuthorityLeaseResult first = leases.TryAcquire(scope, "node-a", incarnationA, Ttl, T0);
            Assert.Equal(AuthorityLeaseOutcome.Granted, first.Outcome);
            fences[scope] = new RealmLeaseFence(scope, "node-a", incarnationA, first.Lease!.FencingToken);
            if (takeOver)
            {
                AuthorityLeaseResult second = leases.TryAcquire(scope, "node-b", Guid.NewGuid(), Ttl, T0 + Ttl + TimeSpan.FromSeconds(1));
                Assert.Equal(AuthorityLeaseOutcome.Granted, second.Outcome);
            }
        }

        RealmLeaseFence? Fence(string scope) => fences.TryGetValue(scope, out RealmLeaseFence f) ? f : null;
        return new Rig(
            connection, character, other,
            new Source(_ => null, _ => false),
            new Source(Fence, scope => fences.ContainsKey(scope)),
            new Source(Fence, _ => false));
    }

    /// <summary>One gated site. <c>Sweep</c>: a boot cleanup that skips quietly on a non-holder; otherwise a write that is refused. <c>Fenced</c>: the store also verifies the lease row in the write's transaction.</summary>
    private sealed record Site(
        string Name, bool Sweep, bool Fenced,
        Func<CharacterDbContext, Task> Seed,
        Func<CharacterDbContext, IRealmLeaseFenceSource, Task> Act,
        Func<CharacterDbContext, Task<bool>> Done);

    private static Task Nothing(CharacterDbContext db) => Task.CompletedTask;

    private static readonly Guid Bot = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static GuildData Guild(int id, int leader) => new(id, $"Guild{id}", leader, "motd", "info", 1_700_000_000, 1, 2, 3, 4, 5,
        [new GuildRankData(0, "Guild Master", 0x1FF), new GuildRankData(1, "Member", 0x43)],
        [new GuildMemberData(leader, 0, string.Empty, string.Empty, 1, 12, 0)]);

    private static GroupRecord Group(uint id, int leader) => new(id, leader, 0, 0, 0, false, new ulong[8], [new GroupMemberRecord(leader, 0, false)]);

    private static GmTicketRecord Ticket(int id, int character)
        => new(id, character, "help", 1, 0, 1.5f, -2.25f, 3.75f, 1000, 1100, GmTicketStatus.Open, string.Empty, string.Empty, 0);

    private static readonly MailRecord ExpiredMail = new()
    {
        Id = 600, MessageType = MailMessageType.Normal, SenderId = 10, ReceiverId = 6, Subject = "old", Money = 10, DeliverTime = 1_000, ExpireTime = 2_000,
    };

    private static List<Site> Sites(Rig rig)
    {
        int leader = rig.Character;
        int second = rig.OtherCharacter;
        return
        [
            // ---- 2(b) instances
            new("instances: raid reset time", false, true, Nothing,
                (db, src) => new EfInstanceStore(db, src).SaveResetTimeAsync(new InstanceResetRecord(30, 1_900_000_000)),
                db => db.Set<InstanceResetRow>().AnyAsync(r => r.MapId == 30)),
            new("instances: group instance bind save", false, true, Nothing,
                (db, src) => new EfInstanceStore(db, src).SaveGroupBindAsync(new GroupInstanceBindRecord(leader, 5, true)),
                db => db.Set<GroupInstanceRow>().AnyAsync(r => r.LeaderCharacterId == leader && r.InstanceId == 5)),
            new("instances: group instance bind delete", false, true,
                db => new EfInstanceStore(db).SaveGroupBindAsync(new GroupInstanceBindRecord(second, 6, true)),
                (db, src) => new EfInstanceStore(db, src).DeleteGroupBindAsync(second, 6),
                async db => !await db.Set<GroupInstanceRow>().AnyAsync(r => r.LeaderCharacterId == second && r.InstanceId == 6)),

            // ---- section 4: boot sweeps
            new("instances: bind cleanup in LoadAsync", true, true,
                async db =>
                {
                    db.Set<CharacterInstanceRow>().Add(new CharacterInstanceRow { CharacterId = 777_001, InstanceId = 8, Permanent = false });
                    await db.SaveChangesAsync();
                },
                (db, src) => new EfInstanceStore(db, src).LoadAsync(),
                async db => !await db.Set<CharacterInstanceRow>().AnyAsync(r => r.CharacterId == 777_001)),
            new("respawn: orphan creature_respawn delete", true, true,
                async db =>
                {
                    db.Set<CreatureRespawnRow>().Add(new CreatureRespawnRow { InstanceId = 9, SpawnGuid = 1, MapId = 33, RespawnTime = 4_000_000_000 });
                    await db.SaveChangesAsync();
                },
                (db, src) => new EfCreatureRespawnStore(db, src).LoadAsync(1),
                async db => !await db.Set<CreatureRespawnRow>().AnyAsync(r => r.InstanceId == 9)),
            new("respawn: orphan gameobject_respawn delete", true, true,
                async db =>
                {
                    db.Set<GameObjectRespawnRow>().Add(new GameObjectRespawnRow { InstanceId = 9, SpawnGuid = 1, MapId = 33, RespawnTime = 4_000_000_000 });
                    await db.SaveChangesAsync();
                },
                (db, src) => new EfGameObjectRespawnStore(db, src).LoadAsync(1),
                async db => !await db.Set<GameObjectRespawnRow>().AnyAsync(r => r.InstanceId == 9)),
            new("loot: orphan chest state purge", true, true,
                async db =>
                {
                    db.Set<LootStateRow>().Add(new LootStateRow { InstanceId = 11, SpawnGuid = 1, SourceEntry = 1, LootOwner = 0, Generation = 0, Consumed = false, RespawnAt = 0 });
                    await db.SaveChangesAsync();
                },
                (db, src) => new EfLootStateStore(db, src).LoadInstanceStatesAsync(),
                async db => !await db.Set<LootStateRow>().AnyAsync(r => r.InstanceId == 11)),
            new("items: orphaned item loot sweep", true, true,
                async db =>
                {
                    db.Set<ItemLootRow>().Add(new ItemLootRow { ItemGuid = 5000, Slot = 0, ItemId = 1, Amount = 1 });
                    await db.SaveChangesAsync();
                },
                (db, src) => new EfItemLootMaintenance(db, src).DeleteOrphanedLootAsync(),
                async db => !await db.Set<ItemLootRow>().AnyAsync(r => r.ItemGuid == 5000)),

            // ---- 2(b) economy expiry operations
            new("economy: expiry commit (participant-less DeleteMail)", false, true,
                async db =>
                {
                    var row = new MailRow();
                    row.CopyFrom(ExpiredMail);
                    db.Add(row);
                    await db.SaveChangesAsync();
                },
                async (db, src) => await new EfEconomyStore(db, src).CommitAsync(new EconomyCommitRequest(Guid.NewGuid(), [], [new DeleteMail(ExpiredMail)])),
                async db => !await db.Set<MailRow>().AnyAsync(m => m.Id == 600)),

            // ---- 2(b) honor weekly maintenance
            new("honor: weekly maintenance batch", false, true, Nothing,
                (db, src) => new EfHonorStore(db, null, src).ApplyMaintenanceAsync(new HonorMaintenanceBatch([], 0, new HonorMaintenanceState(200, 207, false))),
                async db => (await new EfHonorStore(db).GetMaintenanceAsync())?.LastDay == 200),
            new("honor: first-start maintenance state", false, false, Nothing,
                (db, src) => new EfHonorStore(db, null, src).SaveMaintenanceAsync(new HonorMaintenanceState(300, 307, false)),
                async db => (await new EfHonorStore(db).GetMaintenanceAsync())?.LastDay == 300),

            // ---- 2(b) playerbot realm ledgers (maintenance lease)
            new("playerbots: relation ledger", false, false,
                async db =>
                {
                    db.Set<PlayerbotRelationRow>().Add(new PlayerbotRelationRow { BotId = Bot, CharacterId = 1, OtherGuid = 2 });
                    await db.SaveChangesAsync();
                },
                (db, src) => new EfPlayerbotRelationStore(db, src).ReplaceAsync(Bot, []),
                async db => !await db.Set<PlayerbotRelationRow>().AnyAsync(r => r.BotId == Bot)),
            new("playerbots: trade record ledger", false, false,
                async db =>
                {
                    db.Set<PlayerbotTradeRecordRow>().Add(new PlayerbotTradeRecordRow { BotId = Bot, CharacterId = 1, OtherGuid = 2 });
                    await db.SaveChangesAsync();
                },
                (db, src) => new EfPlayerbotTradeRecordStore(db, src).ReplaceAsync(Bot, []),
                async db => !await db.Set<PlayerbotTradeRecordRow>().AnyAsync(r => r.BotId == Bot)),
            new("playerbots: trade counters", false, false,
                async db =>
                {
                    db.Set<PlayerbotTradeCounterRow>().Add(new PlayerbotTradeCounterRow { Kind = 0, BotId = Bot, Key = 1, Stamp = 1, Amount = 1 });
                    await db.SaveChangesAsync();
                },
                (db, src) => new EfPlayerbotTradeCounterStore(db, src).ReplaceAllAsync([]),
                async db => !await db.Set<PlayerbotTradeCounterRow>().AnyAsync()),
            new("playerbots: population upsert", false, false, Nothing,
                (db, src) => new EfPlayerbotPopulationStore(db, src).UpsertAsync(new PlayerbotPopulationMember(Bot, leader, 12, 1, 1_000, 1_000)),
                db => db.Set<PlayerbotPopulationMemberRow>().AnyAsync(r => r.BotId == Bot)),
            new("playerbots: population delete", false, false,
                db => new EfPlayerbotPopulationStore(db).UpsertAsync(new PlayerbotPopulationMember(Bot, second, 12, 1, 1_000, 1_000)),
                (db, src) => new EfPlayerbotPopulationStore(db, src).DeleteAsync(Bot),
                async db => !await db.Set<PlayerbotPopulationMemberRow>().AnyAsync(r => r.BotId == Bot)),

            // ---- 2(b) world state (worldstate lease)
            new("worldstate: game event status", false, false, Nothing,
                (db, src) => new EfGameEventStatusStore(db, src).ReplaceActiveAsync([5]),
                db => db.Set<GameEventStatusRow>().AnyAsync(r => r.Event == 5)),
            new("worldstate: war effort phase", false, false, Nothing,
                (db, src) => new EfWarEffortStateStore(db, src).SetPhaseAsync(WarEffortPhase.Gathering, 0),
                db => db.Set<WarEffortPhaseRow>().AnyAsync(r => r.Id == 1)),
            new("worldstate: war effort boss death", false, false, Nothing,
                (db, src) => new EfWarEffortStateStore(db, src).MarkBossKilledAsync(0),
                db => db.Set<WarEffortBossKillRow>().AnyAsync(r => r.BossId == 0)),
            new("worldstate: scourge invasion start", false, false, Nothing,
                (db, src) => new EfScourgeInvasionStateStore(db, src).StartAsync(),
                db => db.Set<ScourgeInvasionStateRow>().AnyAsync(r => r.Id == 1)),
            new("worldstate: elemental invasion", false, false, Nothing,
                (db, src) => new EfElementalInvasionStore(db, src).SaveAsync(new ElementalInvasionState(1, 2, 3)),
                db => db.Set<ElementalInvasionRow>().AnyAsync(r => r.Element == 1)),
            new("worldstate: silithyst", false, false, Nothing,
                (db, src) => new EfSilithystStore(db, src).SaveAsync(new SilithystState(1, 2, 3)),
                db => db.Set<SilithystRow>().AnyAsync(r => r.Id == 1)),
            new("worldstate: nightmare dragons", false, false, Nothing,
                (db, src) => new EfNightmareDragonsStore(db, src).SaveAsync(new NightmareDragonsState([1u, 2u, 3u, 4u], 0, 0, false, 0)),
                db => db.Set<NightmareDragonsRow>().AnyAsync(r => r.Id == 1)),
            new("worldstate: fishing extravaganza", false, false, Nothing,
                (db, src) => new EfFishingExtravaganzaStore(db, src).SaveAsync(new FishingExtravaganzaState(false, false, false, 0)),
                db => db.Set<FishingExtravaganzaRow>().AnyAsync(r => r.Id == 1)),

            // ---- 2(b) social: guilds, petitions, groups, GM tickets (social lease)
            new("social: guild save", false, true, Nothing,
                (db, src) => new EfSocialStore(db, src).SaveGuildAsync(Guild(1, leader)),
                db => db.Set<GuildRow>().AnyAsync(g => g.Id == 1)),
            new("social: guild delete", false, true,
                db => new EfSocialStore(db).SaveGuildAsync(Guild(2, second)),
                (db, src) => new EfSocialStore(db, src).DeleteGuildAsync(2),
                async db => !await db.Set<GuildRow>().AnyAsync(g => g.Id == 2)),
            new("social: petition save", false, true, Nothing,
                (db, src) => new EfPetitionStore(db, src).SavePetitionAsync(new PetitionData(1, leader, 1000, "Petition", [])),
                db => db.Set<PetitionRow>().AnyAsync(p => p.Id == 1)),
            new("social: petition delete", false, true,
                db => new EfPetitionStore(db).SavePetitionAsync(new PetitionData(2, second, 1001, "Other", [])),
                (db, src) => new EfPetitionStore(db, src).DeletePetitionAsync(2),
                async db => !await db.Set<PetitionRow>().AnyAsync(p => p.Id == 2)),
            new("social: group save", false, true, Nothing,
                (db, src) => new EfGroupStore(db, src).SaveGroupAsync(Group(1, leader)),
                db => db.Set<GroupRow>().AnyAsync(g => g.GroupId == 1)),
            new("social: group delete", false, true,
                db => new EfGroupStore(db).SaveGroupAsync(Group(2, second)),
                (db, src) => new EfGroupStore(db, src).DeleteGroupAsync(2),
                async db => !await db.Set<GroupRow>().AnyAsync(g => g.GroupId == 2)),
            new("social: GM ticket upsert", false, true, Nothing,
                (db, src) => new EfGmAuditStore(db, src).SaveTicketAsync(Ticket(1, leader)),
                db => db.Set<GmTicketRow>().AnyAsync(t => t.Id == 1)),
            new("social: GM ticket delete", false, true,
                db => new EfGmAuditStore(db).SaveTicketAsync(Ticket(2, second)),
                (db, src) => new EfGmAuditStore(db, src).DeleteTicketAsync(2),
                async db => !await db.Set<GmTicketRow>().AnyAsync(t => t.Id == 2)),
        ];
    }

    private static async Task ExpectAsync<T>(string site, Func<Task> act) where T : Exception
    {
        try
        {
            await act();
        }
        catch (T)
        {
            return;
        }
        catch (Exception ex)
        {
            throw new Xunit.Sdk.XunitException($"{site}: expected {typeof(T).Name}, got {ex.GetType().Name}: {ex.Message}");
        }

        throw new Xunit.Sdk.XunitException($"{site}: expected {typeof(T).Name}, but nothing was thrown");
    }

    private async Task RunDeniedAsync(DatabaseProvider provider, Func<Rig, IRealmLeaseFenceSource> pick)
    {
        Rig rig = await CreateAsync(provider);
        IRealmLeaseFenceSource source = pick(rig);
        foreach (Site site in Sites(rig))
        {
            await using (CharacterDbContext seed = TestContexts.Create<CharacterDbContext>(rig.Connection))
                await site.Seed(seed);
            await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(rig.Connection))
            {
                if (site.Sweep)
                    await site.Act(db, source); // a boot cleanup skips quietly
                else
                    await ExpectAsync<RealmLeaseNotHeldException>(site.Name, () => site.Act(db, source));
            }

            await using CharacterDbContext check = TestContexts.Create<CharacterDbContext>(rig.Connection);
            Assert.False(await site.Done(check), $"{site.Name}: a node that does not hold the lease must not write");
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public Task NonHolder_IsRefusedOrSkips_AtEverySite_AndChangesNothing(DatabaseProvider provider)
        => RunDeniedAsync(provider, rig => rig.NonHolder);

    [Theory]
    [MemberData(nameof(Providers))]
    public Task LostLease_IsRefusedOrSkips_AtEverySite_AndChangesNothing(DatabaseProvider provider)
        => RunDeniedAsync(provider, rig => rig.Lost);

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Holder_WritesAndSweeps_AtEverySite(DatabaseProvider provider)
    {
        Rig rig = await CreateAsync(provider);
        foreach (Site site in Sites(rig))
        {
            await using (CharacterDbContext seed = TestContexts.Create<CharacterDbContext>(rig.Connection))
                await site.Seed(seed);
            await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(rig.Connection))
                await site.Act(db, rig.Holder);

            await using CharacterDbContext check = TestContexts.Create<CharacterDbContext>(rig.Connection);
            Assert.True(await site.Done(check), $"{site.Name}: the lease holder must write");
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task HolderWhoseLeaseWasTakenOver_IsRefusedInTheTransaction_WhereTheStoreVerifiesTheLeaseRow(DatabaseProvider provider)
    {
        Rig rig = await CreateAsync(provider, takeOver: true);
        foreach (Site site in Sites(rig).Where(s => s.Fenced))
        {
            await using (CharacterDbContext seed = TestContexts.Create<CharacterDbContext>(rig.Connection))
                await site.Seed(seed);
            await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(rig.Connection))
                await ExpectAsync<RealmLeaseFenceLostException>(site.Name, () => site.Act(db, rig.Holder));

            await using CharacterDbContext check = TestContexts.Create<CharacterDbContext>(rig.Connection);
            Assert.False(await site.Done(check), $"{site.Name}: a former holder must not write");
        }
    }

    [Fact]
    public void IsRealmSweep_IsTheShapeOfTheExpiryOperations_AndNothingElse()
    {
        MailRecord mail = ExpiredMail;
        Assert.True(EfEconomyStore.IsRealmSweep(new EconomyCommitRequest(Guid.NewGuid(), [], [new DeleteMail(mail)])));
        Assert.False(EfEconomyStore.IsRealmSweep(new EconomyCommitRequest(Guid.NewGuid(), [], [new InsertMail(mail, null)])));
    }

    [Fact]
    public void IsHolder_FollowsTheSourcesHoldsAnswer_AndIsAlwaysTrueWithoutASource()
    {
        Assert.True(LeaseFencedWrite.IsHolder(null, RealmAuthorityScopes.Maintenance));
        Assert.False(LeaseFencedWrite.IsHolder(new Source(_ => null, _ => false), RealmAuthorityScopes.Maintenance));
        Assert.True(LeaseFencedWrite.IsHolder(new Source(_ => null, _ => true), RealmAuthorityScopes.Maintenance));
    }
}
