using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Tests;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The per-think scans a profile of 400 managed bots on the real terrain put on top of the bot feature's allocations (perf-tick
/// 2026-10-20, docs/integration/perf-tick-20261020.md): the death-loop check of every creature in sight at a target choice, the
/// defensive-target pick of a fighting bot and the quest-travel candidate scan. Each answers as before and allocates nothing once warm.
/// </summary>
public sealed class PlayerbotThinkAllocationTests
{
    private static readonly Vector3 At = new(1900f, -450f, 35f);

    [Fact]
    public void CoversDeathLoop_AnswersAsBefore_AndAllocatesNothing()
    {
        var hazards = new PlayerbotHazards();
        hazards.Add(0, At + new Vector3(100f, 0f, 0f), PlayerbotHazards.DeathYards, nowMs: 1_000, seconds: 300, "death");
        Assert.False(hazards.RememberDeathLoop(0, At, 1_000, 300, [6]));
        Vector3 inside = At + new Vector3(2f, 0f, 0f);
        Vector3 outside = At + new Vector3(200f, 0f, 0f);
        Assert.True(hazards.CoversDeathLoop(0, inside, 1_000));
        Assert.False(hazards.CoversDeathLoop(1, inside, 1_000));
        Assert.False(hazards.CoversDeathLoop(0, outside, 1_000));
        // A death mark is not a death-loop zone (PlayerbotPlaceHazardRouteTests).
        Assert.False(hazards.CoversDeathLoop(0, At + new Vector3(100f, 0f, 0f), 1_000));

        bool covered = false;
        Action window = () =>
        {
            for (int i = 0; i < 256; i++)
                covered = hazards.CoversDeathLoop(0, inside, 1_000) & !hazards.CoversDeathLoop(0, outside, 1_000);
        };
        AllocationProbe.AssertEveryCleanWindowAtMost(window, 0, "CoversDeathLoop x512");
        Assert.True(covered);
    }

    [Fact]
    public async Task FindDefensiveAttacker_TakesTheNearestAttacker_AndAllocatesNothing()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 500, Risk = { Enabled = false } });
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                Creature far = AddHostile(host, player, 993901, 4, "far attacker");
                Creature near = AddHostile(host, player, 993902, 1.5f, "near attacker");
                Creature farther = AddHostile(host, player, 993903, 6, "farther attacker");
                foreach (Creature attacker in new[] { far, near, farther })
                {
                    Assert.True(player.Map!.Combat.Attack(attacker, player));
                    player.Map!.Combat.DealDamage(attacker, player, 1);
                }

                Assert.Equal(3, player.Combat.Attackers.Count);
                Assert.Same(near, brain.FindDefensiveAttacker(player));

                Creature? found = null;
                Action window = () =>
                {
                    for (int i = 0; i < 64; i++) found = brain.FindDefensiveAttacker(player);
                };
                AllocationProbe.AssertEveryCleanWindowAtMost(window, 0, "FindDefensiveAttacker x64");
                Assert.Same(near, found);
                return true;
            });
        }
        finally
        {
            brain.Stop();
            session.Kick();
            await session.ManagedClosed;
        }
    }

    [Fact]
    public async Task HasQuestCandidate_OnAWarmList_AllocatesNothing()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            var fixture = new QuestTravelFixture(-8648.95f);
            services.AddSingleton<ArcaneCore.Kernel.Quests.IQuestContentStore>(fixture);
            services.AddSingleton<ICreatureDataStore>(fixture);
        });
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            host.WorldServices.GetRequiredService<QuestNpcFeature>().Options.OrdinaryRewardQuestIds = [QuestTravelFixture.QuestId];
            await host.World.InvokeAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor());
                Player player = session.Player!;
                var destinations = new PlayerbotWorldDestinations(session, new PlayerbotOptions { Enabled = true });
                Assert.True(destinations.HasQuestCandidate(player));

                bool advisable = false;
                Action window = () =>
                {
                    for (int i = 0; i < 64; i++) advisable = destinations.HasQuestCandidate(player);
                };
                AllocationProbe.AssertEveryCleanWindowAtMost(window, 0, "HasQuestCandidate x64");
                Assert.True(advisable);
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task HasQuestCandidate_OverAnIneligibleGiver_AllocatesNothing()
    {
        // The giver's quest ends at a far ender (PlayerbotQuestTravelPriorityTests.DistantSameMapEnder_IsIneligibleForOffscreenTravel):
        // every scan marks the giver ineligible in its scratch set.
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            var fixture = new QuestTravelFixture(-8648.95f, distantEnder: true);
            services.AddSingleton<ArcaneCore.Kernel.Quests.IQuestContentStore>(fixture);
            services.AddSingleton<ICreatureDataStore>(fixture);
        });
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                var destinations = new PlayerbotWorldDestinations(session, new PlayerbotOptions { Enabled = true });
                Assert.False(destinations.HasQuestCandidate(player));

                bool advisable = true;
                Action window = () =>
                {
                    for (int i = 0; i < 64; i++) advisable = destinations.HasQuestCandidate(player);
                };
                AllocationProbe.AssertEveryCleanWindowAtMost(window, 0, "HasQuestCandidate (ineligible giver) x64");
                Assert.False(advisable);
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    private static Creature AddHostile(WorldTestHost host, Player player, uint low, float offset, string name)
    {
        var creature = new Creature(low, new CreatureTemplate
        {
            Entry = low, Name = name, CreatureType = 1, MinLevel = 1, MaxLevel = 1, MinLevelHealth = 20, MaxLevelHealth = 20,
        }, null, CreatureContent.Empty, new Random((int)low));
        creature.Relocate(player.X + offset, player.Y, player.Z, 0, host.World.NowMs);
        player.Map!.AddObject(creature);
        return creature;
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
}
