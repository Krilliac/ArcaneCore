using ArcaneCore.Game.Creatures;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Risk;

/// <summary>
/// The risk estimate's group signal through the real <see cref="PlayerbotRisk"/> path (docs/areas/playbots-groups.md, Cooperation, raid
/// escalation): a world elite too strong for a party raises a signal for the raid it needs, an ordinary elite one for a party, a creature
/// that is no elite never one beyond a party, and the search for the size costs a handful of estimates, not one per size.
/// </summary>
public sealed class PlayerbotRaidRiskWorldTests
{
    private static async Task<(PlayerbotGroupSignal? Signal, long Probes)> SignalAsync(uint entry, uint rank, uint health, float maxDamage = 14)
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        Creature mob = await world.OnWorldAsync(() => world.Spawn(RiskTestWorld.Template(entry, level: 4, health: health, minDamage: 10, maxDamage: maxDamage, rank: rank), 25, 0));
        await world.SeeAsync(mob);
        long before = PlayerbotRiskModel.GroupProbes;
        PlayerbotGroupSignal? signal = await world.OnWorldAsync(() =>
        {
            world.Brain.Risk.Assess(world.Player, mob, true, out _);
            return world.Brain.Risk.GroupSignal;
        });
        return (signal, PlayerbotRiskModel.GroupProbes - before);
    }

    [Fact]
    public async Task ARareEliteNeedingEight_RaisesASignalForEight_InAboutSixEstimates()
    {
        (PlayerbotGroupSignal? signal, long probes) = await SignalAsync(992001, rank: 2, health: 1700);
        Assert.NotNull(signal);
        Assert.Equal(8, signal.Value.Size);
        Assert.InRange(probes, 1, 8); // bisection over 2..40, not 39 estimates
    }

    [Fact]
    public async Task ABossNeedingThirtyThree_IsFoundInAtMostEightEstimates_NotThirtyTwo()
    {
        (PlayerbotGroupSignal? signal, long probes) = await SignalAsync(992004, rank: 3, health: 32000);
        Assert.NotNull(signal);
        Assert.Equal(33, signal.Value.Size);
        Assert.InRange(probes, 1, 8);
    }

    [Fact]
    public async Task AnOrdinaryGroupElite_StaysAPartyGoal()
    {
        (PlayerbotGroupSignal? signal, _) = await SignalAsync(992002, rank: 1, health: 300);
        Assert.NotNull(signal);
        Assert.InRange(signal.Value.Size, 2, 5);
    }

    [Fact]
    public async Task ACreatureThatIsNoRareEliteOrBoss_NeverAsksForMoreThanAParty()
    {
        (PlayerbotGroupSignal? signal, long probes) = await SignalAsync(992003, rank: 0, health: 2000);
        Assert.Null(signal); // it would need nine: only a rare elite or a boss may ask for a raid
        Assert.InRange(probes, 1, 4);
    }

    [Fact]
    public async Task APlainEliteNeedingEight_RaisesNoRaidSignal()
    {
        (PlayerbotGroupSignal? signal, long probes) = await SignalAsync(992005, rank: 1, health: 1700);
        Assert.Null(signal); // the same creature as a rare elite needs eight: a plain elite is passed over
        Assert.InRange(probes, 1, 4);
    }
}
