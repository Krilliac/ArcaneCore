using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.TestSupport;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.CreatureMovement;

/// <summary>
/// The follow-link pass runs on every map tick of a map with follow links. Its cost must not grow with the number of creatures on the map:
/// a live heap dump of 1177 bots showed it building a Dictionary and a GroupBy over every creature each tick (LOH churn, 3 s gen2 pauses).
/// </summary>
public sealed class CreatureLinkFollowAllocationTests
{
    private const uint Master = 1, SpawnSlave = 2, TemplateSlave = 3, RangeSlave = 4, NearMaster = 5, FarMaster = 6;
    private const uint FillerEntry = 1000, FirstFiller = 100;

    private static CreatureContent LinkedContent(int fillers)
    {
        List<CreatureSpawn> spawns =
        [
            Spawn(Master, 330, 0, 0),
            Spawn(SpawnSlave, 390, 8, 0),
            Spawn(TemplateSlave, 391, 0, 8),
            Spawn(RangeSlave, 392, 0, -8),
            Spawn(NearMaster, 331, 5, -8),
            Spawn(FarMaster, 331, 15, -8),
        ];
        for (int i = 0; i < fillers; i++)
        {
            spawns.Add(Spawn((uint)(FirstFiller + i), FillerEntry, (i % 100) - 50, (i / 100 % 100) - 50));
        }

        return new CreatureContent(
            [Template(330) with { Civilian = true }, Template(331) with { Civilian = true }, Template(390) with { Civilian = true },
             Template(391) with { Civilian = true }, Template(392) with { Civilian = true }, Template(FillerEntry) with { Civilian = true }],
            spawns, [], [], [],
            links: [new CreatureLink(SpawnSlave, Master, 0x200)],
            templateLinks: [new CreatureTemplateLink(391, 0, 330, 0x200, 0), new CreatureTemplateLink(392, 0, 331, 0x200, 40)]);
    }

    private static Creature BySpawn(CreatureMapSystem system, uint guid) => system.Creatures.Single(c => c.Spawn?.Guid == guid);

    [Fact]
    public void LinkedFollowerUpdate_AllocatesNothingThatScalesWithTheCreatureCount()
    {
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(LinkedContent(5_000));
        using (world)
        {
            AddPlayer(world, 1, 0, 10);
            Run(world, 300);
            Assert.True(system.Creatures.Count >= 5_000);
            Assert.Equal(MovementGeneratorType.Follow, BySpawn(system, SpawnSlave).Motion.CurrentType);

            for (int i = 0; i < 10; i++) system.UpdateLinkedFollowers(); // warm-up: JIT, delegate caches

            Action window = () => { for (int i = 0; i < 50; i++) system.UpdateLinkedFollowers(); };
            AllocationProbe.AssertEveryCleanWindowAtMost(window, 4_096, $"50 follower updates with {system.Creatures.Count} creatures");

            // The template slave's master is gone: every update searches the map for another (the allocation-free candidate scan).
            system.KillCreature(BySpawn(system, Master));
            for (int i = 0; i < 10; i++) system.UpdateLinkedFollowers(); // warm-up of the searching path
            AllocationProbe.AssertEveryCleanWindowAtMost(window, 4_096, $"50 searching follower updates with {system.Creatures.Count} creatures");
        }
    }

    [Fact]
    public void SpawnLink_FollowsReFollowsAfterRespawnAndIsReleasedOnDeathAndDespawn()
    {
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(LinkedContent(50));
        using (world)
        {
            AddPlayer(world, 1, 0, 10);
            Run(world, 200);
            Creature master = BySpawn(system, Master);
            Creature slave = BySpawn(system, SpawnSlave);
            Assert.Equal(MovementGeneratorType.Follow, slave.Motion.CurrentType);
            Assert.Same(master, slave.Motion.TargetedUnit);

            system.KillCreature(master);
            Run(world, 200);
            Assert.NotEqual(MovementGeneratorType.Follow, slave.Motion.CurrentType);

            system.ForceRespawn(master);
            Run(world, 200);
            Assert.Equal(MovementGeneratorType.Follow, slave.Motion.CurrentType);
            Assert.Same(master, slave.Motion.TargetedUnit);

            system.Despawn(master);
            Run(world, 200);
            Assert.NotEqual(MovementGeneratorType.Follow, slave.Motion.CurrentType);
        }
    }

    [Fact]
    public void TemplateLink_UniqueMasterWithoutRange_AndNearestMasterInRange()
    {
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(LinkedContent(50));
        using (world)
        {
            AddPlayer(world, 1, 0, 10);
            Run(world, 200);

            // search_range 0: the single master of entry 330 on the map.
            Assert.Same(BySpawn(system, Master), BySpawn(system, TemplateSlave).Motion.TargetedUnit);

            // search_range 40: two masters of entry 331 are in range; the nearer spawn wins.
            Creature ranged = BySpawn(system, RangeSlave);
            Assert.Equal(MovementGeneratorType.Follow, ranged.Motion.CurrentType);
            Assert.Same(BySpawn(system, NearMaster), ranged.Motion.TargetedUnit);

            // The nearest master dies: it stays the nearest candidate (a dead one is not skipped), so the slave is released, not re-assigned.
            system.KillCreature(BySpawn(system, NearMaster));
            Run(world, 200);
            Assert.NotEqual(MovementGeneratorType.Follow, ranged.Motion.CurrentType);
        }
    }
}
