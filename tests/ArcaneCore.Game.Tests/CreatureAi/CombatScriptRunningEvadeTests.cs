using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>
/// The stranded-combat evade of <c>CreatureMapSystem.CheckHardLeash</c> (a creature in combat with no living combatant on the map evades on the
/// leash check) and its <see cref="CreatureAI.CombatScriptRunning"/> exemption (a boss whose script is mid-sequence, e.g. Thekal's fake death,
/// has no combatant by design and must not be reset).
/// </summary>
public sealed class CombatScriptRunningEvadeTests
{
    private sealed class ScriptedAI(Creature creature) : CreatureAI(creature)
    {
        public bool ScriptRunning { get; set; }

        public override bool CombatScriptRunning => ScriptRunning;

        protected override bool CallsGuardsOnSight => false;

        public override void OnUpdate(uint diffMs)
        {
        }

        public override void OnAttackedBy(Unit attacker)
        {
        }
    }

    private static (WorldRuntime World, Creature Creature, ScriptedAI Ai, CreatureMapSystem System) Start(bool scriptRunning)
    {
        var factory = new CreatureAiFactory();
        factory.RegisterEntryScript(WolfEntry, c => new ScriptedAI(c) { ScriptRunning = scriptRunning });
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 10, 0)]);
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content,
            new CreatureAiServices { Factory = factory, Hostility = new AlwaysHostile() });
        AddPlayer(world, 1, 0, 0);
        Creature creature = Assert.Single(system.Creatures);
        ScriptedAI ai = Assert.IsType<ScriptedAI>(creature.AI);

        map.Combat.SetInCombatState(creature, 0); // in combat, no victim, no threat, no attackers
        Assert.True(creature.Combat.IsInCombat);
        Assert.Empty(creature.Combat.Threat.Entries);
        Assert.Empty(creature.Combat.Attackers);
        return (world, creature, ai, system);
    }

    [Fact]
    public void ACreatureWithNoLivingCombatant_EvadesOnTheLeashCheck_WhenNoCombatScriptIsRunning()
    {
        (WorldRuntime world, Creature creature, _, CreatureMapSystem system) = Start(scriptRunning: false);
        using (world)
        {
            int evaded = 0;
            system.Evaded += c => { if (ReferenceEquals(c, creature)) evaded++; };

            Run(world, 4_000);

            Assert.False(creature.Combat.IsInCombat);
            Assert.Equal(1, evaded); // the creature may already be home again, so the evade is read from the event
        }
    }

    [Fact]
    public void ACreatureWithNoLivingCombatant_StaysInCombat_WhileACombatScriptIsRunning_AndEvadesOnceItEnds()
    {
        (WorldRuntime world, Creature creature, ScriptedAI ai, CreatureMapSystem system) = Start(scriptRunning: true);
        using (world)
        {
            int evaded = 0;
            system.Evaded += c => { if (ReferenceEquals(c, creature)) evaded++; };

            Run(world, 4_000);
            Assert.True(creature.Combat.IsInCombat);
            Assert.False(creature.IsInEvadeMode);
            Assert.Equal(0, evaded);

            ai.ScriptRunning = false;
            Run(world, 4_000);

            Assert.False(creature.Combat.IsInCombat);
            Assert.Equal(1, evaded);
        }
    }

    [Fact]
    public void SelectHostileTarget_WithNoVictimThreatOrAttackers_EvadesWhenNoCombatScriptIsRunning()
    {
        (WorldRuntime world, Creature creature, _, CreatureMapSystem system) = Start(scriptRunning: false);
        using (world)
        {
            Assert.False(system.SelectHostileTarget(creature));

            Assert.False(creature.Combat.IsInCombat);
            Assert.True(creature.IsEvading);
            Assert.True(creature.IsInEvadeMode);
        }
    }

    [Fact]
    public void SelectHostileTarget_WithNoVictimThreatOrAttackers_DoesNotEvadeWhileACombatScriptIsRunning_AndEvadesOnceItEnds()
    {
        // cmangos Unit::SelectHostileTarget (Entities/Unit.cpp:8711-8722): "do not evade during combat script running".
        (WorldRuntime world, Creature creature, ScriptedAI ai, CreatureMapSystem system) = Start(scriptRunning: true);
        using (world)
        {
            Assert.False(system.SelectHostileTarget(creature));

            Assert.True(creature.Combat.IsInCombat);
            Assert.False(creature.IsEvading);
            Assert.False(creature.IsInEvadeMode);

            ai.ScriptRunning = false;
            Assert.True(creature.Combat.IsInCombat);
            Assert.False(system.SelectHostileTarget(creature));

            Assert.False(creature.Combat.IsInCombat);
            Assert.True(creature.IsEvading);
            Assert.True(creature.IsInEvadeMode);
        }
    }
}
