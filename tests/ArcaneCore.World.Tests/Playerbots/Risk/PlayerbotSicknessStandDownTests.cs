using ArcaneCore.Game.Creatures;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Risk;

/// <summary>
/// Under resurrection sickness (spell 15007: every stat's base and all damage -75%, ten minutes) a bot pulls nothing. Live 2026-10-11
/// (analysis bot-xp-analysis.md, 4.1): 61% of the running level 11+ population bots were sick at any moment, at 7.5 spirit-healer
/// revivals per running bot-hour; a sick bot that pulled died again, took the healer again and was sickened again.
/// </summary>
public sealed class PlayerbotSicknessStandDownTests
{
    [Fact]
    public async Task ASickBot_StandsDownEvenWhenRiskScoringIsDisabled()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync(options => options.Risk.Enabled = false);
        Creature lone = await world.OnWorldAsync(() =>
        {
            PlayerbotSicknessTestKit.Apply(world.Host.WorldServices, world.Player);
            return world.Spawn(RiskTestWorld.Template(991402), 0, 30);
        });
        await world.SeeAsync(lone);

        bool pulled = await world.ThinkUntilAsync(5_000,
            () => world.Brain.InspectionTarget is not null || world.Player.Combat.IsInCombat || lone.Combat.IsInCombat);

        Assert.False(pulled, "a sick bot pulled with risk scoring disabled: " + await StateAsync(world));
        Assert.Equal(PlayerbotGoalKind.Rest, await world.OnWorldAsync(() => world.Brain.Goal));
    }

    [Fact]
    public async Task SicknessBeginsDuringAnApproach_AbandonsThePendingPull()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        Creature lone = await world.OnWorldAsync(() => world.Spawn(RiskTestWorld.Template(991403), 0, 30));
        await world.SeeAsync(lone);
        Assert.True(await world.ThinkUntilAsync(2_000, () => ReferenceEquals(world.Brain.InspectionTarget, lone)),
            "the healthy bot did not select the target");
        Assert.False(await world.OnWorldAsync(() => world.Player.Combat.IsInCombat || lone.Combat.IsInCombat),
            "the fight began before sickness was applied");

        await world.OnWorldAsync(() => PlayerbotSicknessTestKit.Apply(world.Host.WorldServices, world.Player));
        Assert.True(await world.ThinkUntilAsync(2_000,
            () => world.Brain.InspectionTarget is null || lone.Combat.IsInCombat),
            "the pending pull continued while sick: " + await StateAsync(world));

        Assert.True(await world.OnWorldAsync(() => world.Brain.InspectionTarget is null),
            "the pending target survived sickness: " + await StateAsync(world));
        Assert.Equal(PlayerbotGoalKind.Rest, await world.OnWorldAsync(() => world.Brain.Goal));
        Assert.False(await world.OnWorldAsync(() => lone.Combat.IsInCombat));
    }

    /// <summary>
    /// A lone, easy creature 30 yards off that the healthy bot pulls at once (<see cref="PlayerbotRiskWorldTests"/>). Sick, the bot
    /// takes no target, does not fight, stands with goal Rest and is not reported stalled (the shortest stall bound, 10 s, passes four
    /// times over); once the sickness is gone it pulls the creature.
    /// </summary>
    [Fact]
    public async Task ASickBot_PullsNothing_RestsWithoutAStall_AndPullsOnceTheSicknessEnds()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync(options => options.StallSeconds = 10);
        Creature lone = await world.OnWorldAsync(() =>
        {
            PlayerbotSicknessTestKit.Apply(world.Host.WorldServices, world.Player);
            return world.Spawn(RiskTestWorld.Template(991401), 0, 30);
        });
        await world.SeeAsync(lone);

        bool pulled = await world.ThinkUntilAsync(40_000,
            () => world.Brain.InspectionTarget is not null || world.Player.Combat.IsInCombat || lone.Combat.IsInCombat);
        string state = await StateAsync(world);

        Assert.False(pulled, "the sick bot went for a fight: " + state);
        Assert.Equal(PlayerbotGoalKind.Rest, await world.OnWorldAsync(() => world.Brain.Goal));
        Assert.Null(await world.OnWorldAsync(() => world.Brain.LastStall));

        await world.OnWorldAsync(() =>
        {
            world.Host.WorldServices.GetRequiredService<SpellFeature>().System.RemoveAuras(world.Player, PlayerbotSicknessTestKit.Sickness.Id);
            return true;
        });
        Assert.True(await world.ThinkUntilAsync(30_000, () => ReferenceEquals(world.Brain.InspectionTarget, lone)),
            "the bot did not pull once well: " + await StateAsync(world));
    }

    private static Task<string> StateAsync(RiskTestWorld world) => world.OnWorldAsync(() =>
        $"goal {world.Brain.Goal} target {world.Brain.InspectionTarget?.Entry} combat {world.Player.Combat.IsInCombat} "
        + $"sick {PlayerbotSickness.IsSick(world.Session, world.Player)} stall {world.Brain.LastStall ?? "none"} risk {world.Brain.RiskReport}");
}
