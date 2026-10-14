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
        // The creatures are level 20 against a level 1 bot: with the risk estimate on, the bot rightly retreats (PlayerbotRisk). This
        // test is about which target a fighting bot takes and how, so it fights.
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 500, Risk = { Enabled = false } });
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
        // The creatures are level 20 against a level 1 bot: with the risk estimate on, the bot rightly retreats (PlayerbotRisk). This
        // test is about which target a fighting bot takes and how, so it fights.
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 500, Risk = { Enabled = false } });
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

    [Fact]
    public async Task ThreatenedByCreatureWithoutDirectAttacker_IsSelectedInsteadOfCombatLinger()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        Creature threatHolder = null!, ally = null!;
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 500, Risk = { Enabled = false } });
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                threatHolder = AddHostile(host, player, 993807, 2, "threat holder", level: 20);
                ally = new Creature(993808, new CreatureTemplate
                {
                    Entry = 993808, Name = "engaged pet", CreatureType = 1,
                    MinLevel = 20, MaxLevel = 20, MinLevelHealth = 500, MaxLevelHealth = 500,
                }, null, CreatureContent.Empty, new Random(993808));
                ally.Relocate(player.X + 1, player.Y + 1, player.Z, 0, host.World.NowMs);
                player.Map!.AddObject(ally);
                return true;
            });
            await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(threatHolder.Guid), "threat holder visibility");
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                // The creature is swinging at another unit (e.g. a warlock's Imp or a fellow bot in the camp) while keeping
                // this bot on its threat list, which keeps player.Combat.IsInCombat true without putting the creature in Attackers.
                Assert.True(player.Map!.Combat.Attack(threatHolder, ally));
                player.Map!.Combat.DealDamage(threatHolder, player, 1);
                Assert.True(player.Map!.Combat.AttackStop(threatHolder));
                threatHolder.Combat.Threat.AddThreat(ally, 100f);
                threatHolder.Combat.Threat.AddThreat(player, 10f);
                Assert.True(player.Combat.IsInCombat);
                Assert.Null(player.Combat.Victim);
                Assert.Empty(player.Combat.Attackers);
                Assert.Contains(threatHolder, player.Combat.ThreatenedBy);

                session.Services.GetRequiredService<SpellFeature>().System.Store = new SpellStore([], [], []);
                session.ManagedBudget = new ManagedActionBudget(2);
                brain.Update(500);

                Assert.Same(threatHolder, brain.InspectionTarget);
                Assert.Equal(threatHolder.Entry, brain.TargetEntry);
                Assert.Same(threatHolder, player.Combat.Victim);
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
    public async Task ChaseInsideSingleDeathMark_ReachesThreatHolderInsteadOfCombatLinger()
    {
        await using Risk.RiskTestWorld world = await Risk.RiskTestWorld.StartAsync();
        CreatureTemplate template = Risk.RiskTestWorld.Template(993809, health: 20, minDamage: 1, maxDamage: 2);
        // 8 yards away: outside melee reach (4 yards) so the bot must plan a chase route, yet inside the 25-yard single death mark.
        Creature threatHolder = await world.OnWorldAsync(() => world.Spawn(template, 8f, 0f));
        await world.SeeAsync(threatHolder);

        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            world.Brain.Risk.Hazards.Add(player.MapId, new System.Numerics.Vector3(player.X, player.Y, player.Z),
                PlayerbotHazards.DeathYards, world.Host.World.NowMs, 300, "death");
            Assert.True(player.Map!.Combat.Attack(threatHolder, player));
            player.Map!.Combat.DealDamage(threatHolder, player, 1);
            Assert.True(player.Map!.Combat.AttackStop(threatHolder));
            threatHolder.Combat.Threat.AddThreat(player, 10f);
            Assert.True(player.Combat.IsInCombat);
            Assert.Null(player.Combat.Victim);
            Assert.Empty(player.Combat.Attackers);
            Assert.Contains(threatHolder, player.Combat.ThreatenedBy);

            world.Session.Services.GetRequiredService<SpellFeature>().System.Store = new SpellStore([], [], []);
            world.Session.ManagedBudget = new ManagedActionBudget(4);
            world.Brain.Update(500);

            // The chase route inside the single death mark must not be refused into target=0 combat linger.
            Assert.Same(threatHolder, world.Brain.InspectionTarget);
            Assert.Equal(threatHolder.Entry, world.Brain.TargetEntry);
            Assert.Equal(PlayerbotGoalKind.Grind, world.Brain.Goal);

            // Subsequent think ticks continue the chase rather than dropping the target.
            world.Session.ManagedBudget = new ManagedActionBudget(4);
            world.Brain.Update(500);
            Assert.Same(threatHolder, world.Brain.InspectionTarget);
            Assert.Equal(threatHolder.Entry, world.Brain.TargetEntry);
            return true;
        });
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
