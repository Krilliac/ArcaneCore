using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureTestKit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Creatures;

/// <summary>
/// vmangos parity: Unit::SetInCombatState calls Creature::OnEnterCombat -> AttackedBy for a creature entering combat (Unit.cpp:5942-5946,
/// Creature.cpp:3652-3653), so a creature whose own hit puts it in combat attacks the unit it hit instead of sitting in combat with no victim
/// and evading. The scenarios use a synthetic direct school-damage spell, not a periodic aura. Firemane mobs (4328/4329/4331) showed this
/// evade loop live: their creature/evade-loop anomaly reports named DealSpellDamage/SetInCombatWithVictim as the caller with no victim and no
/// attackers, i.e. the creature was put in combat as the attacker by its own spell damage, which is the state these scenarios reproduce.
/// Each scenario owns an entry (the anomaly ledger is process-wide).
/// </summary>
public sealed class CreatureAttackerCombatEntryTests
{
    private const uint AttackEntry = 950020;
    private const uint LoopEntry = 950021;
    private const uint BusyEntry = 950022;
    private const uint EvadingEntry = 950023;
    private const uint NoCombatEntry = 950024;
    private const uint FleeingEntry = 950025;
    private const uint ConfusedEntry = 950026;
    private const uint ThreatOnlyEntry = 950027;

    private static readonly SpellInfo PulseSpell = Spell(947202, Effect(SpellEffectName.SchoolDamage, 5, SpellImplicitTarget.UnitEnemy)) with { School = SpellSchool.Fire };

    private sealed record Scene(WorldRuntime World, Map Map, CreatureMapSystem System, Player Player, Creature Mob) : IDisposable
    {
        public void Dispose() => World.Dispose();
    }

    /// <summary>The mob idles at (0,0); the player stands at 45 yd, in the same grid but outside aggro range (a farther player leaves the grid without updates).</summary>
    private static Scene Start(uint entry)
    {
        CreatureContent content = Content([Template(entry)], [Spawn(1, entry, 0, 0)]);
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Hostility = new AlwaysHostile() });
        (Player player, _) = AddPlayer(runtime, 1, 45, 0);
        Creature mob = Assert.Single(system.Creatures);
        mob.AI!.CombatMovement = false;
        return new Scene(runtime, map, system, player, mob);
    }

    private static uint Pulse(Scene s, Unit victim, bool startsCombat = true)
        => new MapCombatDamageSink().DealSpellDamage(s.Mob, victim, PulseSpell, 1, periodic: false, startsCombat: startsCombat);

    [Fact]
    public void AnIdleCreature_WhoseSpellHitsAPlayer_AttacksIt_AndListsItOnItsThreat()
    {
        using Scene s = Start(AttackEntry);
        Assert.False(s.Mob.Combat.IsInCombat);

        Pulse(s, s.Player);

        Assert.True(s.Mob.Combat.IsInCombat);
        Assert.Same(s.Player, s.Mob.Combat.Victim);
        Assert.True(s.Mob.Combat.Threat.Contains(s.Player));
    }

    [Fact]
    public void RepeatedOutOfCombatPulses_DoNotLoopTheCreatureThroughEvade()
    {
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == "creature/evade-loop" && n.Subject.StartsWith($"creature entry {LoopEntry} ", StringComparison.Ordinal));
        using Scene s = Start(LoopEntry);

        int evadingTicks = 0;
        for (int hit = 0; hit < 3; hit++)
        {
            Pulse(s, s.Player);
            for (int tick = 0; tick < 10; tick++)
            {
                Run(s.World, 100);
                if (s.Mob.IsEvading)
                {
                    evadingTicks++;
                }
            }
        }

        Assert.True(evadingTicks == 0, $"evading ticks={evadingTicks} inCombat={s.Mob.Combat.IsInCombat} victim={s.Mob.Combat.Victim} setBy={s.Mob.Combat.LastCombatSetBy}");
        Assert.Empty(capture.Notices);
    }

    [Fact]
    public void ACreatureAlreadyFightingOnePlayer_KeepsIt_WhenItsSpellHitsAnother()
    {
        using Scene s = Start(BusyEntry);
        (Player other, _) = AddPlayer(s.World, 2, 45, 5);
        s.Map.Combat.Attack(s.Mob, s.Player, melee: false);
        Assert.Same(s.Player, s.Mob.Combat.Victim);

        Pulse(s, other);

        Assert.Same(s.Player, s.Mob.Combat.Victim);
    }

    [Fact]
    public void AnEvadingCreature_WhoseSpellHitsAPlayer_StaysEvading_WithoutAttacking()
    {
        using Scene s = Start(EvadingEntry);
        s.Mob.Relocate(10, 0, 83.5f, 0, 0);
        s.System.EnterEvadeMode(s.Mob);
        Assert.True(s.Mob.IsEvading);

        Pulse(s, s.Player);

        Assert.True(s.Mob.IsEvading);
        Assert.Null(s.Mob.Combat.Victim);
    }

    [Fact]
    public void AHitThatDoesNotStartCombat_DoesNotMakeTheCreatureAttack()
    {
        using Scene s = Start(NoCombatEntry);

        Pulse(s, s.Player, startsCombat: false);

        Assert.False(s.Mob.Combat.IsInCombat);
        Assert.Null(s.Mob.Combat.Victim);
        Assert.False(s.Mob.Combat.Threat.Contains(s.Player));
    }

    [Theory]
    [InlineData(FleeingEntry, UnitFlags.Fleeing)]
    [InlineData(ConfusedEntry, UnitFlags.Confused)]
    public void AFeared_OrConfusedCreature_WhoseSpellHitsAPlayer_DoesNotAttackIt(uint entry, UnitFlags cc)
    {
        using Scene s = Start(entry);
        s.Mob.UnitFlags |= cc;

        Pulse(s, s.Player);

        Assert.True(s.Mob.Combat.IsInCombat);
        Assert.Null(s.Mob.Combat.Victim);
    }

    [Fact]
    public void ACreatureAlreadyInCombatWithNoVictim_IsNotMadeToAttack_ByItsSpellHit()
    {
        using Scene s = Start(ThreatOnlyEntry);
        s.Map.Combat.SetInCombatState(s.Mob, 0);
        Assert.True(s.Mob.Combat.IsInCombat);
        Assert.Null(s.Mob.Combat.Victim);

        Pulse(s, s.Player);

        Assert.Null(s.Mob.Combat.Victim);
    }
}
