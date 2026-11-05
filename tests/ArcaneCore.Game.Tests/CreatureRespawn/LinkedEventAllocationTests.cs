using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.TestSupport;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.CreatureRespawn;

/// <summary>
/// A linked death or respawn event walks the map's creatures for the master's slaves. It walks a copy, because the event can respawn or
/// despawn creatures meanwhile; the copy lives in a pooled array, so the cost per event no longer includes an array the size of the map.
/// </summary>
public sealed class LinkedEventAllocationTests
{
    private const uint Boss = 1, Trash = 2, FillerEntry = 1000, FirstFiller = 100;
    private const uint RespawnOnRespawn = 0x80;

    [Fact]
    public void LinkedRespawnEvents_DoNotAllocateAnArrayTheSizeOfTheMap()
    {
        List<CreatureSpawn> spawns = [Spawn(Boss, 330, 0, 0), Spawn(Trash, 390, 8, 0)];
        for (int i = 0; i < 5_000; i++)
        {
            spawns.Add(Spawn((uint)(FirstFiller + i), FillerEntry, (i % 100) - 50, (i / 100 % 100) - 50));
        }

        CreatureContent content = new(
            [Template(330) with { Civilian = true }, Template(390) with { Civilian = true }, Template(FillerEntry) with { Civilian = true }],
            spawns, [], [], [], links: [new CreatureLink(Trash, Boss, RespawnOnRespawn)]);
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(content);
        using (world)
        {
            AddPlayer(world, 1, 0, 10);
            Run(world, 100);
            Creature boss = system.Creatures.Single(c => c.Spawn?.Guid == Boss);
            Creature trash = system.Creatures.Single(c => c.Spawn?.Guid == Trash);
            Assert.True(system.Creatures.Count >= 5_000);

            Action window = () =>
            {
                for (int i = 0; i < 5; i++)
                {
                    system.KillCreature(trash);
                    system.KillCreature(boss);
                    system.ForceRespawn(boss); // the link brings the trash back: a linked event walks every creature
                }
            };

            window(); // warm-up: JIT, pool arrays, delegate caches
            Assert.True(trash.IsAlive);
            // The old copy was 8 bytes x 5000 creatures = 40 KB or more per linked event, several events per cycle (> 1 MB per window).
            AllocationProbe.AssertEveryCleanWindowAtMost(window, 200_000, $"5 linked kill and respawn cycles with {system.Creatures.Count} creatures");
            Assert.True(trash.IsAlive);
        }
    }
}
