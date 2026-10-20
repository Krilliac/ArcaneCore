using ArcaneCore.Data;
using ArcaneCore.Data.Characters;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using InventoryResult = ArcaneCore.Game.Items.InventoryResult;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Features;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Items;
using ArcaneCore.World.Tests.Npc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Progression;

public sealed class PlayerbotQuestObjectiveProgressFixTests
{
    private const uint DamnedQuestId = 376;
    private const uint ScavengerPawItem = 3264;
    private const uint DuskbatWingItem = 3265;
    private const uint FillerItem = 9502;
    private const uint ScavengerEntry = 1509;
    private const uint DuskbatEntry = 1513;

    /// <summary>
    /// Regression for live world.debug.log (<c>bot-quest/objective-no-progress count=1337</c>, e.g.
    /// <c>quest=376 entry=1513 progress=0..5 reason=all-spawns-visited</c>): when all spawns of an
    /// objective are visited, <c>PreferredCreatureEntry</c> is cleared so the bot stops trying to walk
    /// visited spawns, and killing stray respawns (incrementing progress) or alternating between two
    /// visited objectives does not re-emit <c>all-spawns-visited</c> on every progress increment or switch.
    /// </summary>
    [Fact]
    public async Task AllSpawnsVisited_ClearsPreferredEntry_AndDoesNotRefireAcrossProgressIncrementsOrAlternatingObjectives()
    {
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = ScavengerPawItem, Name = "Scavenger Paw", Class = (uint)ItemClass.Quest, Stackable = 20 });
        items.Templates.Templates.Add(new ItemTemplate { Entry = DuskbatWingItem, Name = "Duskbat Wing", Class = (uint)ItemClass.Quest, Stackable = 20 });
        using IDisposable itemScope = items.Use();

        await using ObjectiveTestHost env = await ObjectiveTestHost.StartDamnedQuestAsync();
        await env.Host.OnWorldAsync(() =>
        {
            Player player = env.Session.Player!;
            PlayerNpcState state = env.Feature.Services.StateOf(player)!;
            using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);

            var goals = new PlayerbotQuestGoals(env.Session, new PlayerbotOptions { Enabled = true })
            {
                Contention = new PlayerbotContention(new PlayerbotOptions { Enabled = true }),
                Suspensions = new PlayerbotSuspensions(),
                AllSpawnsVisited = (_, entry, _) => entry is ScavengerEntry or DuskbatEntry,
            };

            // Even as stray respawns are looted (progress 0 -> 1 -> 2 -> 3) while all spawns remain marked visited,
            // PreferredCreatureEntry stays 0 and all-spawns-visited fires only once for the episode.
            for (int tick = 0; tick < 6; tick++)
            {
                if (tick > 0)
                {
                    Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(ScavengerPawItem, 1, out _));
                    Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(DuskbatWingItem, 1, out _));
                }
                goals.RefreshObjectiveEntry(player, env.Feature.Services, state);
                Assert.Equal(0u, goals.PreferredCreatureEntry);
            }

            string key = PlayerbotAnomalies.Key("quest", "objective-no-progress");
            Assert.Equal(1, capture.Count(key));
            return true;
        });
    }

    /// <summary>
    /// When bags are completely full and cannot store a required quest drop item, <c>RefreshObjectiveEntry</c>
    /// must not pick the dropper creature to grind endlessly without being able to loot the quest item.
    /// Once a stackable slot or free bag slot exists, the dropper is picked again.
    /// </summary>
    [Fact]
    public async Task FullBagsThatCannotStoreQuestDrop_DoNotSelectDropperUntilSpaceOrPartialStackExists()
    {
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = ScavengerPawItem, Name = "Scavenger Paw", Class = (uint)ItemClass.Quest, Stackable = 6 });
        items.Templates.Templates.Add(new ItemTemplate { Entry = DuskbatWingItem, Name = "Duskbat Wing", Class = (uint)ItemClass.Quest, Stackable = 6 });
        items.Templates.Templates.Add(new ItemTemplate { Entry = FillerItem, Name = "Rough Stone", Class = (uint)ItemClass.TradeGoods, Quality = 1, Stackable = 1 });
        using IDisposable itemScope = items.Use();

        await using ObjectiveTestHost env = await ObjectiveTestHost.StartDamnedQuestAsync();
        await env.Host.OnWorldAsync(() =>
        {
            Player player = env.Session.Player!;
            PlayerNpcState state = env.Feature.Services.StateOf(player)!;
            while (PlayerbotTownGoals.FreeBagSlots(player) > 0)
                Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(FillerItem, 1, out _));

            Assert.False(PlayerbotQuestObjectives.CanStoreObjectiveItem(player, ScavengerPawItem));
            Assert.False(PlayerbotQuestObjectives.CanStoreObjectiveItem(player, DuskbatWingItem));

            var goals = new PlayerbotQuestGoals(env.Session, new PlayerbotOptions { Enabled = true })
            {
                Contention = new PlayerbotContention(new PlayerbotOptions { Enabled = true }),
                Suspensions = new PlayerbotSuspensions(),
            };

            goals.RefreshObjectiveEntry(player, env.Feature.Services, state);
            Assert.Equal(0u, goals.PreferredCreatureEntry);

            // Freeing one slot and adding a partial stack of Duskbat Wing (1/6) allows Duskbat Wing to stack even if full again.
            Assert.Equal(1u, player.Inventory.DestroyItemCount(FillerItem, 1));
            Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(DuskbatWingItem, 1, out _));
            Assert.Equal(0, PlayerbotTownGoals.FreeBagSlots(player));
            Assert.False(PlayerbotQuestObjectives.CanStoreObjectiveItem(player, ScavengerPawItem));
            Assert.True(PlayerbotQuestObjectives.CanStoreObjectiveItem(player, DuskbatWingItem));

            goals.RefreshObjectiveEntry(player, env.Feature.Services, state);
            Assert.Equal(DuskbatEntry, goals.PreferredCreatureEntry);
            return true;
        });
    }

    /// <summary>
    /// If two different quest objectives both time out or are visited, alternating between them must not
    /// re-emit duplicate <c>bot-quest/objective-no-progress</c> anomalies for the same <c>(questId, entry, progress)</c>.
    /// </summary>
    [Fact]
    public async Task AlternatingObjectives_DeduplicateNoProgressAnomaliesPerQuestAndEntry()
    {
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = ScavengerPawItem, Name = "Scavenger Paw", Class = (uint)ItemClass.Quest, Stackable = 20 });
        items.Templates.Templates.Add(new ItemTemplate { Entry = DuskbatWingItem, Name = "Duskbat Wing", Class = (uint)ItemClass.Quest, Stackable = 20 });
        using IDisposable itemScope = items.Use();

        await using ObjectiveTestHost env = await ObjectiveTestHost.StartDamnedQuestAsync();
        await env.Host.OnWorldAsync(() =>
        {
            Player player = env.Session.Player!;
            PlayerNpcState state = env.Feature.Services.StateOf(player)!;
            using AnomalyCapture capture = PlayerbotAnomalies.CaptureFor(player);

            var suspensions = new PlayerbotSuspensions();
            var goals = new PlayerbotQuestGoals(env.Session, new PlayerbotOptions { Enabled = true })
            {
                Suspensions = suspensions,
                AllSpawnsVisited = (_, _, _) => true,
            };

            uint now = env.Session.World.NowMs;
            // Round 1: first objective is visited -> reported once.
            goals.RefreshObjectiveEntry(player, env.Feature.Services, state);

            // Suspend DuskbatEntry so ScavengerEntry is chosen -> reported once.
            suspensions.SuspendEntry(DuskbatEntry, now);
            goals.RefreshObjectiveEntry(player, env.Feature.Services, state);

            // Suspend ScavengerEntry on a fresh suspension table so DuskbatEntry is chosen -> reported once.
            goals.Suspensions = new PlayerbotSuspensions();
            goals.Suspensions.SuspendEntry(ScavengerEntry, now);
            goals.RefreshObjectiveEntry(player, env.Feature.Services, state);

            // Alternating back again must not re-fire either objective.
            goals.Suspensions = new PlayerbotSuspensions();
            goals.Suspensions.SuspendEntry(DuskbatEntry, now);
            goals.RefreshObjectiveEntry(player, env.Feature.Services, state);

            goals.Suspensions = new PlayerbotSuspensions();
            goals.Suspensions.SuspendEntry(ScavengerEntry, now);
            goals.RefreshObjectiveEntry(player, env.Feature.Services, state);

            string key = PlayerbotAnomalies.Key("quest", "objective-no-progress");
            int scavengerNotices = capture.Notices.Count(n => n.Key == key && n.Detail.Contains("entry=1509", StringComparison.Ordinal));
            int duskbatNotices = capture.Notices.Count(n => n.Key == key && n.Detail.Contains("entry=1513", StringComparison.Ordinal));
            Assert.Equal(1, scavengerNotices);
            Assert.Equal(1, duskbatNotices);
            return true;
        });
    }

    /// <summary>
    /// GitHub #809 (<c>bot-quest/objective-no-progress ... reason=no-credit-timeout</c>, quest 384 and 317 at entry 708 the most frequent
    /// pairs on live): classicdb has Small Crag Boar (708, level 3) drop Crag Boar Rib (2886) at 0.02% where Crag Boar (1125, level 5-6)
    /// drops it at 52.37%. A level 2 bot, too low for Crag Boar, took Small Crag Boar as its item objective's creature and hunted it with no
    /// credit until the contention clock set it aside. A trace dropper is no objective: the low bot has none, and at level 5 the real one.
    /// </summary>
    [Fact]
    public async Task ItemObjective_IsNotHuntedAtATraceDropper()
    {
        const uint ribQuest = 384, ribItem = 2886, smallBoar = 708, cragBoar = 1125;
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = ribItem, Name = "Crag Boar Rib", Class = (uint)ItemClass.Quest, Stackable = 20 });
        using IDisposable itemScope = items.Use();

        await using ObjectiveTestHost env = await ObjectiveTestHost.StartDamnedQuestAsync();
        await env.Host.OnWorldAsync(() =>
        {
            Player player = env.Session.Player!;
            QuestNpcFeature feature = env.Feature;
            feature.Services.ReplaceQuests(new QuestStore(new QuestContent(
                [new QuestTemplate
                {
                    Entry = ribQuest, Method = 2, MinLevel = 1, QuestLevel = 2, Title = "Beer Basted Boar Ribs",
                    Details = "Bring Crag Boar Ribs.", RequestItemsText = "The ribs?", ReqItemId1 = ribItem, ReqItemCount1 = 6,
                }],
                [new CreatureQuestRelation { Id = QuestInteractionFixture.Entry, Quest = ribQuest }],
                [new CreatureQuestRelation { Id = QuestInteractionFixture.Entry, Quest = ribQuest }])));
            env.Host.WorldServices.GetRequiredService<GameObjectLootFeature>().ReplaceLootContent(new LootContent(
                [
                    (LootTableKind.Creature, new LootStoreRow(smallBoar, ribItem, 0.02f, 0, 1, 1)),
                    (LootTableKind.Creature, new LootStoreRow(cragBoar, ribItem, 52.3719f, 0, 1, 1)),
                ],
                [new CreatureLootInfo(smallBoar, smallBoar, 0, 0, 0), new CreatureLootInfo(cragBoar, cragBoar, 0, 0, 0)]));
            env.Host.WorldServices.GetRequiredService<CreatureWorldFeature>().Install(new CreatureContent(
                [
                    new CreatureTemplate { Entry = smallBoar, Name = "Small Crag Boar", MinLevel = 3, MaxLevel = 3, DisplayIds = [49] },
                    new CreatureTemplate { Entry = cragBoar, Name = "Crag Boar", MinLevel = 5, MaxLevel = 6, DisplayIds = [49] },
                ],
                [
                    new CreatureSpawn { Guid = 910011, Entry = smallBoar, MapId = player.MapId, X = player.X + 30f, Y = player.Y, Z = player.Z },
                    new CreatureSpawn { Guid = 910012, Entry = cragBoar, MapId = player.MapId, X = player.X + 40f, Y = player.Y, Z = player.Z },
                ],
                [], [], []));
            Assert.Equal(GmQuestAddResult.Added, feature.Services.GmAddQuest(player, feature.Services.Quests.Get(ribQuest)!));
            PlayerNpcState state = feature.Services.StateOf(player)!;

            var goals = new PlayerbotQuestGoals(env.Session, new PlayerbotOptions { Enabled = true })
            {
                Contention = new PlayerbotContention(new PlayerbotOptions { Enabled = true }),
                Suspensions = new PlayerbotSuspensions(),
            };

            player.Level = 2; // Crag Boar (MinLevel 5) is more than two levels above: only the trace dropper is in range
            goals.RefreshObjectiveEntry(player, feature.Services, state);
            Assert.Equal(0u, goals.PreferredCreatureEntry);

            player.Level = 5;
            goals.RefreshObjectiveEntry(player, feature.Services, state);
            Assert.Equal(cragBoar, goals.PreferredCreatureEntry);
            Assert.Equal(ribQuest, goals.PreferredQuestId);
            return true;
        });
    }

    private sealed class ObjectiveTestHost : IAsyncDisposable
    {
        private ObjectiveTestHost(WorldTestHost host, WorldSession session)
        {
            Host = host;
            Session = session;
        }

        public WorldTestHost Host { get; }
        public WorldSession Session { get; }
        public QuestNpcFeature Feature => Host.WorldServices.GetRequiredService<QuestNpcFeature>();

        public static async Task<ObjectiveTestHost> StartDamnedQuestAsync()
        {
            var fixture = new QuestInteractionFixture();
            string databasePath = Path.Combine(Path.GetTempPath(), "arcane-playerbot-objfix-" + Guid.NewGuid().ToString("N") + ".db");
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["Database:ConnectionString"] = "Data Source=" + databasePath,
            }).Build();
            await using (ServiceProvider bootstrap = new ServiceCollection().AddLogging().AddCharacterDatabase(configuration).BuildServiceProvider())
                await bootstrap.GetRequiredService<CharacterDbInitializer>().InitializeAsync();

            QuestInteractionTestServices.Current.Value = fixture;
            WorldTestHost host;
            try
            {
                host = WorldTestHost.Start(configureServices: services => services.AddCharacterDatabase(configuration));
            }
            finally
            {
                QuestInteractionTestServices.Current.Value = null;
            }

            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(QuestInteractionFixture.Guid), "questgiver visible");

            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                player.Level = 3;
                QuestNpcFeature feature = host.WorldServices.GetRequiredService<QuestNpcFeature>();

                var damnedTemplate = new QuestTemplate
                {
                    Entry = DamnedQuestId,
                    Method = 2,
                    MinLevel = 1,
                    QuestLevel = 2,
                    Title = "The Damned",
                    Details = "Collect Scavenger Paws and Duskbat Wings.",
                    RequestItemsText = "Bring the paws and wings.",
                    ReqItemId1 = ScavengerPawItem,
                    ReqItemCount1 = 6,
                    ReqItemId2 = DuskbatWingItem,
                    ReqItemCount2 = 6,
                };

                feature.Services.ReplaceQuests(new QuestStore(new QuestContent(
                    [damnedTemplate],
                    [new CreatureQuestRelation { Id = QuestInteractionFixture.Entry, Quest = DamnedQuestId }],
                    [new CreatureQuestRelation { Id = QuestInteractionFixture.Entry, Quest = DamnedQuestId }])));

                host.WorldServices.GetRequiredService<GameObjectLootFeature>().ReplaceLootContent(new LootContent(
                    [
                        (LootTableKind.Creature, new LootStoreRow(ScavengerEntry, ScavengerPawItem, 80f, 0, 1, 1)),
                        (LootTableKind.Creature, new LootStoreRow(DuskbatEntry, DuskbatWingItem, 80f, 0, 1, 1)),
                    ],
                    [
                        new CreatureLootInfo(ScavengerEntry, ScavengerEntry, 0, 0, 0),
                        new CreatureLootInfo(DuskbatEntry, DuskbatEntry, 0, 0, 0),
                    ]));

                host.WorldServices.GetRequiredService<CreatureWorldFeature>().Install(new CreatureContent(
                    [
                        new CreatureTemplate { Entry = ScavengerEntry, Name = "Young Scavenger", MinLevel = 1, MaxLevel = 2, DisplayIds = [49] },
                        new CreatureTemplate { Entry = DuskbatEntry, Name = "Duskbat", MinLevel = 1, MaxLevel = 2, DisplayIds = [49] },
                    ],
                    [
                        new CreatureSpawn { Guid = 910001, Entry = ScavengerEntry, MapId = player.MapId, X = player.X + 30f, Y = player.Y, Z = player.Z },
                        new CreatureSpawn { Guid = 910002, Entry = DuskbatEntry, MapId = player.MapId, X = player.X + 40f, Y = player.Y, Z = player.Z },
                    ],
                    [], [], []));

                Assert.Equal(GmQuestAddResult.Added, feature.Services.GmAddQuest(player, feature.Services.Quests.Get(DamnedQuestId)!));
                return true;
            });

            return new ObjectiveTestHost(host, session);
        }

        public async ValueTask DisposeAsync()
        {
            Session.Kick();
            await Session.ManagedClosed;
            await Host.DisposeAsync();
        }
    }
}
