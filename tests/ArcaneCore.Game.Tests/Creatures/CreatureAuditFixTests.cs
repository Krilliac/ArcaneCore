using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Tests.CreatureAi;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.Creatures;

/// <summary>Creature-side fixes from the agent audit lane 04 (update fields written at level selection, text delivery).</summary>
public sealed class CreatureAuditFixTests
{
    [Fact]
    public void SelectLevel_WritesTheTemplateMeleeAttackPowerToTheAttackPowerField()
    {
        // vmangos Creature::UpdateAttackPowerAndDamage (StatSystem.cpp:820-840) writes both attack power fields from the template.
        CreatureContent content = Content([Template() with { MeleeAttackPower = 123, RangedAttackPower = 45 }], [Spawn(1, WolfEntry, 8, 0)]);
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(content);
        using (world)
        {
            AddPlayer(world, 1, 0, 0); // the spawn's grid is activated by a player
            Creature wolf = Assert.Single(system.Creatures);

            Assert.Equal(123, wolf.GetInt32(UpdateFields.UnitFieldAttackPower));
            Assert.Equal(45, wolf.GetInt32(UpdateFields.UnitFieldRangedAttackPower));
        }
    }

    [Theory]
    [InlineData(99u, false)]
    [InlineData(0u, true)]
    public void ZoneYellText_ReachesOnlyThePlayersOfTheSpeakersZone(uint speakerZone, bool heard)
    {
        // cmangos CHAT_TYPE_ZONE_YELL -> Map::MonsterYellToZone: the map's players whose zone is the speaker's. Without terrain a player's zone is 0.
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 8, 0)]);
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(content,
            new CreatureAiServices { ZoneAndAreaOf = _ => (speakerZone, 0u) });
        using (world)
        {
            (_, FakeSession session) = AddPlayer(world, 1, 0, 0);
            Creature wolf = Assert.Single(system.Creatures);
            Packets(session, WorldOpcode.SmsgMessagechat);

            system.Say(wolf, new CreatureAiText(1, "zone yell", 6, 0, 0), null);

            Assert.Equal(heard ? 1 : 0, Packets(session, WorldOpcode.SmsgMessagechat).Count);
        }
    }

    [Fact]
    public void StuckDetectors_ChargeAHungFrameAtMostTheFrameCap()
    {
        // A 6-12 s world-tick hang is one frame, not that much time spent stuck: it must not alone cross the 6 s / 10 s report thresholds.
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 8, 0)]);
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(content);
        using (world)
        {
            AddPlayer(world, 1, 0, 0);
            Creature wolf = Assert.Single(system.Creatures);
            wolf.ResetStuckSampling();
            Assert.Equal(0u, wolf.AdvanceStuckSample(0)); // the first call only takes the sample point

            Assert.True(wolf.AdvanceStuckSample(11_580) < CreatureAnomalies.StuckThresholdMs);
            wolf.ResetStuckInCombatEpisode();
            Assert.True(wolf.AdvanceStuckInCombat(11_580) < CreatureAnomalies.StuckInCombatEmptyMs);
        }
    }

    [Fact]
    public void ProximityAggroOntoAnIdleCreature_MakesItFightBackInsteadOfLeavingItFlaggedWithoutAVictim()
    {
        // vmangos Creature::EnterCombatWithTarget -> pVictim->SetInCombatWith(this) -> Creature::OnEnterCombat -> AttackedBy: the target retaliates.
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 8, 0), Spawn(2, WolfEntry, 10, 0)]);
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Hostility = new AlwaysHostile() });
        using (world)
        {
            (Player player, _) = AddPlayer(world, 1, 0, 0);
            Creature busy = system.Creatures.Single(c => c.Spawn?.Guid == 1);
            Creature idle = system.Creatures.Single(c => c.Spawn?.Guid == 2);
            system.AttackStart(busy, player);
            Assert.NotNull(busy.Combat.Victim);
            Assert.Null(idle.Combat.Victim);

            system.EnterCombatWithTarget(busy, idle);

            Assert.Same(busy, idle.Combat.Victim);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProximityAggroOntoACreature_AlreadyInCombatOrFleeing_DoesNotMakeItRetaliate(bool fleeing)
    {
        // vmangos Unit::SetInCombatState runs OnEnterCombat only for a creature not yet in combat; OnEnterCombat skips AttackedBy when confused or fleeing.
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 8, 0), Spawn(2, WolfEntry, 10, 0)]);
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Hostility = new AlwaysHostile() });
        using (world)
        {
            (Player player, _) = AddPlayer(world, 1, 0, 0);
            Creature busy = system.Creatures.Single(c => c.Spawn?.Guid == 1);
            Creature idle = system.Creatures.Single(c => c.Spawn?.Guid == 2);
            system.AttackStart(busy, player);
            if (fleeing)
            {
                idle.UnitFlags |= UnitFlags.Fleeing;
            }
            else
            {
                map.Combat.SetInCombatState(idle, 0); // flagged by an earlier call: no OnEnterCombat this time
            }

            system.EnterCombatWithTarget(busy, idle);

            Assert.Null(idle.Combat.Victim);
        }
    }
}
