using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureMovement;

/// <summary>vmangos CreatureLinkingHolder::SetFollowing / TryFollowMaster (CreatureLinkingMgr.cpp:711-794).</summary>
public sealed class CreatureLinkFollowTests
{
    [Fact]
    public void ClassicDbSpawnLink_FollowsItsMaster_AndStopsWhenMasterDies()
    {
        CreatureContent content = new(
            [Template(330) with { Civilian = true }, Template(390) with { Civilian = true }],
            [Spawn(13990, 330, 0, 0), Spawn(13991, 390, 8, 0)], [], [], [],
            links: [new CreatureLink(13991, 13990, 515)]); // ClassicDB z2815 has these flags with FOLLOW 0x200.
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(content);
        using (world)
        {
            (Player player, _) = AddPlayer(world, 1, 0, 10);
            Creature master = system.Creatures.Single(c => c.Spawn?.Guid == 13990);
            Creature slave = system.Creatures.Single(c => c.Spawn?.Guid == 13991);
            Run(world, 100);
            Assert.Equal(MovementGeneratorType.Follow, slave.Motion.CurrentType);

            system.MoveTo(master, 18, 0, master.Z, run: false, finalOrientation: null);
            Run(world, 10_000);
            Assert.True(slave.X > 8f);

            master.Health = 1;
            master.Map!.Combat.Kill(player, master);
            Run(world, 100);
            Assert.NotEqual(MovementGeneratorType.Follow, slave.Motion.CurrentType);
        }
    }

    [Fact]
    public void ClassicDbTemplateLink_UsesMasterEntryOnItsMap()
    {
        CreatureContent content = new(
            [Template(330) with { Civilian = true }, Template(390) with { Civilian = true }],
            [Spawn(1, 330, 0, 0), Spawn(2, 390, 8, 0)], [], [], [],
            templateLinks: [new CreatureTemplateLink(390, 0, 330, 515, 0)]); // ClassicDB z2815 (390,0,330,515,0): one Princess on the map.
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(content);
        using (world)
        {
            AddPlayer(world, 1, 0, 10);
            Run(world, 100);
            Assert.Equal(MovementGeneratorType.Follow, system.Creatures.Single(c => c.Entry == 390).Motion.CurrentType);
        }
    }

    [Fact]
    public void TemplateLinkWithoutSearchRange_RequiresOneMasterSpawn()
    {
        CreatureContent content = new(
            [Template(330) with { Civilian = true }, Template(390) with { Civilian = true }],
            [Spawn(1, 330, 0, 0), Spawn(2, 330, 12, 0), Spawn(3, 390, 8, 0)], [], [], [],
            templateLinks: [new CreatureTemplateLink(390, 0, 330, 0x200, 0)]);
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(content);
        using (world)
        {
            AddPlayer(world, 1, 0, 10);
            Run(world, 100);
            Assert.NotEqual(MovementGeneratorType.Follow, system.Creatures.Single(c => c.Entry == 390).Motion.CurrentType);
        }
    }
}
