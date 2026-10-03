using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Reputation;
using ArcaneCore.World.Tests.Npc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Reputation;

/// <summary>
/// The reputation feature forwards every standing change to the quest services, so a reputation-objective quest of a real
/// logged-in player completes and reverts as the standing crosses its value (vmangos Player::ReputationChanged).
/// </summary>
public sealed class ReputationQuestObjectiveWorldTests
{
    private const uint BootyBay = 21;
    private const uint QuestId = 900101;
    private const uint GiverEntry = 900110;
    private static readonly ObjectGuid GiverGuid = ObjectGuid.WithEntry(HighGuid.Unit, GiverEntry, 900120);

    [Fact]
    public async Task ReputationObjectiveQuest_CompletesAndReverts_WithTheLivePlayersStanding()
    {
        var fixture = new ReputationQuestFixture();
        ReputationQuestTestServices.Current.Value = fixture;
        ReputationTestServices.Current.Value = new MemoryReputationStore();
        WorldTestHost host;
        try { host = WorldTestHost.Start(); }
        finally
        {
            ReputationQuestTestServices.Current.Value = null;
            ReputationTestServices.Current.Value = null;
        }

        await using (host)
        {
            await using WorldTestClient client = await host.EnterWorldAsync("REPQUESTW", "Repquestw");
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Repquestw")!.VisibleObjects.Contains(GiverGuid), "the questgiver becomes visible");
            QuestNpcFeature quests = await host.PlayerStateAsync("Repquestw", p => ((WorldSession)p.Session).Services.GetRequiredService<QuestNpcFeature>());
            int id = await host.PlayerStateAsync("Repquestw", p => checked((int)p.Guid.Low));

            Assert.True(await host.OnWorldAsync(() => quests.Services.AcceptQuest(host.World.FindOnlinePlayer("Repquestw")!, GiverGuid, QuestId)));
            await quests.Persistence.FlushCharacterAsync(id);
            Assert.Equal((byte)QuestStatus.Incomplete, fixture.Characters.Stored(id, QuestId).Status);

            await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Repquestw")!;
                var reputation = ((WorldSession)player.Session).Services.GetRequiredService<ReputationFeature>();
                Assert.True(reputation.Service.ModifyReputation(player, BootyBay, 3000));
            });
            await quests.Persistence.FlushCharacterAsync(id);
            Assert.Equal((byte)QuestStatus.Complete, fixture.Characters.Stored(id, QuestId).Status);

            await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Repquestw")!;
                var reputation = ((WorldSession)player.Session).Services.GetRequiredService<ReputationFeature>();
                Assert.True(reputation.Service.ModifyReputation(player, BootyBay, -1));
            });
            await quests.Persistence.FlushCharacterAsync(id);
            Assert.Equal((byte)QuestStatus.Incomplete, fixture.Characters.Stored(id, QuestId).Status);
        }
    }

    private sealed class ReputationQuestFixture : IQuestContentStore, ICreatureDataStore
    {
        public MemoryQuestStore Characters { get; } = new();

        public ManualQuestClock Clock { get; } = new();

        public Task<QuestContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new QuestContent(
            [new QuestTemplate { Entry = QuestId, Method = 2, MinLevel = 1, QuestLevel = 1, Title = "Earn their trust", RepObjectiveFaction = BootyBay, RepObjectiveValue = 3000 }],
            [new CreatureQuestRelation { Id = GiverEntry, Quest = QuestId }], [new CreatureQuestRelation { Id = GiverEntry, Quest = QuestId }]));

        Task<CreatureContent> ICreatureDataStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new CreatureContent(
            [new CreatureTemplate { Entry = GiverEntry, Name = "Reputation questgiver", Faction = 900011, NpcFlags = 2, DisplayIds = [49] }],
            [new CreatureSpawn { Guid = 900120, Entry = GiverEntry, MapId = 0, X = -8948.95f, Y = -132.49f, Z = 83.53f }], [], [], []));
    }

    private sealed class ReputationQuestTestServices : IWorldTestServices
    {
        public static readonly AsyncLocal<ReputationQuestFixture?> Current = new();

        public void Register(IServiceCollection services)
        {
            if (Current.Value is not { } fixture)
            {
                return;
            }

            services.AddSingleton<IQuestContentStore>(fixture);
            services.AddSingleton<ICreatureDataStore>(fixture);
            services.AddSingleton<ICharacterQuestStore>(fixture.Characters);
            services.AddSingleton<TimeProvider>(fixture.Clock);
            services.AddSingleton(new FactionTemplateCatalog([new(1, 1, 0, 1, 0, 0), new(900011, 0, 0, 8, 0, 0)]));
        }
    }
}
