using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Maps;
using Xunit;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>
/// Creature::ForcedDespawn(msDelay) (mangos-classic Creature.cpp, timeMSToDespawn branch) queues a separate delayed event per call, so
/// the earliest request wins: a script asking again later can shorten the wait but never push it back.
/// </summary>
public sealed class ForcedDespawnPostponeTests
{
    private const uint Entry = 10000;

    private sealed class Plain(Map map) : InstanceData(map);

    private static DungeonScriptTestKit Kit() => new(map => new Plain(map), [Entry], [Entry], []);

    [Fact]
    public void RepeatedRequestEveryTick_StillDespawnsAtTheFirstDeadline()
    {
        using var run = Kit();
        Creature creature = run.Creature(Entry);

        for (int elapsed = 0; elapsed < 4900; elapsed += 100)
        {
            run.Creatures.ForcedDespawn(creature, 5000);
            run.Tick(100);
        }

        Assert.True(creature.IsAlive);
        run.Creatures.ForcedDespawn(creature, 5000);
        run.Tick(100);
        run.Tick(100);
        Assert.False(creature.IsAlive);
    }

    [Fact]
    public void ShorterSecondRequest_BringsTheDespawnForward()
    {
        using var run = Kit();
        Creature creature = run.Creature(Entry);

        run.Creatures.ForcedDespawn(creature, 10000);
        run.Creatures.ForcedDespawn(creature, 2000);
        for (int i = 0; i < 19; i++) run.Tick(100);
        Assert.True(creature.IsAlive);
        run.Tick(100);
        run.Tick(100);
        Assert.False(creature.IsAlive);
    }

    [Fact]
    public void LongerSecondRequest_DoesNotPostponeTheDespawn()
    {
        using var run = Kit();
        Creature creature = run.Creature(Entry);

        run.Creatures.ForcedDespawn(creature, 2000);
        run.Creatures.ForcedDespawn(creature, 10000);
        for (int i = 0; i < 19; i++) run.Tick(100);
        Assert.True(creature.IsAlive);
        run.Tick(100);
        run.Tick(100);
        Assert.False(creature.IsAlive);
    }
}
