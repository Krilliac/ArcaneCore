using System.Collections.Concurrent;
using ArcaneCore.Data;
using ArcaneCore.Data.Characters;
using ArcaneCore.Game;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.World.Features;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Scenarios;
using ArcaneCore.World.Tests.Duel;
using ArcaneCore.World.Tests.Items;
using ArcaneCore.World.Tests.Spells;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Tests.Playerbots.Scenarios;

/// <summary>
/// One world for scenario tests: the ordinary <see cref="WorldTestHost"/> (real world thread and handlers) with the
/// world clock on manual (<see cref="WorldRuntime.UseManualClock"/>) plus a <see cref="ScenarioTimeProvider"/>, a SQLite
/// character database (bots, inventories, mail, quests and spells persist through the real EF stores), managed
/// playerbots enabled, and the small synthetic content of <see cref="ScenarioTestContent"/>.
/// </summary>
internal sealed class ScenarioTestWorld : IAsyncDisposable
{
    private readonly string _database;

    private ScenarioTestWorld(WorldTestHost host, ScenarioTimeProvider time, string database)
    {
        Host = host;
        Time = time;
        _database = database;
        Bots = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        Clock = ScenarioClock.Manual(host.World, time);
        host.WorldServices.GetRequiredService<QuestNpcFeature>().Options.OrdinaryRewardQuestIds = [ScenarioTestContent.KillQuest];
    }

    public WorldTestHost Host { get; }

    public ScenarioTimeProvider Time { get; }

    public ManagedPlayerbotFeature Bots { get; }

    public ScenarioClock Clock { get; }

    public IServiceProvider Services => Host.WorldServices;

    public static async Task<ScenarioTestWorld> StartAsync()
    {
        string database = Path.Combine(Path.GetTempPath(), "arcane-scenario-" + Guid.NewGuid().ToString("N") + ".db");
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Sqlite",
            ["Database:ConnectionString"] = "Data Source=" + database + ";Pooling=False",
        }).Build();
        await using (ServiceProvider bootstrap = new ServiceCollection().AddLogging().AddCharacterDatabase(configuration).BuildServiceProvider())
        {
            await bootstrap.GetRequiredService<CharacterDbInitializer>().InitializeAsync();
        }

        var time = new ScenarioTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
        WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddCharacterDatabase(configuration);
            services.AddSingleton<TimeProvider>(time);
            services.AddSingleton<IWorldFeature, ManualClockFeature>();
            services.AddSingleton<IManagedPlayerbotProvisionStore>(sp => new MemoryProvisionStore(sp.GetRequiredService<IAccountStore>()));
            services.AddSingleton<IOptions<PlayerbotOptions>>(Options.Create(new PlayerbotOptions
            {
                Enabled = true, MaxBots = 8, AllowedMaps = [0, 1], Scenarios = { Enabled = true },
            }));
            ScenarioTestContent.Register(services);
        });
        var world = new ScenarioTestWorld(host, time, database);
        await world.Bots.StartupAsync(default);
        return world;
    }

    public Task<ScenarioReport> RunAsync(IPlayerbotScenario scenario, ScenarioRunOptions? options = null)
        => ScenarioRunner.RunAsync(scenario, Bots, Host.World, Services, Clock, options ?? new ScenarioRunOptions
        {
            StepTimeout = TimeSpan.FromSeconds(30), MaxDuration = TimeSpan.FromSeconds(90),
        });

    /// <summary>Run <paramref name="scenario"/> and fail the test with the whole report when it did not pass.</summary>
    public async Task<ScenarioReport> RunPassingAsync(IPlayerbotScenario scenario)
    {
        ScenarioReport report = await RunAsync(scenario);
        // Optional evidence trail: ARCANE_SCENARIO_REPORT_DIR collects every report (passing ones too).
        if (Environment.GetEnvironmentVariable("ARCANE_SCENARIO_REPORT_DIR") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            await File.AppendAllTextAsync(Path.Combine(directory, scenario.Name + ".txt"), report.ToString());
        }

        Xunit.Assert.True(report.Passed, report.ToString());
        return report;
    }

    /// <summary>A real socket client (e.g. a GM) in this world; characters live in the SQLite store, not the host's memory store.</summary>
    public async Task<WorldTestClient> EnterWorldAsync(string account, string character, AccountSecurity security)
    {
        byte[] key = await Host.AddAccountAsync(account, security);
        WorldTestClient client = await Host.ConnectAsync();
        await client.AuthenticateAsync(account, key);
        await client.CreateCharacterAsync(character);
        int id = (await WithScopeAsync(sp => sp.GetRequiredService<ICharacterStore>().GetAllIdentitiesAsync()))
            .Single(identity => identity.Name.Equals(character, StringComparison.OrdinalIgnoreCase)).Id;
        await client.LoginAsync((ulong)id);
        return client;
    }

    public async Task<T> WithScopeAsync<T>(Func<IServiceProvider, Task<T>> read)
    {
        await using AsyncServiceScope scope = Services.CreateAsyncScope();
        return await read(scope.ServiceProvider);
    }

    public async ValueTask DisposeAsync()
    {
        await Bots.ShutdownBeforeWorldStopAsync();
        await Host.DisposeAsync();
        try
        {
            File.Delete(_database);
        }
        catch (IOException)
        {
            // A pooled handle may still be closing; the temp file is harmless.
        }
    }

    /// <summary>Switches the world to the manual clock before it starts (features attach before WorldRuntime.Start).</summary>
    private sealed class ManualClockFeature : IWorldFeature
    {
        public void Attach(WorldRuntime world) => world.UseManualClock();
    }

    /// <summary>Bot account provisioning on the host's in-memory account store (the auth database is not in this host).</summary>
    private sealed class MemoryProvisionStore(IAccountStore accounts) : IManagedPlayerbotProvisionStore
    {
        private readonly ConcurrentDictionary<Guid, ManagedPlayerbotProvision> _pending = new();

        public async Task<Account> CreateAsync(Guid botId, Account account, CancellationToken cancellationToken = default)
        {
            Account created = await accounts.CreateAsync(account, cancellationToken);
            _pending[botId] = new ManagedPlayerbotProvision(botId, created.Id, created.Username, 1);
            return created;
        }

        public Task<IReadOnlyList<ManagedPlayerbotProvision>> LoadPendingAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ManagedPlayerbotProvision>>([.. _pending.Values]);

        public Task<bool> CompleteAsync(Guid botId, int accountId, CancellationToken cancellationToken = default)
            => Task.FromResult(_pending.TryRemove(botId, out ManagedPlayerbotProvision? proof) && proof.AccountId == accountId);

        public Task<bool> RollbackEmptyOwnerAsync(Guid botId, int accountId, CancellationToken cancellationToken = default)
            => Task.FromResult(_pending.TryRemove(botId, out ManagedPlayerbotProvision? proof) && proof.AccountId == accountId);
    }
}

/// <summary>
/// The synthetic content of the scenario tests, around the human start (-8949.95, -132.49, 83.53) on map 0: a mailbox
/// and a quest giver beside it; a hostile wolf (loot: Linen Cloth and 10 copper) and a hostile kobold (the kill-quest
/// target) 60+ yards away, out of aggro range of the start; the Duel spell and flag; faction templates for a human
/// player, a hostile monster and a friendly Stormwind NPC.
/// </summary>
internal static class ScenarioTestContent
{
    public const float StartX = -8949.95f;
    public const float StartY = -132.49f;
    public const float StartZ = 83.53f;

    public const uint LinenCloth = 2589;
    public const uint WolfEntry = 990001;
    public const uint WolfSpawn = 990001;
    public const float WolfX = -8889.95f;
    public const float WolfY = -132.49f;
    public const uint WolfGold = 10;
    public const uint KoboldEntry = 990002;
    public const uint KoboldSpawn = 990002;
    public const float KoboldX = -8889.95f;
    public const float KoboldY = -52.49f;
    public const uint GiverEntry = 990010;
    public const uint GiverSpawn = 990010;
    public const uint KillQuest = 990100;
    public const uint KillQuestXp = 120;
    public const uint KillQuestMoney = 35;
    public const uint MailboxEntry = 990020;
    public const uint MailboxSpawn = 990020;

    public static ObjectGuid Wolf => ObjectGuid.WithEntry(HighGuid.Unit, WolfEntry, WolfSpawn);

    public static ObjectGuid Kobold => ObjectGuid.WithEntry(HighGuid.Unit, KoboldEntry, KoboldSpawn);

    public static ObjectGuid Giver => ObjectGuid.WithEntry(HighGuid.Unit, GiverEntry, GiverSpawn);

    public static ObjectGuid Mailbox => ObjectGuid.WithEntry(HighGuid.GameObject, MailboxEntry, MailboxSpawn);

    public static void Register(IServiceCollection services)
    {
        var items = new InMemoryItemTemplateSource();
        items.Templates.Add(new ItemTemplate
        {
            Entry = LinenCloth, Name = "Linen Cloth", Class = 7, SubClass = 0, Quality = 1, Stackable = 20, SellPrice = 13,
        });
        services.AddSingleton<IItemTemplateSource>(items);
        SpellContent duel = DuelWorldHost.Content();
        services.AddSingleton<ISpellContentStore>(new InMemorySpellContentStore(duel with { Spells = [.. duel.Spells, .. ProcScenarioContent.Spells] }));
        var store = new ContentStore();
        services.AddSingleton<ICreatureDataStore>(store);
        services.AddSingleton<IQuestContentStore>(store);
        services.AddScoped<IGameObjectDataStore>(_ => store);
        services.AddScoped<ILootDataStore>(_ => store);
        services.AddSingleton(new FactionTemplateCatalog(
        [
            new FactionTemplateRecord(1, 1, 0, OwnMask: 3, FriendlyMask: 2, HostileMask: 12),   // human player
            new FactionTemplateRecord(14, 14, 0, OwnMask: 8, FriendlyMask: 0, HostileMask: 1),  // monster
            new FactionTemplateRecord(12, 72, 0, OwnMask: 2, FriendlyMask: 2, HostileMask: 8),  // Stormwind
        ]));
        // Faction 72 (Stormwind) is a reputation faction: the quest lookup reads the reaction through reputation.
        services.AddSingleton(new FactionCatalog([new FactionRecord(72, 0, [0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0], name: "Stormwind")]));
    }

    private sealed class ContentStore : ICreatureDataStore, IQuestContentStore, IGameObjectDataStore, ILootDataStore
    {
        Task<CreatureContent> ICreatureDataStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new CreatureContent(
            [
                Hostile(WolfEntry, "Scenario Wolf"),
                Hostile(KoboldEntry, "Scenario Kobold"),
                new CreatureTemplate
                {
                    Entry = GiverEntry, Name = "Scenario Marshal", Faction = 12, NpcFlags = (uint)NpcFlags.QuestGiver, DisplayIds = [49],
                    MinLevelHealth = 100, MaxLevelHealth = 100, ExtraFlags = Game.Creatures.Creature.ExtraFlagNoAggro,
                },
            ],
            [
                new CreatureSpawn { Guid = WolfSpawn, Entry = WolfEntry, MapId = 0, X = WolfX, Y = WolfY, Z = StartZ },
                new CreatureSpawn { Guid = KoboldSpawn, Entry = KoboldEntry, MapId = 0, X = KoboldX, Y = KoboldY, Z = StartZ },
                new CreatureSpawn { Guid = GiverSpawn, Entry = GiverEntry, MapId = 0, X = StartX + 3f, Y = StartY, Z = StartZ, Orientation = MathF.PI },
            ], [], [], []));

        Task<QuestContent> IQuestContentStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new QuestContent(
            [new QuestTemplate
            {
                Entry = KillQuest, Method = 2, MinLevel = 1, QuestLevel = 1, RequiredRaces = 77, Title = "Scenario culling",
                ReqCreatureOrGOId1 = (int)KoboldEntry, ReqCreatureOrGOCount1 = 1, RewXP = KillQuestXp, RewOrReqMoney = (int)KillQuestMoney,
            }],
            [new CreatureQuestRelation { Id = GiverEntry, Quest = KillQuest }],
            [new CreatureQuestRelation { Id = GiverEntry, Quest = KillQuest }]));

        Task<GameObjectContent> IGameObjectDataStore.LoadAsync(CancellationToken cancellationToken)
        {
            var mailbox = new GameObjectTemplate
            {
                Entry = MailboxEntry, Type = (uint)GameObjectType.Mailbox, DisplayId = 3, Name = "Mailbox", Data = new uint[GameObjectTemplate.DataCount],
            };
            var flag = new GameObjectTemplate
            {
                Entry = DuelWorldHost.FlagEntry, Type = (uint)GameObjectType.DuelArbiter, DisplayId = 787, Name = "Duel Flag",
                Data = new uint[GameObjectTemplate.DataCount],
            };
            return Task.FromResult(new GameObjectContent([mailbox, flag],
                [new GameObjectSpawn { Guid = MailboxSpawn, Entry = MailboxEntry, MapId = 0, X = StartX, Y = StartY - 2f, Z = StartZ }],
                [], [], []));
        }

        Task<LootContent> ILootDataStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new LootContent(
            [(LootTableKind.Creature, new LootStoreRow(WolfEntry, LinenCloth, 100f, 0, 1, 1))],
            [new CreatureLootInfo(WolfEntry, WolfEntry, 0, WolfGold, WolfGold)]));

        private static CreatureTemplate Hostile(uint entry, string name) => new()
        {
            Entry = entry, Name = name, Faction = 14, CreatureType = 1, MinLevel = 1, MaxLevel = 1, DisplayIds = [903],
            MinLevelHealth = 14, MaxLevelHealth = 14, MinMeleeDamage = 1, MaxMeleeDamage = 2, MeleeBaseAttackTime = 2000,
        };
    }
}
