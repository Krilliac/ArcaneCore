using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Combat.Threat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.GridTerrain;

/// <summary>
/// LS-007 (live 2026-10-17, 424 combat/combat-never-left reports): a creature whose fight ended while it stood outside every active
/// cell keeps its combat flag until a player comes near, because its AI (SelectHostileTarget, which evades on an empty threat list)
/// does not run there (vmangos Map::UpdateActiveCellsSynch). That is not a combat state that never ends, so the check holds its clock.
/// </summary>
public sealed class FrozenCombatAnomalyTests
{
    [Fact]
    public void ACreatureLeftInCombatOutsideTheActiveCells_IsNotReported_AndEvadesWhenAPlayerComesNear()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 20, 0), Spawn(2, WolfEntry, 250, 0)]);
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateSystem(content);
        using (world)
        {
            Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
            world.AddPlayer(player); // loads the centre grid, which holds both
            world.RunTick(50);
            Creature near = Assert.Single(system.Creatures, c => c.Spawn!.Guid == 1);
            Creature far = Assert.Single(system.Creatures, c => c.Spawn!.Guid == 2);
            using AnomalyCapture farCap = AnomalyLedger.Shared.Capture(n => n.Subject == MapCombat.SubjectOf(far));

            // Both fights ended with the combat flag still set (no victim, no threat list).
            map.Combat.SetInCombatState(near, 0);
            map.Combat.SetInCombatState(far, 0);
            for (int i = 0; i < 1300; i++)
            {
                world.RunTick(100);
            }

            Assert.False(near.Combat.IsInCombat, "the updated creature did not leave combat");
            Assert.True(far.IsFrozenOutsideActiveCells);
            Assert.True(far.Combat.IsInCombat); // frozen, as in vmangos
            Assert.False(farCap.Any("combat/combat-never-left"), farCap.Describe());

            // A player comes near: the creature updates again, finds no one to fight and evades.
            Player visitor = TestWorld.CreatePlayer(2, 245, 0, new FakeSession());
            world.AddPlayer(visitor);
            for (int i = 0; i < 20; i++)
            {
                world.RunTick(100);
            }

            Assert.False(far.IsFrozenOutsideActiveCells);
            Assert.False(far.Combat.IsInCombat, "the creature did not evade once its cell was active again");
            Assert.False(farCap.Any("combat/combat-never-left"), farCap.Describe());
        }
    }

    [Fact]
    public void ThreatAssist_FromTargetWithDistantOrFrozenCreature_DoesNotPinAssisterInCombat()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 20, 0), Spawn(2, WolfEntry, 250, 0)]);
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateSystem(content);
        using (world)
        {
            Player target = TestWorld.CreatePlayer(1, 0, 0, new FakeSession(1));
            Player assister = TestWorld.CreatePlayer(2, 5, 0, new FakeSession(2));
            world.AddPlayer(target);
            world.AddPlayer(assister);
            world.RunTick(50);

            Creature far = Assert.Single(system.Creatures, c => c.Spawn!.Guid == 2);
            map.Combat.SetInCombatState(far, 0);
            far.Combat.Threat.AddThreat(target, 100f);
            map.Combat.SetInCombatState(target, CombatConstants.PvpCombatTimerMs);
            world.RunTick(100);
            Assert.True(far.IsFrozenOutsideActiveCells);

            // Assisting target must not copy the frozen/distant creature onto assister's ThreatenedBy.
            map.Combat.SetInCombatState(assister, CombatConstants.PvpCombatTimerMs, "SetInCombatWithAssisted");
            Unit[] assisted = HostileRefs.ThreatAssist(target, assister, 50f, null, null);
            Assert.Empty(assisted);
            Assert.Empty(assister.Combat.ThreatenedBy);

            // Even if a frozen/distant creature already held assister on its threat list before freezing,
            // the PvP combat timer prunes the stale holder and lets assister leave combat without anomalies.
            far.Combat.Threat.AddThreat(assister, 10f);
            Assert.Contains(far, assister.Combat.ThreatenedBy);

            using AnomalyCapture assisterCap = AnomalyLedger.Shared.Capture(n => n.Subject == MapCombat.SubjectOf(assister));
            for (int i = 0; i < 80; i++)
            {
                world.RunTick(100);
            }

            Assert.Empty(assister.Combat.ThreatenedBy);
            Assert.False(assister.Combat.IsInCombat);
            Assert.False(assisterCap.Any("combat/combat-never-left"), assisterCap.Describe());
        }
    }

    [Fact]
    public void NearTeleport_OnSameMap_DeletesHostileThreatReferences()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 10, 0)]);
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateSystem(content);
        using (world)
        {
            WorldMaps.Of(world).Load(new MapContent(
                [new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", "")],
                [], [], [], []));
            var teleports = new TeleportService(world, static _ => { }, static _ => { });

            Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession(1));
            world.AddPlayer(player);
            var pet = new CombatTestUnit();
            pet.Spawn(map, 2, 0);
            player.SetPetGuid(pet.Guid);
            world.RunTick(50);

            Creature wolf = Assert.Single(system.Creatures);
            map.Combat.SetInCombatState(wolf, 0);
            map.Combat.SetInCombatState(player, CombatConstants.PvpCombatTimerMs);
            map.Combat.SetInCombatState(pet, CombatConstants.PvpCombatTimerMs);
            wolf.Combat.Threat.AddThreat(player, 100f);
            wolf.Combat.Threat.AddThreat(pet, 50f);
            Assert.Contains(wolf, player.Combat.ThreatenedBy);
            Assert.Contains(wolf, pet.Combat.ThreatenedBy);

            Assert.True(teleports.TeleportTo(player, map.MapId, 50f, 50f, 0f, 0f));

            Assert.False(player.Combat.IsInCombat);
            Assert.False(pet.Combat.IsInCombat);
            Assert.Empty(player.Combat.ThreatenedBy);
            Assert.Empty(pet.Combat.ThreatenedBy);
            Assert.False(wolf.Combat.Threat.Contains(player));
            Assert.False(wolf.Combat.Threat.Contains(pet));
        }
    }
}
