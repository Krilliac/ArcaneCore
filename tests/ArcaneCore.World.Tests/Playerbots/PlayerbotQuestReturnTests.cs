using System.Numerics;
using ArcaneCore.Data;
using ArcaneCore.Data.Characters;
using ArcaneCore.Game;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Npc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed partial class PlayerbotQuestReturnTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedQuestTravelsToOffscreenEnderBeforeNearbyMob(bool alreadyAttacking)
    {
        await using var fixture = await ReturnFixture.CreateAsync(farGiver: true);
        WorldTestHost host = fixture.Host;
        WorldSession session = fixture.Session;
        await fixture.AcceptAndCompleteAsync();
        await host.World.InvokeAsync(() =>
        {
            var player = session.Player!;
            var giver = (Creature)player.Map!.FindObject(QuestInteractionFixture.Guid)!;
            giver.SetPosition(player.X + 150, player.Y, player.Z, 0);
            WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(player.Z));
            return true;
        });
        await host.WaitForWorldAsync(() => !session.Player!.VisibleObjects.Contains(QuestInteractionFixture.Guid), "offscreen return giver");
        Creature mob = await fixture.AddNearbyMobAsync();
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true });
        try
        {
            await host.World.InvokeAsync(() =>
            {
                var player = session.Player!;
                Assert.Same(mob, PlayerbotBrain.FindTarget(player));
                float before = player.X;
                if (alreadyAttacking)
                {
                    session.ManagedBudget = new ManagedActionBudget(1);
                    Assert.True(session.TryManagedAction(WorldOpcode.CmsgAttackswing, PlayerbotNavigation.GuidPayload(mob.Guid.Value)));
                    Assert.Same(mob, player.Combat.Victim);
                    player.Map!.Combat.DealDamage(mob, player, 1);
                    Assert.True(player.Combat.IsInCombat);
                    session.ManagedBudget = new ManagedActionBudget(1);
                    brain.Update(500);
                    Assert.Null(player.Combat.Victim);
                    Assert.True(player.Combat.IsInCombat); // Stop does not grant an out-of-combat state.
                }
                session.ManagedBudget = new ManagedActionBudget(0);
                brain.Update(500);
                Assert.Equal(before, player.X);
                Assert.Null(player.Combat.Victim);
                session.ManagedBudget = new ManagedActionBudget(1);
                brain.Update(500);
                Assert.Equal(before, player.X);
                PlayerbotMotion.ElapseForTests(player, 500);
                session.ManagedBudget = new ManagedActionBudget(1);
                brain.Update(500);
                Assert.True(player.X > before);
                Assert.Equal(PlayerbotGoalKind.Quest, brain.Goal);
                Assert.Equal(QuestInteractionFixture.QuestId, brain.QuestId);
                Assert.Null(player.Combat.Victim);
                Assert.False(fixture.Feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId)!.Rewarded);
                return true;
            });
        }
        finally { brain.Stop(); }
    }

    /// <summary>
    /// GitHub #186: the return trip of a completed quest whose ender the bot cannot walk to (quest 1111, from the Shimmering Flats to
    /// Ratchet over the Great Lift) stalled with the target of an earlier goal: 4147, a Saltstone Basilisk the bot had fought, and 11259,
    /// the ender of quest 5361 it had walked to before. The stall line named that creature and the bot set it aside. The trip's target is
    /// its own ender, or none while no way to it was planned.
    /// </summary>
    [Fact]
    public async Task AReturnTripWithNoWay_DoesNotKeepOrSetAsideAnEarlierGoalsTarget()
    {
        const uint earlierTarget = 4147;
        await using var fixture = await ReturnFixture.CreateAsync(farGiver: true);
        WorldTestHost host = fixture.Host;
        WorldSession session = fixture.Session;
        await fixture.AcceptAndCompleteAsync();
        await host.World.InvokeAsync(() =>
        {
            var player = session.Player!;
            var giver = (Creature)player.Map!.FindObject(QuestInteractionFixture.Guid)!;
            giver.SetPosition(player.X + 150, player.Y, player.Z, 0);
            WorldCollision.Of(host.World).Install(lineOfSight: new Walled(player.Z)); // no step of any way is clear
            return true;
        });
        await host.WaitForWorldAsync(() => !session.Player!.VisibleObjects.Contains(QuestInteractionFixture.Guid), "offscreen return giver");
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true });
        try
        {
            await host.World.InvokeAsync(() =>
            {
                var player = session.Player!;
                float before = player.X;
                brain.HoldGoalForTests(PlayerbotGoalKind.Grind, earlierTarget, 0);
                for (int i = 0; i < 3; i++)
                {
                    session.ManagedBudget = new ManagedActionBudget(1);
                    brain.Update(500);
                }

                Assert.Equal(before, player.X); // no way was planned
                Assert.Equal(PlayerbotGoalKind.Quest, brain.Goal);
                Assert.Equal(QuestInteractionFixture.QuestId, brain.QuestId);
                Assert.NotEqual(earlierTarget, brain.TargetEntry);

                uint now = host.World.NowMs;
                brain.GiveUpStalledGoal(player);
                Assert.True(brain.Suspensions.IsQuestSuspended(QuestInteractionFixture.QuestId, now));
                Assert.False(brain.Suspensions.IsEntrySuspended(earlierTarget, now));
                return true;
            });
        }
        finally { brain.Stop(); }
    }

    /// <summary>
    /// Entering the walk to a completed quest's ender drops the target of the fight the bot was in: the attack-stop think returned with the
    /// mob's entry as the trip's target, so a stall named, and GiveUpStalledGoal set aside, the mob rather than the ender.
    /// </summary>
    [Fact]
    public async Task EnteringTheEnderTrip_ClearsTheLeftoverCombatTarget()
    {
        const uint mobEntry = 90;
        await using var fixture = await ReturnFixture.CreateAsync(farGiver: true);
        WorldTestHost host = fixture.Host;
        WorldSession session = fixture.Session;
        await fixture.AcceptAndCompleteAsync();
        await host.World.InvokeAsync(() =>
        {
            var player = session.Player!;
            var giver = (Creature)player.Map!.FindObject(QuestInteractionFixture.Guid)!;
            giver.SetPosition(player.X + 150, player.Y, player.Z, 0);
            WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(player.Z));
            return true;
        });
        await host.WaitForWorldAsync(() => !session.Player!.VisibleObjects.Contains(QuestInteractionFixture.Guid), "offscreen return giver");
        Creature mob = await fixture.AddNearbyMobAsync();
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true });
        try
        {
            await host.World.InvokeAsync(() =>
            {
                var player = session.Player!;
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(session.TryManagedAction(WorldOpcode.CmsgAttackswing, PlayerbotNavigation.GuidPayload(mob.Guid.Value)));
                player.Map!.Combat.DealDamage(mob, player, 1);
                brain.HoldGoalForTests(PlayerbotGoalKind.Grind, mobEntry, 0);
                session.ManagedBudget = new ManagedActionBudget(1);
                brain.Update(500); // the attack-stop think of the trip
                Assert.Equal(PlayerbotGoalKind.Quest, brain.Goal);
                Assert.Equal(QuestInteractionFixture.QuestId, brain.QuestId);
                Assert.NotEqual(mobEntry, brain.TargetEntry);

                uint now = host.World.NowMs;
                brain.GiveUpStalledGoal(player);
                Assert.False(brain.Suspensions.IsEntrySuspended(mobEntry, now));
                return true;
            });
        }
        finally { brain.Stop(); }
    }

    /// <summary>
    /// On the way to the ender the bot does not detour to a giver that is in view but out of reach: a visible quest NPC that is not the
    /// ender starved the trip (quest 5601: the Flame Crest NPCs). One within reach is still served (the holiday scenario takes its
    /// quests from a herald beside the bot).
    /// </summary>
    [Fact]
    public async Task WalkingToTheEnder_DoesNotDetourToAGiverOutOfReach()
    {
        await using var fixture = await ReturnFixture.CreateAsync(farGiver: true, distractor: true);
        WorldTestHost host = fixture.Host;
        WorldSession session = fixture.Session;
        await fixture.AcceptAndCompleteAsync();
        await host.World.InvokeAsync(() =>
        {
            var player = session.Player!;
            var giver = (Creature)player.Map!.FindObject(QuestInteractionFixture.Guid)!;
            giver.SetPosition(player.X + 150, player.Y, player.Z, 0);
            // The other giver stands beside the bot only now: while the quest was being taken it would have been served first.
            var other = new Creature(DistractorSpawn, new CreatureTemplate { Entry = DistractorEntry, Name = "Distracting questgiver",
                Faction = 900011, NpcFlags = 2, MinLevelHealth = 20, MaxLevelHealth = 20, DisplayIds = [49] }, null, CreatureContent.Empty, new Random(2));
            other.SetPosition(player.X - 10, player.Y, player.Z, 0);
            player.Map.AddObject(other);
            WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(player.Z));
            return true;
        });
        await host.WaitForWorldAsync(() => !session.Player!.VisibleObjects.Contains(QuestInteractionFixture.Guid)
            && session.Player.VisibleObjects.Contains(ObjectGuid.WithEntry(HighGuid.Unit, DistractorEntry, DistractorSpawn)), "offscreen ender, visible distractor");
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true });
        try
        {
            await host.World.InvokeAsync(() =>
            {
                var player = session.Player!;
                float before = player.X;
                for (int i = 0; i < 6; i++)
                {
                    session.ManagedBudget = new ManagedActionBudget(1);
                    brain.Update(500);
                    PlayerbotMotion.ElapseForTests(player, 500);
                }

                Assert.Equal(QuestStatus.None, fixture.Feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.UnrelatedId)?.Status ?? QuestStatus.None);
                Assert.Equal(QuestInteractionFixture.QuestId, brain.QuestId);
                Assert.True(player.X > before);
                return true;
            });
        }
        finally { brain.Stop(); }
    }

    /// <summary>
    /// Bot QA marks a stall as the walk home only when the brain was returning that very quest: an NPC that both gives and ends a quest is
    /// also the target of a bot on its way to take it, and that stall must not be reported as a trip to the ender.
    /// </summary>
    [Fact]
    public async Task OnlyAStallOfTheWalkHome_IsMarkedAsAReturnTrip()
    {
        await using var fixture = await ReturnFixture.CreateAsync(farGiver: true);
        WorldTestHost host = fixture.Host;
        WorldSession session = fixture.Session;
        await fixture.AcceptAndCompleteAsync();
        await host.World.InvokeAsync(() =>
        {
            var player = session.Player!;
            var giver = (Creature)player.Map!.FindObject(QuestInteractionFixture.Guid)!;
            giver.SetPosition(player.X + 150, player.Y, player.Z, 0);
            WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(player.Z));
            return true;
        });
        await host.WaitForWorldAsync(() => !session.Player!.VisibleObjects.Contains(QuestInteractionFixture.Guid), "offscreen return giver");
        var walkingHome = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true });
        try
        {
            await host.World.InvokeAsync(() =>
            {
                var player = session.Player!;
                string stall(uint quest) => $"stalled 120s: goal=Quest target={QuestInteractionFixture.Entry} quest={quest} at 0.0,0.0,0.0 map 0";
                session.ManagedBudget = new ManagedActionBudget(1);
                walkingHome.Update(500);
                Assert.Equal(PlayerbotGoalKind.Quest, walkingHome.Goal);
                walkingHome.GiveUpStalledGoal(player);
                Assert.Equal(QuestInteractionFixture.QuestId, walkingHome.StallReturnQuest);
                Assert.Contains(" return-trip qa[", walkingHome.QaReportContext(player, stall(QuestInteractionFixture.QuestId)), StringComparison.Ordinal);
                // The stall names another quest than the one returned: not the walk home.
                Assert.DoesNotContain("return-trip", walkingHome.QaReportContext(player, stall(QuestInteractionFixture.UnrelatedId)), StringComparison.Ordinal);
                // Only a living stall is marked, and only one of the Quest goal.
                Assert.DoesNotContain("return-trip", walkingHome.QaReportContext(player,
                    $"death loop: died 3 times in 60s within 20 yd of 0.0,0.0,0.0 map 0 attackers=none goal=Quest quest={QuestInteractionFixture.QuestId}"), StringComparison.Ordinal);
                Assert.DoesNotContain("return-trip", walkingHome.QaReportContext(player,
                    $"stalled 120s: goal=Group target=0 quest={QuestInteractionFixture.QuestId} at 0.0,0.0,0.0 map 0"), StringComparison.Ordinal);
                // The bot on its way to TAKE the quest from the NPC that also ends it is covered by
                // ARealStallOnTheWayToTakeTheQuest_IsNotMarkedAsAReturnTrip, which drives a real stall.
                return true;
            });
        }
        finally { walkingHome.Stop(); }
    }

    /// <summary>
    /// While walking home the bot serves another completed quest's ender only within reach: one beside it is turned in on the way, one in
    /// view but out of reach is not walked to.
    /// </summary>
    [Fact]
    public async Task WalkingHome_ServesAnotherCompletedQuestsEnderOnlyWithinReach()
    {
        await using var fixture = await ReturnFixture.CreateAsync(farGiver: true, distractor: true);
        WorldTestHost host = fixture.Host;
        WorldSession session = fixture.Session;
        await fixture.AcceptAndCompleteAsync();
        Creature? other = null;
        await host.World.InvokeAsync(() =>
        {
            var player = session.Player!;
            var giver = (Creature)player.Map!.FindObject(QuestInteractionFixture.Guid)!;
            giver.SetPosition(player.X + 150, player.Y, player.Z, 0);
            other = new Creature(DistractorSpawn, new CreatureTemplate { Entry = DistractorEntry, Name = "Second ender",
                Faction = 900011, NpcFlags = 2, MinLevelHealth = 20, MaxLevelHealth = 20, DisplayIds = [49] }, null, CreatureContent.Empty, new Random(3));
            other.SetPosition(player.X - 2, player.Y, player.Z, 0);
            player.Map.AddObject(other);
            WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(player.Z));
            return true;
        });
        await host.WaitForWorldAsync(() => !session.Player!.VisibleObjects.Contains(QuestInteractionFixture.Guid)
            && session.Player.VisibleObjects.Contains(other!.Guid), "offscreen ender, visible second ender");
        await host.World.InvokeAsync(() =>
        {
            // Take the second quest (a talk-to quest, complete on accept) while not on the trip, and stop before it is turned in.
            var goals = new PlayerbotQuestGoals(session, new PlayerbotOptions { Enabled = true });
            for (int i = 0; i < 8 && fixture.Feature.Services.StateOf(session.Player!)!.Quests.Get(QuestInteractionFixture.UnrelatedId)?.Status != QuestStatus.Complete; i++)
            { session.ManagedBudget = new ManagedActionBudget(1); goals.Update(session.Player!, 500); }
            Assert.Equal(QuestStatus.Complete, fixture.Feature.Services.StateOf(session.Player!)!.Quests.Get(QuestInteractionFixture.UnrelatedId)?.Status);
            return true;
        });
        await host.World.InvokeAsync(() =>
        {
            var player = session.Player!;
            var returning = new PlayerbotQuestGoals(session, new PlayerbotOptions { Enabled = true }) { ReturnQuest = QuestInteractionFixture.QuestId };
            Assert.True(returning.HasCandidate(player), "the second ender beside the bot is served");
            other!.SetPosition(player.X - 30, player.Y, player.Z, 0);
            Assert.Contains(other.Guid, player.VisibleObjects);
            Assert.False(returning.HasCandidate(player), "the second ender in view but out of reach is skipped");
            returning.ReturnQuest = 0;
            Assert.True(returning.HasCandidate(player), "off the trip it is walked to as before");
            return true;
        });
    }

    [Fact]
    public async Task VisibleCompletedQuestRewardsNormallyBeforeFreshAttackAndPersists()
    {
        await using var fixture = await ReturnFixture.CreateAsync(farGiver: false);
        await fixture.AcceptAndCompleteAsync();
        await fixture.AddNearbyMobAsync();
        var brain = new PlayerbotBrain(fixture.Session, new PlayerbotOptions { Enabled = true });
        try
        {
            await fixture.Host.World.InvokeAsync(() =>
            {
                for (int i = 0; i < 5 && fixture.Feature.PendingSettlementCount == 0; i++)
                {
                    fixture.Session.ManagedBudget = new ManagedActionBudget(1);
                    brain.Update(500);
                    Assert.Null(fixture.Session.Player!.Combat.Victim);
                }
                Assert.True(fixture.Feature.PendingSettlementCount > 0
                    || fixture.Feature.Services.StateOf(fixture.Session.Player!)!.Quests.Get(QuestInteractionFixture.QuestId)!.Rewarded);
                return true;
            });
            await fixture.Feature.WaitForSettlementAsync((int)fixture.Session.Player!.Guid.Low);
            await fixture.Host.WaitForWorldAsync(() => fixture.Feature.Services.StateOf(fixture.Session.Player!)!
                .Quests.Get(QuestInteractionFixture.QuestId)!.Rewarded, "ordinary return reward");
            await using var scope = fixture.Host.WorldServices.CreateAsyncScope();
            CharacterQuestData saved = await scope.ServiceProvider.GetRequiredService<ICharacterQuestStore>()
                .LoadAsync((int)fixture.Session.Player!.Guid.Low);
            Assert.True(saved.Quests.Single(q => q.Quest == QuestInteractionFixture.QuestId).Rewarded);
        }
        finally { brain.Stop(); }
    }

    [Fact]
    public async Task IncompleteOrUnapprovedQuestDoesNotCreateReturnPriority()
    {
        await using var fixture = await ReturnFixture.CreateAsync(farGiver: false);
        await fixture.AcceptAndCompleteAsync(complete: false);
        await fixture.Host.World.InvokeAsync(() =>
        {
            var goals = new PlayerbotQuestGoals(fixture.Session, new PlayerbotOptions { Enabled = true });
            Assert.Equal(0u, goals.CompletedReturnQuest(fixture.Session.Player!));
            fixture.Feature.Services.KilledMonsterCredit(fixture.Session.Player!, 90, ObjectGuid.WithEntry(HighGuid.Unit, 90, 42));
            Assert.Equal(QuestInteractionFixture.QuestId, goals.CompletedReturnQuest(fixture.Session.Player!));
            // Default Quests:RewardMode (AllSupported) settles it without an allowlist; AllowlistOnly withholds it unlisted.
            fixture.Feature.Options.OrdinaryRewardQuestIds = [];
            Assert.Equal(QuestInteractionFixture.QuestId, goals.CompletedReturnQuest(fixture.Session.Player!));
            fixture.Feature.Options.RewardMode = QuestRewardMode.AllowlistOnly;
            Assert.Equal(0u, goals.CompletedReturnQuest(fixture.Session.Player!));
            return true;
        });
    }

    [Fact]
    public async Task CompletedOutOfHomeBreadcrumb_IsAbandonedThroughTheQuestHandler()
    {
        await using var fixture = await ReturnFixture.CreateAsync(farGiver: false);
        await fixture.AcceptAndCompleteAsync();
        var brain = new PlayerbotBrain(fixture.Session, new PlayerbotOptions { Enabled = true });
        try
        {
            await fixture.Host.World.InvokeAsync(() =>
            {
                var player = fixture.Session.Player!;
                player.SetPosition(player.X + 3_000f, player.Y, player.Z, 0);
                brain.Home = new PlayerbotHomeArea(player.MapId, new Vector3(player.X, player.Y, player.Z), 750, "test home");
                fixture.Session.ManagedBudget = new ManagedActionBudget(1);
                brain.Update(500);
                Assert.Equal(QuestStatus.None, fixture.Feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId)!.Status);
                return true;
            });
            await fixture.Feature.Persistence.FlushCharacterAsync((int)fixture.Session.Player!.Guid.Low);
            await using var scope = fixture.Host.WorldServices.CreateAsyncScope();
            CharacterQuestData saved = await scope.ServiceProvider.GetRequiredService<ICharacterQuestStore>()
                .LoadAsync((int)fixture.Session.Player!.Guid.Low);
            Assert.Equal((byte)QuestStatus.None, saved.Quests.Single(q => q.Quest == QuestInteractionFixture.QuestId).Status);
        }
        finally { brain.Stop(); }
    }

    [Fact]
    public async Task RefusedDistantAbandon_DoesNotHoldTheBrainOrRepeatEveryThink()
    {
        await using var fixture = await ReturnFixture.CreateAsync(farGiver: false);
        await fixture.AcceptAndCompleteAsync();
        var brain = new PlayerbotBrain(fixture.Session, new PlayerbotOptions { Enabled = true });
        try
        {
            await fixture.Host.World.InvokeAsync(() =>
            {
                // The managed dispatch reports InWorld even when an ordinary handler refuses the operation.
                OpcodeTable opcodes = fixture.Host.Opcodes;
                var refused = new OpcodeTable();
                refused.OnWorld(WorldOpcode.CmsgQuestlogRemoveQuest, (_, _, _) => { });
                OpcodeTable replacement = opcodes.TrySwap([WorldOpcode.CmsgQuestlogRemoveQuest], refused, out string? error)!;
                Assert.Null(error);
                opcodes.Replace(replacement);

                int attempts = 0;
                fixture.Session.ManagedDispatchObserver = opcode =>
                {
                    if (opcode == WorldOpcode.CmsgQuestlogRemoveQuest) attempts++;
                };
                var player = fixture.Session.Player!;
                player.SetPosition(player.X + 3_000f, player.Y, player.Z, 0);
                brain.Home = new PlayerbotHomeArea(player.MapId, new Vector3(player.X, player.Y, player.Z), 750, "test home");
                for (int i = 0; i < 2; i++)
                {
                    fixture.Session.ManagedBudget = new ManagedActionBudget(1);
                    brain.Update(500);
                }

                Assert.Equal(QuestStatus.Complete, fixture.Feature.Services.StateOf(player)!.Quests.Get(QuestInteractionFixture.QuestId)!.Status);
                Assert.True(brain.QuestGoals.IsRefused(QuestInteractionFixture.QuestId));
                Assert.Equal(1, attempts);
                Assert.NotEqual(PlayerbotGoalKind.Quest, brain.Goal);
                return true;
            });
        }
        finally { brain.Stop(); }
    }

    /// <summary>A second quest NPC that starts <see cref="QuestInteractionFixture.UnrelatedId"/>; the test adds it beside the player.</summary>
    private const uint DistractorEntry = 900013;
    private const uint DistractorSpawn = 900030;

    private sealed class ReturnFixture(WorldTestHost host, WorldSession session) : IAsyncDisposable
    {
        internal WorldTestHost Host => host;
        internal WorldSession Session => session;
        internal QuestNpcFeature Feature => host.WorldServices.GetRequiredService<QuestNpcFeature>();

        internal static async Task<ReturnFixture> CreateAsync(bool farGiver, bool secondEnder = false, bool manualClock = false, bool distractor = false)
        {
            var original = new QuestInteractionFixture();
            string db = Path.Combine(Path.GetTempPath(), "arcane-return-" + Guid.NewGuid().ToString("N") + ".db");
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Database:Provider"] = "Sqlite", ["Database:ConnectionString"] = "Data Source=" + db }).Build();
            await using (ServiceProvider bootstrap = new ServiceCollection().AddLogging().AddCharacterDatabase(configuration).BuildServiceProvider())
                await bootstrap.GetRequiredService<CharacterDbInitializer>().InitializeAsync();
            QuestInteractionTestServices.Current.Value = original;
            WorldTestHost host;
            try
            {
                host = WorldTestHost.Start(configureServices: services =>
                {
                    services.AddCharacterDatabase(configuration);
                    if (manualClock) services.AddSingleton<IWorldFeature, ManualReturnClock>();
                    if (farGiver) services.AddSingleton<ICreatureDataStore>(new FarGiverStore(original, secondEnder));
                    if (distractor) services.AddSingleton<IQuestContentStore>(new DistractorQuests(original));
                });
            }
            finally { QuestInteractionTestServices.Current.Value = null; }
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            await host.World.InvokeAsync(() =>
            {
                var giver = (Creature)session.Player!.Map!.FindObject(QuestInteractionFixture.Guid)!;
                giver.SetPosition(session.Player.X + 1, session.Player.Y, session.Player.Z, 0);
                host.WorldServices.GetRequiredService<QuestNpcFeature>().Options.OrdinaryRewardQuestIds = [QuestInteractionFixture.QuestId];
                return true;
            });
            await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(QuestInteractionFixture.Guid), "return fixture giver visibility");
            return new ReturnFixture(host, session);
        }

        internal async Task AcceptAndCompleteAsync(bool complete = true)
        {
            // Quest state loads asynchronously: StateOf can be null or not Loaded, and the quest absent from the log, on the first
            // steps. Keep stepping the goals (one world call per poll) until the state is loaded and the quest is in the log.
            var goals = new PlayerbotQuestGoals(session, new PlayerbotOptions { Enabled = true });
            await host.WaitForWorldAsync(() =>
            {
                session.ManagedBudget = new ManagedActionBudget(1);
                goals.Update(session.Player!, 500);
                return Feature.Services.StateOf(session.Player!) is { Loaded: true } state
                    && state.Quests.Get(QuestInteractionFixture.QuestId) is not null;
            }, "return fixture quest accepted into the log");
            await host.World.InvokeAsync(() =>
            {
                Assert.Equal(QuestStatus.Incomplete, Feature.Services.StateOf(session.Player!)!.Quests.Get(QuestInteractionFixture.QuestId)!.Status);
                if (complete) Feature.Services.KilledMonsterCredit(session.Player!, 90, ObjectGuid.WithEntry(HighGuid.Unit, 90, 42));
                return true;
            });
        }

        internal async Task<Creature> AddNearbyMobAsync()
        {
            Creature? mob = null;
            await host.World.InvokeAsync(() =>
            {
                var player = session.Player!;
                mob = new Creature(900030, new CreatureTemplate { Entry = 90, Name = "nearby hostile", Faction = 900012,
                    MinLevelHealth = 20, MaxLevelHealth = 20, DisplayIds = [49] }, null, CreatureContent.Empty, new Random(1));
                mob.SetPosition(player.X - 2, player.Y, player.Z, 0);
                player.Map!.AddObject(mob);
                return true;
            });
            await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(mob!.Guid), "nearby hostile visibility");
            return mob!;
        }

        public async ValueTask DisposeAsync()
        { session.Kick(); await session.ManagedClosed; await host.DisposeAsync(); }
    }

    private sealed class DistractorQuests(QuestInteractionFixture original) : IQuestContentStore
    {
        public async Task<QuestContent> LoadAsync(CancellationToken cancellationToken = default)
        {
            QuestContent content = await ((IQuestContentStore)original).LoadAsync(cancellationToken);
            var relation = new CreatureQuestRelation { Id = DistractorEntry, Quest = QuestInteractionFixture.UnrelatedId };
            return content with { Starters = [.. content.Starters, relation], Enders = [.. content.Enders, relation] };
        }
    }

    private sealed class FarGiverStore(QuestInteractionFixture original, bool secondEnder = false) : ICreatureDataStore
    {
        public async Task<CreatureContent> LoadAsync(CancellationToken cancellationToken = default)
        {
            CreatureContent content = await ((ICreatureDataStore)original).LoadAsync(cancellationToken);
            CreatureSpawn[] spawns = content.GetSpawns(0).Select(spawn => spawn with { X = -8799.95f }).ToArray();
            if (secondEnder) spawns = [.. spawns, .. spawns.Select(spawn => spawn with { Guid = spawn.Guid + 1000, X = -8700f })];
            return new CreatureContent(content.Templates, spawns, [], [], []);
        }
    }

    private sealed class Walled(float height) : ILineOfSight
    {
        public bool Enabled => true;
        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => false;
        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        { hit = from; return true; }
        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => height;
        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        { info = default; return false; }
    }

    private sealed class ManualReturnClock : IWorldFeature
    {
        public void Attach(ArcaneCore.Game.Maps.WorldRuntime world) => world.UseManualClock();
    }

    private sealed class FlatFloor(float height) : ILineOfSight
    {
        public bool Enabled => true;
        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;
        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        { hit = to; return false; }
        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => height;
        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        { info = default; return false; }
    }
}
