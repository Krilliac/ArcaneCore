using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Battlegrounds;
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

    private static AlteracValley StartAlteracValley(Player ally, params Player[] hordes)
    {
        var av = new AlteracValley(new BattlegroundTemplate
        {
            Type = BattlegroundType.AlteracValley, MapId = ally.MapId, Name = "Alterac Valley",
            MinPlayersPerTeam = 1, MaxPlayersPerTeam = 40, MinLevel = 1, MaxLevel = 60,
        }, bracket: 0, instanceId: ally.Map!.InstanceId, clientInstanceId: 1, new BattlegroundOptions(), new BattlegroundPorts());
        av.AddPlayer(ally.Guid, Team.Alliance);
        foreach (Player horde in hordes) av.AddPlayer(horde.Guid, Team.Horde);
        for (int i = 0; i < 4; i++) av.Update(60_000);
        Assert.Equal(BattlegroundStatus.InProgress, av.Status);
        return av;
    }

    private static Creature SpawnNpc(WorldTestHost host, Player near, uint entry, float dx, float dz = 0f, bool civilian = false)
    {
        var creature = new Creature(entry, new CreatureTemplate
        {
            Entry = entry, Name = $"npc-{entry}", CreatureType = 1, MinLevel = 1, MaxLevel = 1, MinLevelHealth = 500, MaxLevelHealth = 500, Civilian = civilian,
        }, null, CreatureContent.Empty, new Random((int)entry));
        creature.Relocate(near.X + dx, near.Y, near.Z + dz, 0, host.World.NowMs);
        near.Map!.AddObject(creature);
        near.VisibleObjects.Add(creature.Guid);
        return creature;
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

    [Fact]
    public async Task SelectEnemy_DoesNotPickAnOutOfRangeCandidateOnTheBotsLevel_AfterAnInRangeOneOnAnotherLevel()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        (WorldSession bot, WorldSession enemySession) = await PlayerbotPvpTestKit.EnterPairAsync(host, 4f);
        WorldSession farSession = await PlayerbotPvpTestKit.EnterAsync(host, "PVPFAR", "Pvpfar", PlayerbotPvpTestKit.Orc);
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = bot.Player!, nearOtherLevel = enemySession.Player!, farSameLevel = farSession.Player!;
                PlayerbotPvpTestKit.Flag(player);
                PlayerbotPvpTestKit.Flag(nearOtherLevel);
                PlayerbotPvpTestKit.Flag(farSameLevel);
                AlteracValley av = StartAlteracValley(player, nearOtherLevel, farSameLevel);
                var ai = new PlayerbotBattlegroundAI(bot, new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 500 });

                // 1. NPC candidates: an in-range NPC on another level (distance ~25.3 <= 30) followed by an out-of-range NPC on the bot's level (50 > 30).
                Creature nearNpc = SpawnNpc(host, player, 994101, dx: 4f, dz: 25f);
                Creature farNpc = SpawnNpc(host, player, 994102, dx: 50f, dz: 0f);
                player.VisibleObjects.Clear();
                player.VisibleObjects.Add(nearNpc.Guid);
                player.VisibleObjects.Add(farNpc.Guid);

                Prepare(bot);
                ai.Play(player, av, Team.Alliance, PlayerbotBattlegroundRole.Attacker, 500);
                Assert.Equal("fighting", ai.Activity);
                Assert.Equal(nearNpc.Entry, ai.TargetEntry);

                // 2. Player candidates: an in-range player on another level (distance ~25.3 <= 30) followed by an out-of-range player on the bot's level (50 > 30).
                nearOtherLevel.Relocate(player.X + 4f, player.Y, player.Z + 25f, 0, host.World.NowMs);
                farSameLevel.Relocate(player.X + 50f, player.Y, player.Z, 0, host.World.NowMs);
                player.VisibleObjects.Clear();
                player.VisibleObjects.Add(nearOtherLevel.Guid);
                player.VisibleObjects.Add(farSameLevel.Guid);

                Prepare(bot);
                ai.Play(player, av, Team.Alliance, PlayerbotBattlegroundRole.Attacker, 500);
                Assert.Equal("fighting", ai.Activity);
                // The first think tried nearOtherLevel and refused the walk; the next think finds no in-range target (farSameLevel at 50 yd is out of range).
                Prepare(bot);
                ai.Play(player, av, Team.Alliance, PlayerbotBattlegroundRole.Attacker, 500);
                Assert.NotEqual("fighting", ai.Activity);
                return true;
            });
        }
        finally
        {
            farSession.Kick();
            enemySession.Kick();
            bot.Kick();
            await farSession.ManagedClosed;
            await enemySession.ManagedClosed;
            await bot.ManagedClosed;
        }
    }

    [Fact]
    public Task AlteracValleySelectEnemy_PrefersAPlayerOnAnotherLevel_OverAnNpcVisitedFirstOnTheBotsLevel() => Run(4f, (host, bot, player, enemy, _) => host.OnWorldAsync(() =>
    {
        PlayerbotPvpTestKit.Flag(player);
        PlayerbotPvpTestKit.Flag(enemy);
        AlteracValley av = StartAlteracValley(player, enemy);
        var ai = new PlayerbotBattlegroundAI(bot, new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 500 });

        // The NPC is on the bot's level and closer (6 yd); the enemy player is on another level (dx 4, dz 25 -> ~25.3 yd <= 30) and visited after the NPC.
        Creature npc = SpawnNpc(host, player, 994103, dx: 6f, dz: 0f);
        enemy.Relocate(player.X + 4f, player.Y, player.Z + 25f, 0, host.World.NowMs);
        player.VisibleObjects.Clear();
        player.VisibleObjects.Add(npc.Guid);
        player.VisibleObjects.Add(enemy.Guid);

        Prepare(bot);
        ai.Play(player, av, Team.Alliance, PlayerbotBattlegroundRole.Attacker, 500);
        Assert.Equal("fighting", ai.Activity);
        // TargetEntry is 0 for a player and npc.Entry for a creature: the player won over the creature despite being on another level.
        Assert.Equal(0u, ai.TargetEntry);
    }));

    [Fact]
    public Task AlteracValleyBoss_EngagedThroughPlay_IsNeverMarkedUnreachableOrSkipped() => Run(4f, (host, bot, player, enemy, _) => host.OnWorldAsync(() =>
    {
        AlteracValley av = StartAlteracValley(player);
        var ai = new PlayerbotBattlegroundAI(bot, new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 500 });

        // Control: a regular AV guard 20 yards away whose chase walk is refused on the first think is skipped on the second think.
        Creature guard = SpawnNpc(host, player, 994104, dx: 20f);
        player.VisibleObjects.Clear();
        player.VisibleObjects.Add(guard.Guid);
        Prepare(bot);
        ai.Play(player, av, Team.Alliance, PlayerbotBattlegroundRole.Attacker, 500);
        Assert.Equal("fighting", ai.Activity);
        Assert.Equal(guard.Entry, ai.TargetEntry);

        Prepare(bot);
        ai.Play(player, av, Team.Alliance, PlayerbotBattlegroundRole.Attacker, 500);
        Assert.NotEqual("fighting", ai.Activity);

        // An AV boss (the enemy captain) 20 yards away: Play -> SelectEnemy engages it with mustPursue and never skips it on subsequent thinks.
        Creature captain = SpawnNpc(host, player, 994105, dx: 20f, civilian: true);
        PlayerbotAlteracValleyState.For(av).Captains[1] = captain;
        player.VisibleObjects.Clear();
        player.VisibleObjects.Add(captain.Guid);

        Prepare(bot);
        ai.Play(player, av, Team.Alliance, PlayerbotBattlegroundRole.Attacker, 500);
        Assert.Equal("fighting", ai.Activity);
        Assert.Equal(captain.Entry, ai.TargetEntry);

        Prepare(bot);
        ai.Play(player, av, Team.Alliance, PlayerbotBattlegroundRole.Attacker, 500);
        Assert.Equal("fighting", ai.Activity);
        Assert.Equal(captain.Entry, ai.TargetEntry);
    }));

    [Fact]
    public async Task FirstProvokingPlayer_SkipsAnUnreachableAttacker_AndReturnsTheNextHostilePlayerAttacker()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        (WorldSession bot, WorldSession firstSession) = await PlayerbotPvpTestKit.EnterPairAsync(host, 25f);
        WorldSession secondSession = await PlayerbotPvpTestKit.EnterAsync(host, "PVPSECOND", "Pvpsecond", PlayerbotPvpTestKit.Orc);
        var brain = new PlayerbotBrain(bot, new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 500, Risk = { Enabled = false } });
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = bot.Player!, first = firstSession.Player!, second = secondSession.Player!;
                second.Relocate(player.X + 2f, player.Y, player.Z, 0, host.World.NowMs);
                PlayerbotPvpTestKit.Flag(player);
                PlayerbotPvpTestKit.Flag(first);
                PlayerbotPvpTestKit.Flag(second);

                var map = player.Map!;
                Assert.True(map.Combat.Attack(first, player));
                map.Combat.DealDamage(first, player, 1);
                Assert.True(map.Combat.Attack(second, player));
                map.Combat.DealDamage(second, player, 1);

                // Think 1 tries the first attacker (25 yd away; no ground loaded, so the walk is refused and it is marked unreachable).
                Prepare(bot);
                brain.Update(500);
                Assert.Null(brain.InspectionPvpTarget);

                // Think 2 skips the unreachable first attacker and fights the second attacker in melee reach.
                Prepare(bot);
                brain.Update(500);
                Assert.Same(second, brain.InspectionPvpTarget);
                Assert.Same(second, player.Combat.Victim);
                return true;
            });
        }
        finally
        {
            brain.Stop();
            secondSession.Kick();
            firstSession.Kick();
            bot.Kick();
            await secondSession.ManagedClosed;
            await firstSession.ManagedClosed;
            await bot.ManagedClosed;
        }
    }
}
