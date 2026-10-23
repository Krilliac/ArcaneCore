using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.CreatureMovement;

/// <summary>The bookkeeping behind the follow-link pass: the follow-slave set follows entry changes, the spawn-guid index survives duplicates.</summary>
public sealed class CreatureLinkIndexTests
{
    private const uint Master = 1, Subject = 2, SpawnSlave = 3;
    private const uint MasterEntry = 330, LinkedEntry = 393, PlainEntry = 394, SpawnSlaveEntry = 395;

    private static CreatureContent Linked() => new(
        [Template(MasterEntry) with { Civilian = true }, Template(LinkedEntry) with { Civilian = true },
         Template(PlainEntry) with { Civilian = true }, Template(SpawnSlaveEntry) with { Civilian = true }],
        [Spawn(Master, MasterEntry, 0, 0), Spawn(Subject, PlainEntry, 8, 0), Spawn(SpawnSlave, SpawnSlaveEntry, 0, 8)], [], [], [],
        links: [new CreatureLink(SpawnSlave, Master, 0x200)],
        templateLinks: [new CreatureTemplateLink(LinkedEntry, 0, MasterEntry, 0x200, 0)]);

    private static Creature BySpawn(CreatureMapSystem system, uint guid) => system.Creatures.Single(c => c.Spawn?.Guid == guid);

    [Fact]
    public void EntryChangeIntoALinkedEntry_JoinsTheFollowSet_AndOutOfItLeavesIt()
    {
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(Linked());
        using (world)
        {
            AddPlayer(world, 1, 0, 10);
            Run(world, 200);
            Creature master = BySpawn(system, Master);
            Creature subject = BySpawn(system, Subject);
            Assert.False(system.IsFollowSlave(subject));
            Assert.NotEqual(MovementGeneratorType.Follow, subject.Motion.CurrentType);

            Assert.True(system.UpdateEntry(subject, LinkedEntry));
            Assert.True(system.IsFollowSlave(subject));
            Run(world, 200);
            Assert.Equal(MovementGeneratorType.Follow, subject.Motion.CurrentType);
            Assert.Same(master, subject.Motion.TargetedUnit);

            Assert.True(system.UpdateEntry(subject, PlainEntry));
            Assert.False(system.IsFollowSlave(subject));
            subject.Motion.Remove(MovementGeneratorType.Follow);
            Run(world, 200);
            Assert.NotEqual(MovementGeneratorType.Follow, subject.Motion.CurrentType);
        }
    }

    [Fact]
    public void DuplicateSpawnGuid_IsPromotedWhenTheFirstLeaves_AndTheIndexEmptiesWithTheLast()
    {
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(Linked());
        using (world)
        {
            AddPlayer(world, 1, 0, 10);
            Run(world, 200);
            Creature first = BySpawn(system, Master);
            Creature slave = BySpawn(system, SpawnSlave);
            Assert.Same(first, slave.Motion.TargetedUnit);

            // Same spawn row, another entry: a different ObjectGuid, so it can share the spawn guid with the first.
            var second = new Creature(Master, Template(332) with { Civilian = true }, first.Spawn, Linked(), new Random(1));
            Assert.NotEqual(first.Guid, second.Guid);
            system.IndexCreature(second);
            Assert.Equal(1, system.SpawnDuplicateCount);

            system.Despawn(first); // leaves _creatures and the index; the second takes its place
            Assert.True(system.HasSpawnIndex(Master));
            Assert.Equal(0, system.SpawnDuplicateCount);
            slave.Motion.Remove(MovementGeneratorType.Follow);
            system.UpdateLinkedFollowers();
            Assert.Same(second, slave.Motion.TargetedUnit);

            system.UnindexCreature(second);
            Assert.False(system.HasSpawnIndex(Master));
            Assert.Equal(0, system.SpawnDuplicateCount);
        }
    }
}
