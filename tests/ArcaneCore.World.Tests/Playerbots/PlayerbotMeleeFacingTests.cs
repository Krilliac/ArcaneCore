using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// A bot in melee turns to its victim, as a client does when it attacks. The server refuses a swing at a target outside the
/// 120 degree auto-attack arc (vmangos Unit::AttackerStateUpdate, SMSG_ATTACKSWING_BADFACING) and nothing else turns a managed
/// player, so a bot that ended a route, or was reached by a creature, facing away from its victim never dealt damage again.
/// Live replay of the 2026-10-08 rehearsal snapshot: Duststalker, a hunter, stood 2 to 3 yards from a Vile Familiar at 78 to 106
/// degrees off its facing for 150 seconds, the creature's health untouched, until it died.
/// </summary>
public sealed class PlayerbotMeleeFacingTests
{
    [Fact]
    public async Task ABotInMeleeThatFacesAwayFromItsVictim_TurnsAndSwings()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        Creature mob = null!;
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 500 });
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                mob = new Creature(993901, new CreatureTemplate
                {
                    Entry = 993901, Name = "facing mob", CreatureType = 1, MinLevel = 20, MaxLevel = 20,
                    MinLevelHealth = 2000, MaxLevelHealth = 2000,
                }, null, CreatureContent.Empty, new Random(993901));
                mob.Relocate(player.X + 2, player.Y, player.Z, 0, host.World.NowMs);
                player.Map!.AddObject(mob);
                return true;
            });
            await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(mob.Guid), "mob visibility");
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                Assert.True(player.Map!.Combat.Attack(mob, player));
                player.Map!.Combat.DealDamage(mob, player, 1);
                session.Services.GetRequiredService<SpellFeature>().System.Store = new SpellStore([], [], []);
                session.ManagedBudget = new ManagedActionBudget(4);
                brain.Update(500);
                Assert.Same(mob, player.Combat.Victim);

                // The mob is east of the player; the player faces west, 180 degrees off.
                player.Relocate(player.X, player.Y, player.Z, MathF.PI, host.World.NowMs);
                Assert.False(MapCombat.HasInArc(player, mob, CombatConstants.AutoAttackArc));
                session.ManagedBudget = new ManagedActionBudget(4);
                brain.Update(500);
                Assert.True(MapCombat.HasInArc(player, mob, CombatConstants.AutoAttackArc),
                    $"still facing {player.Orientation:F2} with the victim at bearing 0");
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
}
