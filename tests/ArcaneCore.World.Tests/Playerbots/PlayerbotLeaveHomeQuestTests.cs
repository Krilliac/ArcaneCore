using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Population;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The level 5 breadcrumb quests of the fresh launch (2160 to Kharanos, 2158 to Goldshire, 2159 to Dolanaar, ...) were set aside 410 times
/// on live: level 1 bots took them and could not walk to the turn-in. A quest whose turn-in NPC is outside the bot's home is a leave-home
/// quest: a bot under a start home takes none, a bot under a graduated home takes it once it is old enough (the quest's level less
/// <c>LeaveHomeLevelMargin</c>, never below its minimum level).
/// </summary>
public sealed class PlayerbotLeaveHomeQuestTests
{
    private const float StartX = -8949.95f;
    private const float HomeRadius = 550f;

    [Fact]
    public async Task AStartHome_NeverAllowsALeaveHomeQuest_WhateverTheLevel()
    {
        await RunAsync((session, player, options, _) =>
        {
            PlayerbotHomeArea valley = Home(player, graduated: false, options);
            foreach (byte level in new byte[] { 1, 5, 12 })
            {
                player.Level = level;
                Assert.False(new PlayerbotWorldDestinations(session, options) { Home = valley }.HasQuestCandidate(player),
                    $"a level {level} bot under a start home took the breadcrumb");
            }
        });
    }

    [Fact]
    public async Task AGraduatedHome_AllowsTheLeaveHomeQuest_OnlyFromTheQuestsLevel()
    {
        await RunAsync((session, player, options, _) =>
        {
            PlayerbotHomeArea widened = Home(player, graduated: true, options);
            foreach ((byte level, bool expected) in new[] { ((byte)1, false), ((byte)4, false), ((byte)5, true), ((byte)8, true) })
            {
                player.Level = level;
                Assert.Equal(expected, new PlayerbotWorldDestinations(session, options) { Home = widened }.HasQuestCandidate(player));
            }
        });
    }

    [Fact]
    public async Task TheLevelMargin_LetsTheQuestInEarlier_ButNeverBelowItsMinimumLevel()
    {
        await RunAsync((session, player, options, quest) =>
        {
            options.Population.LeaveHomeLevelMargin = 2;
            PlayerbotHomeArea widened = Home(player, graduated: true, options);
            foreach ((byte level, bool expected) in new[] { ((byte)2, false), ((byte)3, true), ((byte)4, true) })
            {
                player.Level = level;
                Assert.Equal(expected, new PlayerbotWorldDestinations(session, options) { Home = widened }.HasQuestCandidate(player));
            }

            Assert.Equal(3u, widened.LeaveLevelOf(quest));
        });
    }

    // ---- the start valley ----

    [Fact]
    public async Task AStartHome_HoldsEveryoneElsesQuestsToTheValley_ButNotAClassesQuestsOrAChainsContinuation()
    {
        await RunAsync((session, player, options, _) =>
        {
            QuestStore quests = session.Services.GetRequiredService<QuestNpcFeature>().Services.Quests;
            CreatureContent content = session.Services.GetRequiredService<ArcaneCore.World.Creatures.CreatureWorldFeature>().Content!;
            var valley = new PlayerbotHomeArea(player.Map!.MapId, new Vector3(StartX, -132.49f, 83.53f), 1100f, "start:northshire")
            {
                ValleyRadius = 400f, LeavePolicy = options.Population,
            };
            bool Allowed(uint id, uint level = 5) => valley.QuestAllowed(quests, content, quests.Get(id)!, level);

            Assert.True(Allowed(NearQuest));       // turn-in 100 yards away
            Assert.False(Allowed(FarQuest));       // turn-in 450 yards away, for everyone: the next town's delivery
            Assert.True(Allowed(ClassQuest));      // the same distance, but for a class: its trainer's chain leads there by design
            Assert.True(Allowed(ChainQuest));      // 500 yards, but it continues a chain begun in the valley (400 + 120)
            Assert.False(Allowed(Breadcrumb, 12)); // and no level makes the valley take the breadcrumb of 3000 yards
        });
    }

    // ---- a kept leave-home quest has a way out ----

    private static void Complete(Player player, WorldSession session, uint questId)
    {
        PlayerQuestLog log = session.Services.GetRequiredService<QuestNpcFeature>().Services.StateOf(player)!.Quests;
        QuestStatusData row = log.GetOrAdd(questId);
        log.SetSlot(log.FindSlot(0), questId); // in the journal, as the remove packet needs
        row.Status = QuestStatus.Complete;
    }

    private static QuestStatus? StatusOf(Player player, WorldSession session, uint questId)
        => session.Services.GetRequiredService<QuestNpcFeature>().Services.StateOf(player)!.Quests.Get(questId)?.Status;

    [Fact]
    public async Task ACompletedQuestsReturn_IsNeverSkippedByTheHomeRule_ForAStartHomeOrAGraduatedOne()
    {
        await RunAsync((session, player, options, _) =>
        {
            Complete(player, session, Breadcrumb);
            player.Level = 1;
            foreach (bool graduated in new[] { false, true })
            {
                var destinations = new PlayerbotWorldDestinations(session, options) { Home = Home(player, graduated, options) };
                destinations.Update(player, 0, 100, returnQuestId: Breadcrumb);
                // The ender lies 3000 yards outside the home, yet the return trip to it is planned: a bot always turns a completed quest in.
                Assert.Equal(Ender, destinations.TargetEntry);
            }
        });
    }

    [Fact]
    public async Task ACompletedLeaveHomeQuest_IsKept_ThenAbandonedAfterTheBoundedStalledTrips()
    {
        await RunAsync((session, player, options, _) =>
        {
            Complete(player, session, Breadcrumb);
            var suspensions = new PlayerbotSuspensions();
            var goals = new PlayerbotQuestGoals(session, options) { Home = Home(player, graduated: true, options), Suspensions = suspensions };

            // A graduated home keeps it, however far the turn-in is.
            session.ManagedBudget = new ManagedActionBudget(1);
            Assert.False(goals.AbandonDistantCompletedQuest(player));
            Assert.Equal(QuestStatus.Complete, StatusOf(player, session, Breadcrumb));

            // Two stalled trips (set-aside, expiry, stall again): still kept.
            for (int i = 0; i < PlayerbotSuspensions.MaxReturnSetAsides - 1; i++) suspensions.SuspendQuest(Breadcrumb, unchecked(session.World.NowMs - 2_000_000u - (uint)i)); // long expired
            session.ManagedBudget = new ManagedActionBudget(1);
            Assert.False(goals.AbandonDistantCompletedQuest(player));

            // The bound: the next stalled trip ends it.
            suspensions.SuspendQuest(Breadcrumb, unchecked(session.World.NowMs - 1_000_000u)); // expired too
            session.ManagedBudget = new ManagedActionBudget(1);
            Assert.True(goals.AbandonDistantCompletedQuest(player));
            Assert.NotEqual(QuestStatus.Complete, StatusOf(player, session, Breadcrumb));
        });
    }

    [Fact]
    public async Task ACompletedQuestAcrossTheTram_ForABotTooLowForIt_IsAbandonedAfterTheBoundedSetAsides()
    {
        await RunAsync((session, player, options, _) =>
        {
            player.Level = 1;
            Complete(player, session, Breadcrumb);
            var suspensions = new PlayerbotSuspensions();
            var destinations = new PlayerbotWorldDestinations(session, options) { Suspensions = suspensions };

            for (int i = 1; i < PlayerbotSuspensions.MaxReturnSetAsides; i++)
            {
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.False(destinations.ConfirmTransitBlocked(player, Breadcrumb, Ender, (uint)(i * 700_000)));
                Assert.Equal(QuestStatus.Complete, StatusOf(player, session, Breadcrumb));
            }

            session.ManagedBudget = new ManagedActionBudget(1);
            Assert.True(destinations.ConfirmTransitBlocked(player, Breadcrumb, Ender, 3_000_000));
            Assert.NotEqual(QuestStatus.Complete, StatusOf(player, session, Breadcrumb));
            Assert.True(suspensions.IsQuestSuspended(Breadcrumb, 3_000_001)); // and never taken again
        });
    }

    private static QuestTemplate BreadcrumbTemplate() => new() { Entry = Breadcrumb, Method = 2, MinLevel = 1, QuestLevel = 5, Title = "On to the town" };

    private const uint Breadcrumb = 20;
    private const uint Giver = 234;
    private const uint Ender = 235;
    private const uint NearQuest = 21, FarQuest = 22, ClassQuest = 23, ChainQuest = 24;

    private static PlayerbotHomeArea Home(Player player, bool graduated, PlayerbotOptions options)
        => new(player.Map!.MapId, new Vector3(player.X, player.Y, player.Z), HomeRadius, graduated ? "zone:northshire" : "start:northshire")
        {
            AllowsLeaveQuests = graduated,
            LeavePolicy = options.Population,
        };

    private static async Task RunAsync(Action<WorldSession, Player, PlayerbotOptions, ArcaneCore.Game.Quests.Quest> body)
    {
        var fixture = new BreadcrumbFixture();
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<IQuestContentStore>(fixture);
            services.AddSingleton<ICreatureDataStore>(fixture);
            services.AddSingleton<ICharacterQuestStore>(new ArcaneCore.World.Tests.Npc.MemoryQuestStore()); // the abandon queues a quest save
        });
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                body(session, session.Player!, new PlayerbotOptions { Enabled = true },
                    host.WorldServices.GetRequiredService<QuestNpcFeature>().Services.Quests.Get(Breadcrumb)!);
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    private sealed class FlatFloor : ILineOfSight
    {
        public bool Enabled => true;
        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;
        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        { hit = to; return false; }
        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => 83.53f;
        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        { info = default; return false; }
    }

    private sealed class OpenPathfinder : IPathfinder
    {
        public bool Enabled => true;
        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => PathResult.StraightLine(start, end, PathType.Normal);
    }

    /// <summary>The giver (inside the home) offers a level 5 breadcrumb quest that is turned in 3000 yards away, outside any home.</summary>
    private sealed class BreadcrumbFixture : IQuestContentStore, ICreatureDataStore
    {
        public Task<QuestContent> LoadAsync(CancellationToken cancellationToken = default)
        {
            List<CreatureQuestRelation> starts = [new() { Id = Giver, Quest = Breadcrumb }];
            List<CreatureQuestRelation> ends = [new() { Id = Ender, Quest = Breadcrumb }];
            List<QuestTemplate> templates = [BreadcrumbTemplate()];
            foreach ((uint quest, uint ender, uint classes, int prev) in new (uint, uint, uint, int)[]
                { (NearQuest, 240, 0, 0), (FarQuest, 241, 0, 0), (ClassQuest, 242, 1, 0), (ChainQuest, 243, 0, (int)Breadcrumb) })
            {
                templates.Add(new QuestTemplate { Entry = quest, Method = 2, MinLevel = 1, QuestLevel = 3, Title = "Valley " + quest, RequiredClasses = classes, PrevQuestId = prev });
                starts.Add(new CreatureQuestRelation { Id = 250, Quest = quest }); // a giver nobody spawned: only the direct rule checks use these
                ends.Add(new CreatureQuestRelation { Id = ender, Quest = quest });
            }

            return Task.FromResult(new QuestContent(templates, starts, ends));
        }

        Task<CreatureContent> ICreatureDataStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new CreatureContent(
            [new CreatureTemplate { Entry = Giver, Name = "Giver", NpcFlags = 2, DisplayIds = [49] },
             new CreatureTemplate { Entry = Ender, Name = "Ender", NpcFlags = 2, DisplayIds = [49] },
             new CreatureTemplate { Entry = 240, Name = "Near", NpcFlags = 2, DisplayIds = [49] },
             new CreatureTemplate { Entry = 241, Name = "Far", NpcFlags = 2, DisplayIds = [49] },
             new CreatureTemplate { Entry = 242, Name = "Class", NpcFlags = 2, DisplayIds = [49] },
             new CreatureTemplate { Entry = 243, Name = "Chain", NpcFlags = 2, DisplayIds = [49] }],
            [new CreatureSpawn { Guid = 920234, Entry = Giver, MapId = 0, X = StartX + 300f, Y = -132.49f, Z = 83.53f },
             new CreatureSpawn { Guid = 920235, Entry = Ender, MapId = 0, X = StartX + 3000f, Y = -132.49f, Z = 83.53f },
             new CreatureSpawn { Guid = 920240, Entry = 240, MapId = 0, X = StartX + 100f, Y = -132.49f, Z = 83.53f },
             new CreatureSpawn { Guid = 920241, Entry = 241, MapId = 0, X = StartX + 450f, Y = -132.49f, Z = 83.53f },
             new CreatureSpawn { Guid = 920242, Entry = 242, MapId = 0, X = StartX + 450f, Y = -132.49f, Z = 83.53f },
             new CreatureSpawn { Guid = 920243, Entry = 243, MapId = 0, X = StartX + 500f, Y = -132.49f, Z = 83.53f }], [], [], []));
    }
}
