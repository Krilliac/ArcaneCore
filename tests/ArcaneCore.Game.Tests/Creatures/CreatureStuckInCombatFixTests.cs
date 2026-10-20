using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Creatures;

/// <summary>
/// Regression tests for <c>creature/stuck-in-combat</c> (live: Creature entry 4075 spawn 44664 Rat on map 1 in combat with no valid victim,
/// threat entries, or living attackers on map for &gt;10s) and orphan combat states in <c>CreatureMapSystem</c>.
/// </summary>
public sealed class CreatureStuckInCombatFixTests
{
    private const uint RatCritterEntry = 974075;
    private const uint CritterPostFleeEntry = 974076;
    private const uint NullAiEntry = 974077;
    private const uint DeadTaunterEntry = 974078;
    private const uint NoThreatListEntry = 974079;

    private static AnomalyCapture CaptureStuckInCombat(uint entry)
        => AnomalyLedger.Shared.Capture(n =>
            n.Key == "creature/stuck-in-combat"
            && n.Subject.StartsWith($"creature entry {entry} ", StringComparison.Ordinal));

    [Fact]
    public void CritterFleeingWithNoThreatOrAttackers_DoesNotReportStuckInCombatAtTenSeconds_AndEvadesAfterEscapeTimer()
    {
        using AnomalyCapture capture = CaptureStuckInCombat(RatCritterEntry);
        CreatureTemplate ratTemplate = Template(RatCritterEntry) with { CreatureType = 8 }; // Critter -> CritterAI
        CreatureContent content = Content([ratTemplate], [Spawn(44664, RatCritterEntry, 10, 0)]);
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Hostility = new AlwaysHostile() });
        using (world)
        {
            (Player player, _) = AddPlayer(world, 1, 0, 0);
            Creature rat = Assert.Single(system.Creatures);
            Assert.IsType<CritterAI>(rat.AI);

            // Simulate a critter hit where the attacker subsequently died or the spell suppressed threat:
            // the critter flees for EscapeTimerMs (30s) with no victim, threat entries, or living attackers.
            map.Combat.SetInCombatState(rat, 0);
            rat.OnAttackedBy(player);
            Assert.True(rat.Combat.IsInCombat);
            Assert.Equal(MovementGeneratorType.Fleeing, rat.Motion.CurrentType);
            Assert.Empty(rat.Combat.Threat.Entries);
            Assert.Empty(rat.Combat.Attackers);

            Run(world, 15_000);
            Assert.Empty(capture.Notices);

            Run(world, 20_000);
            Assert.False(rat.Combat.IsInCombat);
            Assert.Empty(capture.Notices);
        }
    }

    [Fact]
    public void CritterInCombatAfterFleeEndedWithNoCombatants_EvadesOnLeashCheck_BeforeTenSeconds()
    {
        using AnomalyCapture capture = CaptureStuckInCombat(CritterPostFleeEntry);
        CreatureTemplate ratTemplate = Template(CritterPostFleeEntry) with { CreatureType = 8 };
        CreatureContent content = Content([ratTemplate], [Spawn(1, CritterPostFleeEntry, 10, 0)]);
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Hostility = new AlwaysHostile() });
        using (world)
        {
            AddPlayer(world, 1, 0, 0);
            Creature rat = Assert.Single(system.Creatures);
            Assert.IsType<CritterAI>(rat.AI);

            // Put the critter in combat without an active Fleeing generator and with no threat or attackers.
            map.Combat.SetInCombatState(rat, 0);
            Assert.NotEqual(MovementGeneratorType.Fleeing, rat.Motion.CurrentType);

            Run(world, 4_000);

            Assert.False(rat.Combat.IsInCombat);
            Assert.Empty(capture.Notices);
        }
    }

    [Fact]
    public void NullAiCreatureStrandedInCombatWithNoCombatants_EvadesOnLeashCheck_BeforeTenSeconds()
    {
        using AnomalyCapture capture = CaptureStuckInCombat(NullAiEntry);
        CreatureTemplate template = Template(NullAiEntry, b => b.AIName = "NullAI");
        CreatureContent content = Content([template], [Spawn(1, NullAiEntry, 10, 0)]);
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Hostility = new AlwaysHostile() });
        using (world)
        {
            AddPlayer(world, 1, 0, 0);
            Creature npc = Assert.Single(system.Creatures);
            Assert.IsType<NullCreatureAI>(npc.AI);

            map.Combat.SetInCombatState(npc, 0);
            Run(world, 4_000);

            Assert.False(npc.Combat.IsInCombat);
            Assert.Empty(capture.Notices);
        }
    }

    [Fact]
    public void SelectHostileTarget_WithStaleTauntCasterAndNoValidTargets_HoldsWhileTheTauntIsUp_AndEvadesOnceItFades()
    {
        using AnomalyCapture capture = CaptureStuckInCombat(DeadTaunterEntry);
        CreatureContent content = Content([Template(DeadTaunterEntry)], [Spawn(1, DeadTaunterEntry, 10, 0)]);
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Hostility = new AlwaysHostile() });
        using (world)
        {
            AddPlayer(world, 1, 100, 100);
            Creature wolf = Assert.Single(system.Creatures);

            // A taunt caster GUID that is not a valid living unit on the map while the creature is in combat.
            ObjectGuid taunter = ObjectGuid.Player(99999);
            map.Combat.SetInCombatState(wolf, 0);
            wolf.Combat.Threat.AddTauntCaster(taunter);

            // vmangos Unit::SelectHostileTarget (Unit.cpp:7589): no target, but the taunt aura is still up, so no evade yet ...
            Assert.False(system.SelectHostileTarget(wolf));
            Assert.True(wolf.Combat.IsInCombat);
            Assert.False(wolf.IsInEvadeMode);

            // ... and once it fades (Aura::HandleModTaunt removes the caster) nothing holds it: it evades instead of sticking in combat.
            wolf.Combat.Threat.RemoveTauntCaster(taunter);
            Assert.False(system.SelectHostileTarget(wolf));
            Assert.False(wolf.Combat.IsInCombat);
        }
    }

    [Fact]
    public void NoThreatListCreature_WhoseVictimDies_LeavesCombatInsteadOfSticking()
    {
        using AnomalyCapture capture = CaptureStuckInCombat(NoThreatListEntry);
        CreatureTemplate template = Template(NoThreatListEntry, b =>
        {
            b.ExtraFlags = 0x00000800u;
            b.ExtraFlagsDialect = CreatureExtraFlagsDialect.VMangos;
        });
        CreatureContent content = Content([template], [Spawn(1, NoThreatListEntry, 5, 0)]);
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Hostility = new AlwaysHostile() });
        using (world)
        {
            (Player player, _) = AddPlayer(world, 1, 5, 0);
            Creature mob = Assert.Single(system.Creatures);

            system.AttackStart(mob, player);
            Assert.True(mob.Combat.IsInCombat);

            map.Combat.Kill(mob, player);
            Run(world, 500);

            Assert.False(mob.Combat.IsInCombat);
            Assert.Null(mob.Combat.Victim);
        }
    }
}
