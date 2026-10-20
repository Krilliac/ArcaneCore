using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Playerbots.Combat;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotPvpDefenceTests
{
    private static async Task Run(Func<WorldTestHost, WorldSession, Player, PlayerbotBrain, Task> body)
    {
        await using WorldTestHost host = WorldTestHost.Start();
        (WorldSession botSession, WorldSession enemySession) = await PlayerbotPvpTestKit.EnterPairAsync(host);
        // Risk off: the opponent is level 1 as well, but this is about whom the bot fights, not whether it should.
        var brain = new PlayerbotBrain(botSession, new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 500, Risk = { Enabled = false } });
        try
        {
            await body(host, botSession, enemySession.Player!, brain);
        }
        finally
        {
            brain.Stop();
            botSession.Kick();
            enemySession.Kick();
            await botSession.ManagedClosed;
            await enemySession.ManagedClosed;
        }
    }

    private static void Think(WorldSession session, PlayerbotBrain brain)
    {
        session.Services.GetRequiredService<SpellFeature>().System.Store = new SpellStore([], [], []);
        session.ManagedBudget = new ManagedActionBudget(2);
        brain.Update(500);
    }

    [Fact]
    public Task BotHitsBackFlaggedPlayerWhoAttacksIt() => Run((host, session, enemy, brain) => host.OnWorldAsync(() =>
    {
        Player bot = session.Player!;
        Map map = bot.Map!;
        PlayerbotPvpTestKit.Flag(bot);
        PlayerbotPvpTestKit.Flag(enemy);
        Assert.True(map.Combat.Attack(enemy, bot));
        map.Combat.DealDamage(enemy, bot, 1);
        Assert.Contains(enemy, bot.Combat.Attackers);

        Think(session, brain);

        Assert.Same(enemy, brain.InspectionPvpTarget);
        Assert.Same(enemy, bot.Combat.Victim);
        Assert.Equal(PlayerbotGoalKind.Combat, brain.Goal);
        Assert.Null(brain.InspectionTarget);
    }));

    [Fact]
    public Task BotIgnoresFlaggedEnemyPlayerWhoDidNotAttack() => Run((host, session, enemy, brain) => host.OnWorldAsync(() =>
    {
        Player bot = session.Player!;
        Map map = bot.Map!;
        PlayerbotPvpTestKit.Flag(bot);
        PlayerbotPvpTestKit.Flag(enemy);

        Think(session, brain);

        Assert.Null(brain.InspectionPvpTarget);
        Assert.Null(bot.Combat.Victim);
        Assert.False(bot.Combat.IsInCombat);
    }));

    [Fact]
    public Task PlayerAttackerBeatsCreatureLinger() => Run((host, session, enemy, brain) => host.OnWorldAsync(() =>
    {
        Player bot = session.Player!;
        Map map = bot.Map!;
        PlayerbotPvpTestKit.Flag(bot);
        PlayerbotPvpTestKit.Flag(enemy);
        var creature = new Creature(993901, new CreatureTemplate
        {
            Entry = 993901, Name = "lingering attacker", CreatureType = 1,
            MinLevel = 1, MaxLevel = 1, MinLevelHealth = 20, MaxLevelHealth = 20,
        }, null, CreatureContent.Empty, new Random(993901));
        creature.Relocate(bot.X - 2, bot.Y, bot.Z, 0, host.World.NowMs);
        map.AddObject(creature);
        // The creature hit the bot and stopped: the bot is in combat with no victim and no attacker, the linger of PlayerbotDefensiveTargetTests.
        Assert.True(map.Combat.Attack(creature, bot));
        map.Combat.DealDamage(creature, bot, 1);
        Assert.True(map.Combat.AttackStop(creature));
        Assert.True(bot.Combat.IsInCombat);
        Assert.True(map.Combat.Attack(enemy, bot));
        map.Combat.DealDamage(enemy, bot, 1);

        Think(session, brain);

        Assert.Same(enemy, brain.InspectionPvpTarget);
        Assert.Same(enemy, bot.Combat.Victim);
    }));
}
