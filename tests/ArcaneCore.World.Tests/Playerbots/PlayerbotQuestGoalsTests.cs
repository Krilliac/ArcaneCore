using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Data;
using ArcaneCore.Data.Characters;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.Npc;
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

/// <summary>
/// A quest's creature objective that is friendly to the bot is credited another way (an item or a spell used on it: quest 5441
/// "Lazy Peons", the Foreman's Blackjack on a sleeping peon of the bot's own faction). The bot does not hunt it: in the live stress
/// test (2026-10-08) bots in the Valley of Trials walked from peon to peon they could not attack (goal Grind target 10556) and on out
/// of the valley to their deaths.
/// </summary>
public sealed class PlayerbotKillObjectiveTests
{
    private static readonly FactionTemplateCatalog Factions = new(
    [
        new FactionTemplateRecord(1, 0, 0, OwnMask: 3, FriendlyMask: 2, HostileMask: 12), // the player
        new FactionTemplateRecord(12, 0, 0, OwnMask: 2, FriendlyMask: 3, HostileMask: 0), // a peon of its own side
        new FactionTemplateRecord(14, 0, 0, OwnMask: 8, FriendlyMask: 0, HostileMask: 1), // a monster
        new FactionTemplateRecord(7, 0, 0, OwnMask: 0, FriendlyMask: 0, HostileMask: 0), // a neutral beast
    ]);

    [Theory]
    [InlineData(12u, false)]
    [InlineData(14u, true)]
    [InlineData(7u, true)]
    [InlineData(999u, true)] // unknown template: unresolved, still an objective
    public void OnlyACreatureNotFriendlyToTheBot_IsAKillObjective(uint npcTemplate, bool killable)
        => Assert.Equal(killable, PlayerbotQuestGoals.IsKillable(CreatePlayer(), npcTemplate, Factions, reactions: null));

    [Fact]
    public void WithReputation_AFriendlyReaction_IsNotAKillObjective()
    {
        Player player = CreatePlayer();
        Assert.False(PlayerbotQuestGoals.IsKillable(player, 14, Factions, new Reaction(ArcaneCore.Game.Reputation.ReputationRank.Friendly)));
        Assert.True(PlayerbotQuestGoals.IsKillable(player, 12, Factions, new Reaction(ArcaneCore.Game.Reputation.ReputationRank.Neutral)));
    }

    private sealed class Reaction(ArcaneCore.Game.Reputation.ReputationRank rank) : ArcaneCore.Game.Reputation.INpcReactionSource
    {
        public bool TryGetNpcReaction(Player player, FactionTemplateRecord npc, FactionTemplateRecord playerTemplate,
            out ArcaneCore.Game.Reputation.ReputationRank reaction)
        {
            reaction = rank;
            return true;
        }
    }

    private static Player CreatePlayer()
    {
        var character = new ArcaneCore.Kernel.Characters.CharacterRecord
        {
            Id = 1, AccountId = 1, Name = "Peon", Race = 2, Class = 1, Gender = 0, Level = 3,
            MapId = 1, ZoneId = 14, X = 0, Y = 0, Z = 0,
        };
        var appearance = new PlayerAppearance(
            DisplayId: 51, FactionTemplate: 1, PowerType.Rage, BaseHealth: 60, BaseMana: 0,
            MaxHealth: 60, MaxPower: 1000, StartPower: 0, NextLevelXp: 400);
        return new Player(character, appearance, new KillObjectiveSession());
    }

    private sealed class KillObjectiveSession : IPlayerSession
    {
        public int AccountId => 1;

        public ArcaneCore.Kernel.Accounts.AccountSecurity Security => ArcaneCore.Kernel.Accounts.AccountSecurity.Player;

        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload)
        {
        }

        public void ProcessWorldPackets(Player player)
        {
        }

        public void Kick()
        {
        }

        public void OnLoggedOut()
        {
        }
    }
}
