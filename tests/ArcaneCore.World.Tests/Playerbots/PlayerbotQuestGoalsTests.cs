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
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Game.Items;
using ArcaneCore.World.Features;
using ArcaneCore.World.Tests.Items;
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

    /// <summary>
    /// A quest whose accept the server refuses (its source item does not fit in the full bags, vmangos
    /// CanGiveQuestSourceItemIfNeed) is asked for once; after the exchange's deadline the bot leaves it alone instead of asking
    /// again every few seconds. On the replayed live state Ironwander stood at Hands Springsprocket for good, accepting quest 2160
    /// (Supplies to Tannok, which hands out a crate) over and over.
    /// </summary>
    [Fact]
    public async Task AQuestTheServerRefusesToHandOut_IsNotAskedForAgainAfterTheDeadline()
    {
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = 9501, Name = "Quest crate", Class = (uint)ItemClass.Quest, Stackable = 1 });
        items.Templates.Templates.Add(new ItemTemplate { Entry = 9502, Name = "Filler", Class = (uint)ItemClass.TradeGoods, Quality = 1, Stackable = 1 });
        using IDisposable itemScope = items.Use();
        await using QuestHost quest = await QuestHost.StartAsync(new QuestInteractionFixture { SourceItem = 9501 }, manualClock: true);
        WorldSession session = quest.Session;
        int queries = 0;
        var goals = new PlayerbotQuestGoals(session, new PlayerbotOptions { Enabled = true });
        await quest.Host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            while (PlayerbotTownGoals.FreeBagSlots(player) > 0)
                Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(9502, 1, out _));
            session.ManagedDispatchObserver = opcode => { if (opcode == WorldOpcode.CmsgQuestgiverQueryQuest) queries++; };
            Assert.True(goals.HasCandidate(player));
            for (int think = 0; think < 4; think++)
            { session.ManagedBudget = new ManagedActionBudget(1); goals.Update(player, 500); }
            Assert.Equal(1, queries);
            Assert.Null(quest.Feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId)); // refused
            return true;
        });

        for (int second = 0; second < 30; second++)
        {
            await quest.Host.World.AdvanceClockAsync(1_000);
            await quest.Host.OnWorldAsync(() =>
            {
                for (int think = 0; think < 2; think++)
                { session.ManagedBudget = new ManagedActionBudget(1); goals.Update(session.Player!, 500); }
                return true;
            });
        }

        await quest.Host.OnWorldAsync(() =>
        {
            Assert.Equal(1, queries);
            Assert.False(goals.HasCandidate(session.Player!));
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

        public static async Task<QuestHost> StartAsync(QuestInteractionFixture? questFixture = null, bool manualClock = false)
        {
            QuestInteractionFixture fixture = questFixture ?? new QuestInteractionFixture();
            string databasePath = Path.Combine(Path.GetTempPath(), "arcane-playerbot-quest-" + Guid.NewGuid().ToString("N") + ".db");
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite", ["Database:ConnectionString"] = "Data Source=" + databasePath,
            }).Build();
            await using (ServiceProvider bootstrap = new ServiceCollection().AddLogging().AddCharacterDatabase(configuration).BuildServiceProvider())
                await bootstrap.GetRequiredService<CharacterDbInitializer>().InitializeAsync();
            QuestInteractionTestServices.Current.Value = fixture;
            WorldTestHost host;
            try
            {
                host = WorldTestHost.Start(configureServices: services =>
                {
                    services.AddCharacterDatabase(configuration);
                    if (manualClock) services.AddSingleton<IWorldFeature, ManualClock>();
                });
            }
            finally { QuestInteractionTestServices.Current.Value = null; }
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(QuestInteractionFixture.Guid), "managed questgiver visibility");
            return new QuestHost(host, session);
        }

        private sealed class ManualClock : IWorldFeature
        {
            public void Attach(WorldRuntime world) => world.UseManualClock();
        }

        public async ValueTask DisposeAsync()
        {
            Session.Kick();
            await Session.ManagedClosed;
            await Host.DisposeAsync();
        }
    }
}
