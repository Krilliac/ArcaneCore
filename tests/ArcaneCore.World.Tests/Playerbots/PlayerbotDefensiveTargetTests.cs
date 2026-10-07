using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotDefensiveTargetTests
{
    [Fact]
    public async Task IncomingAttackerBeatsCompetingOptionalGrindTarget()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        Creature attacker = null!, grind = null!;
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 500 });
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                attacker = AddHostile(host, player, 993801, 2, "incoming attacker", level: 20);
                grind = AddHostile(host, player, 993802, 1, "optional grind target");
                return true;
            });
            await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(attacker.Guid)
                && session.Player.VisibleObjects.Contains(grind.Guid), "defensive target visibility");
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                Assert.True(player.Map!.Combat.Attack(attacker, player));
                player.Map!.Combat.DealDamage(attacker, player, 1);
                Assert.True(player.Combat.IsInCombat);
                Assert.Null(player.Combat.Victim);
                Assert.Contains(attacker, player.Combat.Attackers);

                // Remove the spell-first option so this test exercises the ordinary
                // attack handler after Brain selects the defensive target.
                session.Services.GetRequiredService<SpellFeature>().System.Store = new SpellStore([], [], []);
                session.ManagedBudget = new ManagedActionBudget(1);
                brain.Update(500);

                Assert.Same(attacker, brain.InspectionTarget);
                Assert.Same(attacker, player.Combat.Victim);
                Assert.DoesNotContain(grind, player.Combat.Attackers);
                return true;
            });
        }
        finally
        {
            brain.Stop();
            session.Kick();
            await session.ManagedClosed;
        }
    }

    [Fact]
    public async Task CombatLingerWithoutVictimOrAttackers_DoesNotStartOptionalGrind()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        Creature attacker = null!, grind = null!;
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 500 });
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                attacker = AddHostile(host, player, 993803, 1, "stopped attacker");
                grind = AddHostile(host, player, 993804, 2, "linger grind target");
                Assert.True(player.Map!.Combat.Attack(attacker, player));
                player.Map!.Combat.DealDamage(attacker, player, 1);
                Assert.True(player.Map!.Combat.AttackStop(attacker));
                Assert.True(player.Combat.IsInCombat);
                Assert.Null(player.Combat.Victim);
                Assert.Empty(player.Combat.Attackers);
                return true;
            });
            await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(grind.Guid), "linger target visibility");
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                session.ManagedBudget = new ManagedActionBudget(1);
                brain.Update(500);
                Assert.Equal(PlayerbotGoalKind.Combat, brain.Goal);
                Assert.Null(brain.InspectionTarget);
                Assert.Null(player.Combat.Victim);
                Assert.True(player.Combat.IsInCombat);
                return true;
            });
        }
        finally
        {
            brain.Stop();
            session.Kick();
            await session.ManagedClosed;
        }
    }

    [Fact]
    public async Task ExistingOutgoingVictimRemainsPreferredOverOptionalTarget()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        Creature victim = null!, secondAttacker = null!;
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 500 });
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                victim = AddHostile(host, player, 993805, 3, "existing victim", level: 20);
                secondAttacker = AddHostile(host, player, 993806, 1, "second incoming attacker", level: 20);
                return true;
            });
            await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(victim.Guid)
                && session.Player.VisibleObjects.Contains(secondAttacker.Guid), "outgoing target visibility");
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                Assert.True(session.TryManagedAction(WorldOpcode.CmsgAttackswing,
                    PlayerbotNavigation.GuidPayload(victim.Guid.Value)));
                Assert.Same(victim, player.Combat.Victim);
                Assert.True(player.Map!.Combat.Attack(secondAttacker, player));
                player.Map!.Combat.DealDamage(secondAttacker, player, 1);
                Assert.Contains(secondAttacker, player.Combat.Attackers);
                session.Services.GetRequiredService<SpellFeature>().System.Store = new SpellStore([], [], []);
                session.ManagedBudget = new ManagedActionBudget(1);
                brain.Update(500);
                Assert.Same(victim, brain.InspectionTarget);
                Assert.Same(victim, player.Combat.Victim);
                Assert.Contains(secondAttacker, player.Combat.Attackers);
                return true;
            });
        }
        finally
        {
            brain.Stop();
            session.Kick();
            await session.ManagedClosed;
        }
    }

    private static Creature AddHostile(WorldTestHost host, Player player, uint low, float offset, string name, byte level = 1)
    {
        var creature = new Creature(low, new CreatureTemplate
        {
            Entry = (uint)low, Name = name, CreatureType = 1,
            MinLevel = level, MaxLevel = level, MinLevelHealth = 20, MaxLevelHealth = 20,
        }, null, CreatureContent.Empty, new Random((int)low));
        creature.Relocate(player.X + offset, player.Y, player.Z, 0, host.World.NowMs);
        player.Map!.AddObject(creature);
        return creature;
    }
}
