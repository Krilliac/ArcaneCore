using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Spells;
using ArcaneCore.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>Contract tests for the advisory seam used to select offscreen quest travel.</summary>
public sealed class PlayerbotQuestTravelPriorityTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task BrainPrefersOffscreenQuestTravelWhilePreservingCombatAndCasts(int activeState)
    {
        await using WorldTestHost host = Start(new QuestTravelFixture(-8648.95f));
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            QuestNpcFeature feature = host.WorldServices.GetRequiredService<QuestNpcFeature>();
            feature.Options.OrdinaryRewardQuestIds = [QuestTravelFixture.QuestId];
            Creature? hostile = null;
            await host.World.InvokeAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new QuestOpenPathfinder());
                Player player = session.Player!;
                hostile = new Creature(6, new CreatureTemplate { Entry = 910099, Name = "idle hostile",
                    MinLevelHealth = 20, MaxLevelHealth = 20 }, null, CreatureContent.Empty, new Random(1));
                hostile!.Relocate(player.X + 8, player.Y, player.Z, 0, host.World.NowMs);
                player.Map!.AddObject(hostile);
                return true;
            });
            await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(hostile!.Guid), "idle hostile visibility");
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                float before = player.X;
                var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true });
                if (activeState == 1)
                {
                    session.ManagedBudget = new ManagedActionBudget(1);
                    Assert.True(session.TryManagedAction(WorldOpcode.CmsgAttackswing,
                        PlayerbotNavigation.GuidPayload(hostile!.Guid.Value)));
                    player.Map!.Combat.DealDamage(hostile, player, 1);
                    Assert.True(player.Combat.IsInCombat);
                }
                else if (activeState == 2)
                {
                    SpellSystem spells = host.WorldServices.GetRequiredService<SpellFeature>().System;
                    spells.Store = new SpellStore([new SpellInfo
                    {
                        Id = 993282, RangeIndex = 1, Range = new SpellRange(0, 0),
                        CastTime = new SpellCastTime(5000, 0, 0),
                        Effects = [new SpellEffectInfo { Effect = SpellEffectName.Heal,
                            BasePoints = 4, BaseDice = 1, DieSides = 1, TargetA = SpellImplicitTarget.UnitCaster }, new(), new()],
                    }], [], []);
                    Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(player, 993282, SpellCastTargets.ForSelf(), triggered: false));
                }
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.Same(hostile, PlayerbotBrain.FindTarget(player));
                brain.Update(1_000);
                if (activeState == 0)
                {
                    Assert.Equal(PlayerbotGoalKind.Quest, brain.Goal);
                    Assert.Equal(QuestTravelFixture.QuestId, brain.QuestId);
                    Assert.Null(player.Combat.Victim);
                    Assert.Equal(before, player.X);
                    PlayerbotMotion.ElapseForTests(player, 500);
                    session.ManagedBudget = new ManagedActionBudget(1);
                    brain.Update(500);
                    Assert.NotEqual(before, player.X);
                }
                else if (activeState == 1)
                {
                    Assert.Same(hostile, player.Combat.Victim);
                    Assert.NotEqual(PlayerbotGoalKind.Quest, brain.Goal);
                }
                else
                {
                    Assert.Equal(before, player.X);
                    Assert.NotNull(host.WorldServices.GetRequiredService<SpellFeature>().System.GetState(player.Guid)!.CurrentCast);
                }
                brain.Stop();
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task EligibleOffscreenQuestIsAdvisableAndUsesNormalNavigation()
    {
        await using WorldTestHost host = Start(new QuestTravelFixture(-8648.95f));
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            QuestNpcFeature feature = host.WorldServices.GetRequiredService<QuestNpcFeature>();
            feature.Options.OrdinaryRewardQuestIds = [QuestTravelFixture.QuestId];
            await host.World.InvokeAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor());
                Player player = session.Player!;
                var destinations = new PlayerbotWorldDestinations(session, new PlayerbotOptions { Enabled = true });
                Assert.True(destinations.HasQuestCandidate(player));
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(destinations.Update(player, 0, 500));
                Assert.Equal(QuestTravelFixture.Entry, destinations.TargetEntry);
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task AllowlistAndVisibleEligibilityKeepTravelAdvisoryFalse()
    {
        await using WorldTestHost host = Start(new QuestTravelFixture(-8948.95f));
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            QuestNpcFeature feature = host.WorldServices.GetRequiredService<QuestNpcFeature>();
            await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(QuestTravelFixture.Guid), "questgiver visibility");
            await host.World.InvokeAsync(() =>
            {
                var destinations = new PlayerbotWorldDestinations(session, new PlayerbotOptions { Enabled = true });
                feature.Options.OrdinaryRewardQuestIds = [];
                Assert.False(destinations.HasQuestCandidate(session.Player!));
                feature.Options.OrdinaryRewardQuestIds = [QuestTravelFixture.QuestId];
                Assert.False(destinations.HasQuestCandidate(session.Player!));
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task UnreachableCandidateIsBlockedAndBacksOffAfterOnePlanAttempt()
    {
        await using WorldTestHost host = Start(new QuestTravelFixture(-8648.95f));
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            QuestNpcFeature feature = host.WorldServices.GetRequiredService<QuestNpcFeature>();
            feature.Options.OrdinaryRewardQuestIds = [QuestTravelFixture.QuestId];
            PlayerbotWorldDestinations destinations = null!;
            NoPathfinder pathfinder = null!;
            await host.World.InvokeAsync(() =>
            {
                pathfinder = new NoPathfinder();
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: pathfinder);
                Player player = session.Player!;
                destinations = new PlayerbotWorldDestinations(session, new PlayerbotOptions { Enabled = true });
                Assert.True(destinations.HasQuestCandidate(player));
                Assert.False(destinations.Update(player, 0, 500));
                Assert.Equal(1, pathfinder.Calls);
                Assert.False(destinations.HasQuestCandidate(player));
                Assert.Equal(1, pathfinder.Calls);
                return true;
            });
            uint failedAt = host.World.NowMs;
            await host.WaitForWorldAsync(() => unchecked(host.World.NowMs - failedAt) >= 2_100,
                "quest travel backoff to expire");
            await host.World.InvokeAsync(() =>
            {
                Assert.True(destinations.HasQuestCandidate(session.Player!));
                Assert.False(destinations.Update(session.Player!, 0, 500));
                Assert.Equal(2, pathfinder.Calls);
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    private static WorldTestHost Start(QuestTravelFixture fixture) => WorldTestHost.Start(configureServices: services =>
    {
        services.AddSingleton<IQuestContentStore>(fixture);
        services.AddSingleton<ICreatureDataStore>(fixture);
    });

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

    private sealed class NoPathfinder : IPathfinder
    {
        public bool Enabled => true;
        public int Calls { get; private set; }
        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            Calls++;
            return PathResult.None(start);
        }
    }

    private sealed class QuestOpenPathfinder : IPathfinder
    {
        public bool Enabled => true;
        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => PathResult.StraightLine(start, end, PathType.Normal);
    }
}

internal sealed class QuestTravelFixture(float spawnX) : IQuestContentStore, ICreatureDataStore
{
    internal const uint QuestId = 910001;
    internal const uint Entry = 910010;
    internal static readonly ObjectGuid Guid = ObjectGuid.WithEntry(HighGuid.Unit, Entry, 910020);

    public Task<QuestContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new QuestContent(
        [new QuestTemplate { Entry = QuestId, Method = 2, MinLevel = 1, QuestLevel = 1, Title = "Travel quest" }],
        [new CreatureQuestRelation { Id = Entry, Quest = QuestId }],
        [new CreatureQuestRelation { Id = Entry, Quest = QuestId }]));

    Task<CreatureContent> ICreatureDataStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new CreatureContent(
        [new CreatureTemplate { Entry = Entry, Name = "Travel questgiver", NpcFlags = 2, DisplayIds = [49] }],
        [new CreatureSpawn { Guid = 910020, Entry = Entry, MapId = 0, X = spawnX, Y = -132.49f, Z = 83.53f }], [], [], []));
}
