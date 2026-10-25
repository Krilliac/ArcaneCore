using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Combat;

/// <summary>
/// Live bot-brain/stall at the Everlook graveyard: bots of one faction kept killing the other faction's warlock imps, the warlocks
/// resummoned them and the bots never moved on. A pet, guardian or totem a player controls is never an idle-grind or quest-objective
/// target (<see cref="PlayerbotBrain.FindTarget"/>); engaging one is a player-versus-player decision, not a grind.
/// </summary>
public sealed class PlayerbotPlayerPetTargetTests
{
    private const uint ImpEntry = 416;

    [Fact]
    public async Task APetOfAnotherPlayer_IsNeverChosen_ForGrindOrAQuestObjective_FlaggedOrNot()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        // A human (Alliance) bot and an orc (Horde) owner, both unflagged; the test host enters them at the same start spot.
        WorldSession bot = await CombatTestSessions.EnterAsync(host, "PETBOT", "Petbot", race: 1);
        WorldSession owner = await CombatTestSessions.EnterAsync(host, "PETOWNER", "Petowner", race: 2);
        try
        {
            Creature wild = null!, imp = null!;
            await host.OnWorldAsync(() =>
            {
                Player player = bot.Player!;
                player.Level = 60;
                owner.Player!.Relocate(player.X + 5f, player.Y, player.Z, player.Orientation, host.World.NowMs);
                wild = AddImp(host, player, 994101, 3f, ownerGuid: null);
                imp = AddImp(host, player, 994102, 8f, owner.Player!.Guid);
                return true;
            });
            await host.WaitForWorldAsync(() => bot.Player!.VisibleObjects.Contains(wild.Guid)
                && bot.Player.VisibleObjects.Contains(imp.Guid), "imps visible");
            await host.OnWorldAsync(() =>
            {
                Player player = bot.Player!;
                // Control: the unowned imp is an ordinary hostile creature, so the target search is not vacuously empty.
                Assert.Same(wild, PlayerbotBrain.FindTarget(player));
                Assert.Same(wild, PlayerbotBrain.FindTarget(player, ImpEntry));

                // Drop the wild imp: only the player's pet is left.
                player.Map!.RemoveObject(wild);
                Assert.Null(PlayerbotBrain.FindTarget(player));
                Assert.Null(PlayerbotBrain.FindTarget(player, ImpEntry));

                // Whether the owner is PvP flagged is the PvP rule's business, not the grind's.
                owner.Player!.UnitFlags |= UnitFlags.Pvp;
                Assert.Null(PlayerbotBrain.FindTarget(player));
                Assert.Null(PlayerbotBrain.FindTarget(player, ImpEntry));
                Assert.Equal(imp.MaxHealth, imp.Health);
                return true;
            });
        }
        finally
        {
            owner.Kick(); await owner.ManagedClosed;
            bot.Kick(); await bot.ManagedClosed;
        }
    }

    private static Creature AddImp(WorldTestHost host, Player near, uint low, float offset, ObjectGuid? ownerGuid)
    {
        var creature = new Creature(low, new CreatureTemplate
        {
            Entry = ImpEntry, Name = "Imp", CreatureType = 3,
            MinLevel = 60, MaxLevel = 60, MinLevelHealth = 500, MaxLevelHealth = 500,
        }, null, CreatureContent.Empty, new Random((int)low));
        creature.Relocate(near.X + offset, near.Y, near.Z, 0, host.World.NowMs);
        if (ownerGuid is { } guid)
        {
            creature.SetOwnerGuid(guid);
            creature.UnitFlags |= UnitFlags.PlayerControlled;
        }

        near.Map!.AddObject(creature);
        return creature;
    }
}
