using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots.Risk;

/// <summary>
/// Waiting to recover before a pull (<see cref="PlayerbotRisk.KeepWaiting"/>, goal Rest) without food or drink. Live 2026-10-10:
/// Wfbotcx (level 14) took the spirit healer after a death loop in Duskwood, was revived at the Darkshire graveyard at half health
/// with resurrection sickness (spell 15007: every stat -75% for four minutes at level 14), and stood there with goal Rest until the
/// stall watch reported <c>stalled 120s: goal=Rest</c>. vmangos Player::RegenerateHealth: health regenerates from spirit (a warrior's
/// GetRegenHPPerSpirit is spirit * 1.26 - 22.6 per two-second tick, nothing at all with the sickness's spirit), times 1.5 when not
/// standing. The bot stood (no food, so it never sat), its regeneration was not progress for the stall watch, and a wait that
/// recovered nothing ran to its end and was begun again by the next pull verdict.
/// </summary>
public sealed class PlayerbotRestStallTests(ITestOutputHelper output)
{
    /// <summary>
    /// Slow regeneration (spirit just above the warrior's regeneration threshold): the waiting bot sits (half as much again per tick),
    /// its regeneration counts as progress, so no stall is reported, and it reaches the recovery threshold. The stall bound is the
    /// shortest allowed (10 s), well below the wait, so a wait that is not progress would be reported.
    /// </summary>
    [Fact]
    public async Task ABotWaitingToRecoverWithoutFood_Sits_AndIsNotReportedStalled_WhileItRegenerates()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync(options => options.StallSeconds = 10);
        Creature lone = await SetUpAsync(world, spirit: 19);
        bool sat = false;
        bool recovered = await world.ThinkUntilAsync(300_000, () => world.Brain.Risk.Recovered(world.Player),
            () => sat |= world.Player.StandState == StandState.Sit && world.Brain.Risk.Waiting);
        string state = await StateAsync(world);
        output.WriteLine(state);

        Assert.True(sat, "the bot never sat down to wait: " + state);
        Assert.True(recovered, "the bot never recovered: " + state);
        Assert.Null(await world.OnWorldAsync(() => world.Brain.LastStall));
        Assert.True(lone.IsAlive);
    }

    /// <summary>
    /// No regeneration at all (spirit below the warrior's threshold, as under resurrection sickness): the wait ends instead of running
    /// on with goal Rest, the bot stands up and goes on with something else, and no Rest stall is reported.
    /// </summary>
    [Fact]
    public async Task ABotThatRegeneratesNothing_StopsWaiting_StandsUp_AndIsNotReportedStalledResting()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await SetUpAsync(world, spirit: 10);
        Assert.True(await world.ThinkUntilAsync(10_000, () => world.Brain.Risk.Waiting), "the bot never began to wait: " + await StateAsync(world));

        bool leftRest = await world.ThinkUntilAsync(150_000,
            () => !world.Brain.Risk.Waiting && world.Brain.Goal != PlayerbotGoalKind.Rest && world.Player.StandState == StandState.Stand);
        string state = await StateAsync(world);
        output.WriteLine(state);

        Assert.True(leftRest, "the bot is still resting: " + state);
        string? stall = await world.OnWorldAsync(() => world.Brain.LastStall);
        Assert.False(stall?.Contains("goal=Rest", StringComparison.Ordinal) == true, stall);
    }

    /// <summary>
    /// A warrior at 30% health with <paramref name="spirit"/>, no food, and a lone creature in sight that it would pull at full health
    /// (the verdict is to recover first). Returns the creature.
    /// </summary>
    private static async Task<Creature> SetUpAsync(RiskTestWorld world, uint spirit)
    {
        Creature lone = await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            Assert.Equal(Class.Warrior, player.Class);
            player.SetUInt32(UpdateFields.UnitFieldStat0 + 4, spirit);
            player.Health = player.MaxHealth * 3 / 10;
            return world.Spawn(RiskTestWorld.Template(991301), 0, -40);
        });
        await world.SeeAsync(lone);
        return lone;
    }

    private static Task<string> StateAsync(RiskTestWorld world) => world.OnWorldAsync(() =>
        $"goal {world.Brain.Goal} waiting {world.Brain.Risk.Waiting} health {world.Player.Health}/{world.Player.MaxHealth} "
        + $"stand {world.Player.StandState} stall {world.Brain.LastStall ?? "none"} risk {world.Brain.RiskReport}");
}
