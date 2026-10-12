using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// Quest travel and the quest-giver controller agree on the home rule (Sentinel Hill, live 2026-10-12: about 165 homed bots flipped
/// between Grind and Quest every 5-10 s, walking toward Gryan Stoutmantle for quest 13 whose kill targets lie outside the home, and
/// dropping the trip each time he came into sight because the quest-giver controller refuses the quest).
/// </summary>
public sealed class PlayerbotQuestTravelHomeTests
{
    private const float StartX = -8949.95f;
    private const float HomeRadius = 550f;

    [Fact]
    public async Task A_QuestWhoseKillTargetsAreOutsideTheHome_IsNoTravelCandidate()
    {
        await RunAsync(GryanFixture.Plain(StartX + 300f), (host, session, player, feature) =>
        {
            var options = new PlayerbotOptions { Enabled = true };
            PlayerbotHomeArea home = Home(player);
            // Control: without a home the quest is a trip.
            Assert.True(new PlayerbotWorldDestinations(session, options).HasQuestCandidate(player));
            // Homed, Gryan out of sight: his only takeable quest (13) has its targets outside the home.
            Assert.False(new PlayerbotWorldDestinations(session, options) { Home = home }.HasQuestCandidate(player));
        });
    }

    [Fact]
    public async Task B_GiverWithAnInHomeQuest_TravelPicksThatQuestNotTheOutsideOne()
    {
        await RunAsync(GryanFixture.WithInHomeQuest(StartX + 300f), (host, session, player, feature) =>
        {
            var destinations = new PlayerbotWorldDestinations(session, new PlayerbotOptions { Enabled = true }) { Home = Home(player) };
            Assert.True(destinations.HasQuestCandidate(player));
            session.ManagedBudget = new ManagedActionBudget(1);
            Assert.True(destinations.Update(player, 0, 500));
            Assert.Equal(GryanFixture.Gryan, destinations.TargetEntry);
            Assert.Equal(GryanFixture.InHomeQuest, destinations.QuestId);
        });
    }

    [Fact]
    public async Task C_BrainDoesNotFlipIntoTheQuestGoalAsGryanTogglesInAndOutOfSight()
    {
        var fixture = GryanFixture.Plain(StartX + 110f);
        await RunAsync(fixture, (host, session, player, feature) =>
        {
            Creature gryan = (Creature)player.Map!.FindObject(GryanFixture.GryanGuid)!;
            var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true }) { Home = Home(player) };
            int switches = 0;
            bool wasQuest = false;
            for (int think = 0; think < 20; think++)
            {
                if (think % 2 == 0) player.VisibleObjects.Remove(gryan.Guid);
                else player.VisibleObjects.Add(gryan.Guid);
                session.ManagedBudget = new ManagedActionBudget(2);
                brain.Update(1_000);
                bool quest = brain.Goal == PlayerbotGoalKind.Quest && brain.TargetEntry == GryanFixture.Gryan;
                if (quest && !wasQuest) switches++;
                wasQuest = quest;
            }

            Assert.True(switches <= 1, $"{switches} switches into the Quest goal toward Gryan over 20 thinks");
            brain.Stop();
        }, waitForGryan: true);
    }

    [Fact]
    public async Task D_ForcedDisagreement_SetsTheQuestAsideAfterOneArrivalInSight()
    {
        await RunAsync(GryanFixture.Plain(StartX + 300f), (host, session, player, feature) =>
        {
            var options = new PlayerbotOptions { Enabled = true };
            // Travel without the home (it accepts quest 13); the quest-giver controller with it (it refuses).
            var destinations = new PlayerbotWorldDestinations(session, options);
            var quests = new PlayerbotQuestGoals(session, options) { Home = Home(player) };
            destinations.SkipQuest = quests.IsRefused;
            destinations.GiverHasCandidate = quests.HasCandidate;
            destinations.SetQuestAside = quests.SetAside;
            Creature gryan = (Creature)player.Map!.FindObject(GryanFixture.GryanGuid)!;

            Assert.True(destinations.HasQuestCandidate(player));
            session.ManagedBudget = new ManagedActionBudget(1);
            Assert.True(destinations.Update(player, 0, 500));
            Assert.Equal(GryanFixture.OutsideQuest, destinations.QuestId);

            // The trip ends in sight of Gryan: the quest-giver controller has nothing, the quest is set aside.
            player.VisibleObjects.Add(gryan.Guid);
            Assert.False(quests.HasCandidate(player));
            Assert.False(destinations.HasQuestCandidate(player));
            Assert.True(quests.IsRefused(GryanFixture.OutsideQuest));

            // Out of sight again: no second trip for it inside the set-aside window.
            player.VisibleObjects.Remove(gryan.Guid);
            Assert.False(destinations.HasQuestCandidate(player));
            Assert.False(destinations.HasQuestCandidate(player));
        }, waitForGryan: true);
    }

    private static PlayerbotHomeArea Home(Player player)
        => new(player.Map!.MapId, new Vector3(player.X, player.Y, player.Z), HomeRadius, "Sentinel Hill");

    private static async Task RunAsync(GryanFixture fixture, Action<WorldTestHost, WorldSession, Player, QuestNpcFeature> body,
        bool waitForGryan = false)
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<IQuestContentStore>(fixture);
            services.AddSingleton<ICreatureDataStore>(fixture);
        });
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            QuestNpcFeature feature = host.WorldServices.GetRequiredService<QuestNpcFeature>();
            if (waitForGryan)
                await host.WaitForWorldAsync(() => session.Player!.Map?.FindObject(GryanFixture.GryanGuid) is Creature, "Gryan on the map");
            await host.World.InvokeAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                Player player = session.Player!;
                // Quest 12 is done: quests 13 and 14 are takeable.
                QuestStatusData done = feature.Services.StateOf(player)!.Quests.GetOrAdd(GryanFixture.DoneQuest);
                done.Status = QuestStatus.Complete;
                done.Rewarded = true;
                body(host, session, player, feature);
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

    /// <summary>
    /// Gryan (234) gives quest 13 (after the rewarded quest 12) whose kill targets 589 and 590 spawn 3000 yards away, outside any home;
    /// optionally also quest 14, which needs nothing outside the home.
    /// </summary>
    private sealed class GryanFixture : IQuestContentStore, ICreatureDataStore
    {
        internal const uint Gryan = 234;
        internal const uint DoneQuest = 12;
        internal const uint OutsideQuest = 13;
        internal const uint InHomeQuest = 14;
        internal static readonly ObjectGuid GryanGuid = ObjectGuid.WithEntry(HighGuid.Unit, Gryan, 920234);

        private readonly float _gryanX;
        private readonly bool _inHomeQuest;

        private GryanFixture(float gryanX, bool inHomeQuest) { _gryanX = gryanX; _inHomeQuest = inHomeQuest; }

        internal static GryanFixture Plain(float gryanX) => new(gryanX, false);

        internal static GryanFixture WithInHomeQuest(float gryanX) => new(gryanX, true);

        public Task<QuestContent> LoadAsync(CancellationToken cancellationToken = default)
        {
            List<QuestTemplate> templates =
            [
                new() { Entry = DoneQuest, Method = 2, MinLevel = 1, QuestLevel = 1, Title = "Report to Gryan" },
                new() { Entry = OutsideQuest, Method = 2, MinLevel = 1, QuestLevel = 1, Title = "Kill the far ones", PrevQuestId = (int)DoneQuest,
                    ReqCreatureOrGOId1 = 589, ReqCreatureOrGOCount1 = 1, ReqCreatureOrGOId2 = 590, ReqCreatureOrGOCount2 = 1 },
            ];
            List<CreatureQuestRelation> relations =
            [
                new() { Id = Gryan, Quest = DoneQuest },
                new() { Id = Gryan, Quest = OutsideQuest },
            ];
            if (_inHomeQuest)
            {
                templates.Add(new QuestTemplate { Entry = InHomeQuest, Method = 2, MinLevel = 1, QuestLevel = 1, Title = "Stay in the village",
                    PrevQuestId = (int)DoneQuest });
                relations.Add(new CreatureQuestRelation { Id = Gryan, Quest = InHomeQuest });
            }

            return Task.FromResult(new QuestContent(templates, relations, relations));
        }

        Task<CreatureContent> ICreatureDataStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new CreatureContent(
            [new CreatureTemplate { Entry = Gryan, Name = "Gryan Stoutmantle", NpcFlags = 2, DisplayIds = [49] },
             new CreatureTemplate { Entry = 589, Name = "Far one", DisplayIds = [49], MinLevelHealth = 20, MaxLevelHealth = 20 },
             new CreatureTemplate { Entry = 590, Name = "Far two", DisplayIds = [49], MinLevelHealth = 20, MaxLevelHealth = 20 }],
            [new CreatureSpawn { Guid = 920234, Entry = Gryan, MapId = 0, X = _gryanX, Y = -132.49f, Z = 83.53f },
             new CreatureSpawn { Guid = 920589, Entry = 589, MapId = 0, X = StartX + 3000f, Y = -132.49f, Z = 83.53f },
             new CreatureSpawn { Guid = 920590, Entry = 590, MapId = 0, X = StartX + 3000f, Y = -232.49f, Z = 83.53f }], [], [], []));
    }
}
