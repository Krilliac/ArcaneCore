using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Data;
using ArcaneCore.Data.Characters;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Game.Quests;
using ArcaneCore.Protocol;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Npc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotQuestGoalsTests
{
    [Fact]
    public Task AutonomousQuestStages_RespectBudgetThenAcceptAndRewardOnlyCompletedProgress()
        => RunQuestStagesAsync(feature => feature.Options.OrdinaryRewardQuestIds = [QuestInteractionFixture.QuestId]);

    /// <summary>
    /// Quests:RewardMode is AllSupported by default (229faf27): every supported quest settles, with no allowlist. The bot must
    /// pursue exactly what the server settles, so under the default options it accepts and turns in the quest. Before the
    /// predicate it consulted the allowlist alone and never accepted anything on a default server.
    /// </summary>
    [Fact]
    public Task DefaultQuestOptions_BotAcceptsAndRewardsASupportedQuestWithoutAnAllowlist()
        => RunQuestStagesAsync(feature =>
        {
            Assert.Equal(QuestRewardMode.AllSupported, feature.Options.RewardMode);
            Assert.Empty(feature.Options.OrdinaryRewardQuestIds);
        });

    [Fact]
    public async Task AllowlistOnlyMode_WithoutTheQuestListed_NeverAccepts()
    {
        await using QuestHost quest = await QuestHost.StartAsync();
        await quest.Host.World.InvokeAsync(() =>
        {
            quest.Feature.Options.RewardMode = QuestRewardMode.AllowlistOnly;
            quest.Feature.Options.OrdinaryRewardQuestIds = [];
            var goals = new PlayerbotQuestGoals(quest.Session, new PlayerbotOptions { Enabled = true });
            Player player = quest.Session.Player!;
            Assert.False(goals.HasCandidate(player));
            for (int i = 0; i < 3; i++)
            { quest.Session.ManagedBudget = new ManagedActionBudget(1); goals.Update(player, 500); }
            Assert.Null(quest.Feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId));
            quest.Feature.Options.OrdinaryRewardQuestIds = [QuestInteractionFixture.QuestId];
            Assert.True(goals.HasCandidate(player));
            return true;
        });
    }

    private static async Task RunQuestStagesAsync(Action<QuestNpcFeature> configure)
    {
        await using QuestHost quest = await QuestHost.StartAsync();
        WorldTestHost host = quest.Host;
        WorldSession session = quest.Session;
        QuestNpcFeature feature = quest.Feature;
        var goals = new PlayerbotQuestGoals(session, new PlayerbotOptions { Enabled = true });
        await host.World.InvokeAsync(() =>
        {
            configure(feature);
            var player = session.Player!;
            session.ManagedBudget = new ManagedActionBudget(0);
            goals.Update(player, 500);
            Assert.Null(feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId));
            for (int i = 0; i < 2; i++)
            { session.ManagedBudget = new ManagedActionBudget(1); goals.Update(player, 500); }
            Assert.Equal(QuestStatus.Incomplete, feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId)?.Status);
            session.ManagedBudget = new ManagedActionBudget(1); goals.Update(player, 500);
            Assert.Equal(90u, goals.PreferredCreatureEntry);
            Assert.False(feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId)!.Rewarded);
            // A producer event supplies completed progress; the bot never fabricates it.
            feature.Services.KilledMonsterCredit(player, 90, ObjectGuid.WithEntry(HighGuid.Unit, 90, 42));
            for (int i = 0; i < 3; i++)
            { session.ManagedBudget = new ManagedActionBudget(1); goals.Update(player, 500); }
            Assert.True(feature.PendingSettlementCount > 0 || feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId)!.Rewarded,
                "Reward stage=" + goals.StageName + "; packets=" + string.Join(',', session.DrainManagedPackets().Select(p => p.Opcode)));
            return true;
        });
        await feature.WaitForSettlementAsync((int)session.Player!.Guid.Low);
        await host.WaitForWorldAsync(() => feature.Services.StateOf(session.Player!)!.Quests.Get(QuestInteractionFixture.QuestId)!.Rewarded,
            "managed durable quest reward");
        await using var persisted = host.WorldServices.CreateAsyncScope();
        CharacterQuestData data = await persisted.ServiceProvider.GetRequiredService<ICharacterQuestStore>()
            .LoadAsync((int)session.Player!.Guid.Low);
        Assert.True(data.Quests.Single(q => q.Quest == QuestInteractionFixture.QuestId).Rewarded);
    }

    /// <summary>A world with the synthetic quest giver beside a managed player and a SQLite character database.</summary>
    private sealed class QuestHost : IAsyncDisposable
    {
        private QuestHost(WorldTestHost host, WorldSession session)
        {
            Host = host;
            Session = session;
        }

        public WorldTestHost Host { get; }

        public WorldSession Session { get; }

        public QuestNpcFeature Feature => Host.WorldServices.GetRequiredService<QuestNpcFeature>();

        public static async Task<QuestHost> StartAsync()
        {
            var fixture = new QuestInteractionFixture();
            string databasePath = Path.Combine(Path.GetTempPath(), "arcane-playerbot-quest-" + Guid.NewGuid().ToString("N") + ".db");
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite", ["Database:ConnectionString"] = "Data Source=" + databasePath,
            }).Build();
            await using (ServiceProvider bootstrap = new ServiceCollection().AddLogging().AddCharacterDatabase(configuration).BuildServiceProvider())
                await bootstrap.GetRequiredService<CharacterDbInitializer>().InitializeAsync();
            QuestInteractionTestServices.Current.Value = fixture;
            WorldTestHost host;
            try { host = WorldTestHost.Start(configureServices: services => services.AddCharacterDatabase(configuration)); }
            finally { QuestInteractionTestServices.Current.Value = null; }
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(QuestInteractionFixture.Guid), "managed questgiver visibility");
            return new QuestHost(host, session);
        }

        public async ValueTask DisposeAsync()
        {
            Session.Kick();
            await Session.ManagedClosed;
            await Host.DisposeAsync();
        }
    }
}
