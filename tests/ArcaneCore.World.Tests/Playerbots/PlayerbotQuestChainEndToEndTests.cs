using ArcaneCore.Data;
using ArcaneCore.Data.Characters;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Reputation;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotQuestChainEndToEndTests
{
    [Fact]
    public async Task Successor15_IsRefusedWithoutAllowlist_AndSettlesWithAllowlist()
    {
        var fixture = new Quest15Fixture();
        Quest15Services.Current.Value = fixture;
        try
        {
            string db = Path.Combine(Path.GetTempPath(), "arcane-chain80-" + Guid.NewGuid().ToString("N") + ".db");
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Database:Provider"] = "Sqlite", ["Database:ConnectionString"] = "Data Source=" + db }).Build();
            await using (ServiceProvider bootstrap = new ServiceCollection().AddLogging().AddCharacterDatabase(configuration).BuildServiceProvider())
                await bootstrap.GetRequiredService<CharacterDbInitializer>().InitializeAsync();
            await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
            {
                services.AddCharacterDatabase(configuration);
                services.AddSingleton(new FactionCatalog([new FactionRecord(72, 0,
                    [0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0], name: "Quest 15 faction")]));
            });
            WorldSession session = await EnterSeededAsync(host);
            try
            {
                QuestNpcFeature feature = host.WorldServices.GetRequiredService<QuestNpcFeature>();
                await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(Quest15Fixture.Guid), "quest 15 giver");
                await host.World.InvokeAsync(() =>
                {
                    Player player = session.Player!;
                    var goals = new PlayerbotQuestGoals(session, new PlayerbotOptions { Enabled = true });
                    feature.Options.RewardMode = QuestRewardMode.AllowlistOnly;
                    feature.Options.OrdinaryRewardQuestIds = [783, 7];
                    session.ManagedBudget = new ManagedActionBudget(1);
                    Assert.False(goals.HasCandidate(player));
                    feature.Options.OrdinaryRewardQuestIds = [783, 7, 15];
                    session.ManagedBudget = new ManagedActionBudget(1);
                    goals.Update(player, 500);
                    session.ManagedBudget = new ManagedActionBudget(1);
                    goals.Update(player, 500);
                    Assert.Equal(QuestStatus.Incomplete, feature.Services.StateOf(player)!.Quests.Get(15)!.Status);
                    InstallHumanBonus(host.WorldServices.GetRequiredService<SpellFeature>().System, player);
                    for (uint i = 0; i < 10; i++)
                        feature.Services.KilledMonsterCredit(player, 257, ObjectGuid.WithEntry(HighGuid.Unit, 257, i + 1));
                    for (int i = 0; i < 5; i++)
                    {
                        session.ManagedBudget = new ManagedActionBudget(1);
                        goals.Update(player, 500);
                    }
                    return true;
                });
                await feature.WaitForSettlementAsync((int)session.Player!.Guid.Low);
                await host.WaitForWorldAsync(() => feature.Services.StateOf(session.Player!)!.Quests.Get(15)?.Rewarded == true, "quest 15 reward");
                Assert.Equal(250u, session.Player!.GetUInt32(UpdateFields.PlayerXp));
                Assert.Equal(40u, session.Player!.Money);
                Assert.Equal(275, host.WorldServices.GetRequiredService<ReputationFeature>().Service.GetReputation(session.Player!, 72));
                await using var scope = host.WorldServices.CreateAsyncScope();
                CharacterQuestData saved = await scope.ServiceProvider.GetRequiredService<ICharacterQuestStore>()
                    .LoadAsync((int)session.Player!.Guid.Low);
                Assert.Contains(saved.Quests, row => row.Quest == 15 && row.Rewarded);
                CharacterReputationData rep = await scope.ServiceProvider.GetRequiredService<ICharacterReputationStore>()
                    .LoadAsync((int)session.Player!.Guid.Low);
                Assert.Equal(275, rep.Factions.Single(row => row.Faction == 72).Standing);
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { Quest15Services.Current.Value = null; }
    }

    private static void InstallHumanBonus(SpellSystem spells, Player player)
    {
        spells.Store = new SpellStore([.. spells.Store.All, new SpellInfo
        {
            Id = 20599, Name = "Human diplomacy", RangeIndex = 1, Range = new SpellRange(0, 0),
            Duration = new SpellDuration(-1, 0, -1), Effects = [new SpellEffectInfo
            {
                Effect = SpellEffectName.ApplyAura, AuraType = AuraType.ModReputationGain,
                BasePoints = 9, BaseDice = 1, DieSides = 1, TargetA = SpellImplicitTarget.UnitCaster,
            }, new(), new()]
        }], [], []);
        Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(player, 20599, SpellCastTargets.ForSelf(), triggered: true));
    }

    private static async Task<WorldSession> EnterSeededAsync(WorldTestHost host)
    {
        Account owner = await host.Accounts.CreateAsync(new Account { Username = "CHAIN80", Salt = new byte[32], Verifier = new byte[32] });
        WorldSession session = await WorldSession.CreateManagedAsync(owner, null, host.WorldServices, host.Opcodes, host.World, host.Registry, new WorldSessionOptions(), NullLogger.Instance);
        var create = new PacketWriter(); create.WriteCString("Chainbot"); create.WriteByte(1); create.WriteByte(1); for (int i = 0; i < 8; i++) create.WriteByte(0);
        await session.DispatchManagedSessionAsync(WorldOpcode.CmsgCharCreate, create.ToArray());
        int id = (await session.Services.GetRequiredService<ICharacterStore>().GetByAccountAsync(owner.Id)).Single().Id;
        await session.Services.GetRequiredService<ICharacterQuestStore>().SaveQuestsAsync(id, [
            new CharacterQuestStatus(id, 783, 1, true, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            new CharacterQuestStatus(id, 7, 1, true, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)]);
        var login = new PacketWriter(); login.WriteUInt64((ulong)id);
        await session.DispatchManagedSessionAsync(WorldOpcode.CmsgPlayerLogin, login.ToArray());
        await host.WaitForWorldAsync(() => session.Player is not null, "chain player login");
        return session;
    }
}

internal sealed class Quest15Services : IWorldTestServices
{
    public static readonly AsyncLocal<Quest15Fixture?> Current = new();
    public void Register(IServiceCollection services)
    {
        if (Current.Value is not { } fixture) return;
        services.AddSingleton<IQuestContentStore>(fixture);
        services.AddSingleton<ICreatureDataStore>(fixture);
        services.AddSingleton(new FactionTemplateCatalog([new(1, 1, 0, 1, 0, 0), new(900011, 0, 0, 8, 0, 0)]));
    }
}

internal sealed class Quest15Fixture : IQuestContentStore, ICreatureDataStore
{
    public const uint Entry = 197;
    public static readonly ObjectGuid Guid = ObjectGuid.WithEntry(HighGuid.Unit, Entry, 19701);

    public Task<QuestContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new QuestContent([
        new QuestTemplate { Entry = 783, Method = 2, MinLevel = 1, QuestLevel = 1, NextQuestInChain = 7, RequiredRaces = 77 },
        new QuestTemplate { Entry = 7, Method = 2, MinLevel = 1, QuestLevel = 2, PrevQuestId = 783, RequiredRaces = 77 },
        new QuestTemplate { Entry = 15, Method = 2, MinLevel = 1, QuestLevel = 3, PrevQuestId = 7, RequiredRaces = 77,
            ReqCreatureOrGOId1 = 257, ReqCreatureOrGOCount1 = 10, RewXP = 250, RewOrReqMoney = 40, RewMoneyMaxLevel = 150,
            RewRepFaction1 = 72, RewRepValue1 = 250 }],
        [new CreatureQuestRelation { Id = Entry, Quest = 15 }], [new CreatureQuestRelation { Id = Entry, Quest = 15 }]));

    Task<CreatureContent> ICreatureDataStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new CreatureContent(
        [new CreatureTemplate { Entry = Entry, Name = "Marshal McBride", Faction = 900011, NpcFlags = (uint)NpcFlags.QuestGiver, DisplayIds = [49] }],
        [new CreatureSpawn { Guid = 19701, Entry = Entry, MapId = 0, X = -8948.95f, Y = -132.49f, Z = 83.53f }], [], [], []));

}
