using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Party;
using ArcaneCore.World.Social;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Risk;

/// <summary>
/// A bot in a real player's group follows its master's lead (vmangos PartyBotAI): it does not weigh its fights or flee them on its
/// own judgement, however badly it fares; only a wiping group (<see cref="PlayerbotRiskOptions.PartyRetreatOnWipe"/>) sends it back.
/// The party AI is driven by hand, one think at a time, on the manual clock.
/// </summary>
public sealed class PlayerbotRiskPartyTests
{
    [Fact]
    public async Task APartyBot_StaysInItsMastersFight_UntilTheGroupWipes()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await using WorldTestClient masterClient = await world.Host.EnterWorldAsync("RISKMASTER", "Riskmaster");
        var ai = new PlayerbotPartyAI(world.Session, world.Options);
        Creature mob = await world.OnWorldAsync(() =>
        {
            Player master = world.Host.World.FindOnlinePlayer("Riskmaster")!;
            Player bot = world.Player;
            master.Relocate(bot.X - 2, bot.Y, bot.Z, 0, world.Host.World.NowMs);
            GroupManager groups = world.Host.WorldServices.GetRequiredService<SocialFeature>().Context.Groups;
            groups.Invite(master, bot.Name);
            groups.Accept(bot);
            Assert.True(groups.AreInSameGroup(master.Guid, bot.Guid));
            Assert.True(ai.Drives(bot));
            return world.Spawn(RiskTestWorld.Template(991051, health: 4000, minDamage: 3, maxDamage: 4), 2, 0);
        });
        await world.SeeAsync(mob);
        await world.OnWorldAsync(() =>
        {
            world.Engage(mob);
            world.Player.Health = world.Player.MaxHealth * 30 / 100; // badly losing: a solo bot would retreat at once
            return true;
        });

        for (int think = 0; think < 40 && await world.OnWorldAsync(() => world.Player.IsAlive); think++)
        {
            await world.OnWorldAsync(() =>
            {
                world.Session.ManagedBudget = new ManagedActionBudget(8);
                ai.Update(world.Player, RiskTestWorld.ThinkMs);
                Assert.False(ai.Retreat.Active, "the party bot fled its master's fight");
                Assert.NotEqual(PlayerbotGoalKind.Retreat, ai.Goal);
                return true;
            });
            await world.Host.World.AdvanceClockAsync(RiskTestWorld.ThinkMs);
        }

        Assert.Equal(0, await world.OnWorldAsync(() => ai.Retreat.Count));
        Assert.Equal("decision=follow-master", await world.OnWorldAsync(() => ai.RiskReport));

        // The master falls: the group is wiping, and the bot gets out.
        await world.OnWorldAsync(() =>
        {
            Player master = world.Host.World.FindOnlinePlayer("Riskmaster")!;
            master.Map!.Combat.DealDamage(mob, master, master.Health, direct: false);
            Assert.False(master.IsAlive);
            world.Player.Health = Math.Max(world.Player.Health, world.Player.MaxHealth / 2);
            world.Session.ManagedBudget = new ManagedActionBudget(8);
            ai.Update(world.Player, RiskTestWorld.ThinkMs);
            Assert.True(ai.Retreat.Active, "the bot stayed in a wiped group's fight");
            Assert.Equal(PlayerbotGoalKind.Retreat, ai.Goal);
            Assert.Equal("master-dead", ai.Retreat.Reason);
            return true;
        });
    }

    [Fact]
    public async Task WithPartyRetreatOnWipeOff_APartyBotStaysEvenInAWipe()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync(options => options.Risk.PartyRetreatOnWipe = false);
        await using WorldTestClient masterClient = await world.Host.EnterWorldAsync("RISKMASTERB", "Riskmasterb");
        var ai = new PlayerbotPartyAI(world.Session, world.Options);
        Creature mob = await world.OnWorldAsync(() =>
        {
            Player master = world.Host.World.FindOnlinePlayer("Riskmasterb")!;
            Player bot = world.Player;
            GroupManager groups = world.Host.WorldServices.GetRequiredService<SocialFeature>().Context.Groups;
            groups.Invite(master, bot.Name);
            groups.Accept(bot);
            Assert.True(ai.Drives(bot));
            return world.Spawn(RiskTestWorld.Template(991052, health: 4000, minDamage: 1, maxDamage: 1), 2, 0);
        });
        await world.SeeAsync(mob);
        await world.OnWorldAsync(() =>
        {
            world.Engage(mob);
            Player master = world.Host.World.FindOnlinePlayer("Riskmasterb")!;
            master.Map!.Combat.DealDamage(mob, master, master.Health, direct: false);
            world.Session.ManagedBudget = new ManagedActionBudget(8);
            ai.Update(world.Player, RiskTestWorld.ThinkMs);
            Assert.False(ai.Retreat.Active);
            return true;
        });
    }
}
