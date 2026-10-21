using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Combat;

/// <summary>
/// A PvP, battleground or defend fight whose walk is refused (no path to the target) remembers the target for 30 s so the next choice passes it
/// over (<see cref="PlayerbotPvpFight.Unreachable"/>); the flag carrier and the script's objectives are never set aside (mustPursue); a target on
/// the bot's own level is preferred (<see cref="PlayerbotPvpFight.IsBetterTarget"/>). The battleground AI and the brain pick through these.
/// </summary>
public sealed class PlayerbotPvpUnreachableTests
{
    private static async Task Run(float gap, Func<WorldTestHost, WorldSession, Player, Player, PlayerbotPvpFight, Task> body)
    {
        await using WorldTestHost host = WorldTestHost.Start();
        (WorldSession bot, WorldSession enemy) = await PlayerbotPvpTestKit.EnterPairAsync(host, gap);
        try
        {
            await host.WaitForWorldAsync(() => bot.Player!.VisibleObjects.Contains(enemy.Player!.Guid), "the enemy is in sight");
            var fight = new PlayerbotPvpFight(bot, new PlayerbotCombatSpells(bot));
            await body(host, bot, bot.Player!, enemy.Player!, fight);
        }
        finally
        {
            bot.Kick();
            enemy.Kick();
            await bot.ManagedClosed;
            await enemy.ManagedClosed;
        }
    }

    private static void Prepare(WorldSession bot)
    {
        bot.Services.GetRequiredService<SpellFeature>().System.Store = new SpellStore([], [], []);
        bot.ManagedBudget = new ManagedActionBudget(4);
    }

    [Fact]
    public Task ARefusedWalk_MarksTheTargetUnreachable_AndItIsSkippedUntilThirtySecondsPass() => Run(25f, (host, bot, player, enemy, fight) => host.OnWorldAsync(() =>
    {
        Prepare(bot);
        uint now = host.World.NowMs;
        fight.Fight(player, enemy, 500, (_, _, _) => false, () => { });

        Assert.Null(fight.Target);
        Assert.True(fight.IsSkipped(enemy));
        Assert.True(fight.Unreachable.Has(enemy.Guid, now + PlayerbotPvpFight.UnreachableMs - 2_000));
        // After the 30 s the same target may be chosen again.
        Assert.False(fight.Unreachable.Has(enemy.Guid, now + PlayerbotPvpFight.UnreachableMs + 2_000));
    }));

    [Fact]
    public Task AWalkThatSucceeds_MarksNothing() => Run(25f, (host, bot, player, enemy, fight) => host.OnWorldAsync(() =>
    {
        Prepare(bot);
        fight.Fight(player, enemy, 500, (_, _, _) => true, () => { });

        Assert.Same(enemy, fight.Target);
        Assert.False(fight.IsSkipped(enemy));
    }));

    [Fact]
    public Task TheFlagCarrierAndTheScriptsObjectives_AreNeverSetAside_ThoughTheWalkIsRefused() => Run(25f, (host, bot, player, enemy, fight) => host.OnWorldAsync(() =>
    {
        Prepare(bot);
        fight.Fight(player, enemy, 500, (_, _, _) => false, () => { }, mustPursue: true);

        Assert.Null(fight.Target);
        Assert.False(fight.IsSkipped(enemy));
        Assert.Equal(0, fight.Unreachable.Count);
    }));

    [Fact]
    public Task ForgettingTheMemory_LetsTheBotChooseTheTargetAgain() => Run(25f, (host, bot, player, enemy, fight) => host.OnWorldAsync(() =>
    {
        Prepare(bot);
        fight.Fight(player, enemy, 500, (_, _, _) => false, () => { });
        Assert.True(fight.IsSkipped(enemy));

        fight.Forget();

        Assert.False(fight.IsSkipped(enemy));
    }));

    [Fact]
    public Task ATargetOnTheBotsLevel_IsPreferredOverOneOnAnotherLevel_NearOrNot() => Run(4f, (host, bot, player, enemy, fight) => host.OnWorldAsync(() =>
    {
        // Same height: the same level. 25 yards above: another level (wall, cliff, another floor of an AV base).
        Assert.False(PlayerbotPvpFight.OnOtherLevel(player, enemy));
        enemy.Relocate(player.X + 4f, player.Y, player.Z + 25f, 0, host.World.NowMs);
        Assert.True(PlayerbotPvpFight.OnOtherLevel(player, enemy));

        // Of two candidates the one on the bot's level wins even when it is farther; of two on the same level the nearer wins.
        Assert.True(PlayerbotPvpFight.IsBetterTarget(candidateOtherLevel: false, candidateYards: 20f, bestOtherLevel: true, bestYards: 5f));
        Assert.False(PlayerbotPvpFight.IsBetterTarget(candidateOtherLevel: true, candidateYards: 5f, bestOtherLevel: false, bestYards: 20f));
        Assert.True(PlayerbotPvpFight.IsBetterTarget(false, 5f, false, 20f));
        Assert.False(PlayerbotPvpFight.IsBetterTarget(false, 20f, false, 5f));
        Assert.True(PlayerbotPvpFight.IsBetterTarget(true, 5f, true, 20f));
        // Equal in every key: the first met stays.
        Assert.False(PlayerbotPvpFight.IsBetterTarget(false, 10f, false, 10f));
    }));
}
