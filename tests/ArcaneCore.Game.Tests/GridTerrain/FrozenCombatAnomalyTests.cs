using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Diagnostics;
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
}