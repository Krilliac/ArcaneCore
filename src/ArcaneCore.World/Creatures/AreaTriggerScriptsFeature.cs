using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Creatures.Scripts;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Quests;
using ArcaneCore.World.Features;
using ArcaneCore.World.Npc;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Creatures;

/// <summary>
/// cmangos <c>world/areatrigger_scripts.cpp</c>, slice 1: the quest triggers that need only quest state, a proximity check and a
/// player summon. Bound by the cmangos <c>scripted_areatrigger</c> ids (scriptdev2.sql) rather than a table, since the imported world has none.
/// Runs on CMSG_AREATRIGGER after the volume check (<see cref="IAreaTriggerListener"/>); like the smart trigger seam it cannot end the
/// handler, which matches these scripts: each of them returns false in cmangos, or true only after it did nothing further.
/// The other five scripts, which needed engine pieces first, are <see cref="AreaTriggerEngineScriptsFeature"/>. at_twiggy_flathead (the_barrens.cpp)
/// hands the trigger to Twiggy's AI (<see cref="TwiggyFlatheadAI.OnAreaTrigger"/>).
/// </summary>
public sealed class AreaTriggerScriptsFeature(IServiceProvider services) : IWorldFeature, IAreaTriggerListener
{
    public const uint TriggerRavenholdt = 3066;
    public const uint TriggerAncientLeaf = 3587;
    public const uint TriggerTwilightGrove = 4017;
    public const uint TriggerLarkorwiFirst = 1726, TriggerLarkorwiLast = 1740;

    public const uint QuestManorRavenholdt = 6681, NpcRavenholdt = 13936;
    public const uint QuestScentOfLarkorwi = 4291, NpcLarkorwiMate = 9683;
    public const uint QuestAncientLeaf = 7632, NpcVartrus = 14524, NpcStoma = 14525, NpcHastat = 14526;
    /// <summary>SD2 SAY_TWILIGHT_CORRUPTER_SPAWN (-1000411) is broadcast text 11271, "Come, $N. See what the Nightmare brings...".</summary>
    public const int TextTwilightCorrupterSpawn = 11271;

    /// <summary>at_childrens_week_spot TriggerOrphanSpell: trigger, the orphan's mini-pet entry, quest.</summary>
    public static readonly (uint Trigger, uint MiniPet, uint Quest)[] ChildrensWeekSpots =
    [
        (3546, 14305, 1479), (3547, 14444, 1800), (3548, 14305, 1558), (3549, 14444, 911), (3550, 14444, 910), (3552, 14305, 1687),
    ];

    public const uint QuestNightmareCorruption = 8735, NpcTwilightCorrupter = 15625, ItemFragmentOfNightmare = 21149;

    private static readonly (uint Entry, float X, float Y, float Z, float O)[] Ancients =
    [
        (NpcVartrus, 6204.051758f, -1172.575684f, 370.079224f, 0.86052f),
        (NpcStoma, 6246.953613f, -1155.985718f, 366.182953f, 2.90269f),
        (NpcHastat, 6193.449219f, -1137.834106f, 366.260529f, 5.77332f),
    ];

    private static readonly (float X, float Y, float Z, float O) TwilightCorrupterSpawn = (-10326.3f, -487.423f, 50.1127f, 5.73692f);

    public void Attach(WorldRuntime world) => ArgumentNullException.ThrowIfNull(world);

    public void OnAreaTrigger(Player player, uint triggerId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (services.GetService<QuestNpcFeature>()?.Services is not { } quests)
        {
            return;
        }

        AreaTriggerTemplate? trigger = null;
        try
        {
            trigger = services.GetService<TeleportFeature>()?.Maps.FindAreaTrigger(triggerId);
        }
        catch (InvalidOperationException)
        {
            // The teleport feature is not attached (a bare test container); the player's position stands in for the centre.
        }

        Run(quests, player, triggerId, trigger is null ? null : (trigger.X, trigger.Y, trigger.Z));
    }

    /// <summary>Runs the script bound to <paramref name="triggerId"/>; returns true when one ran an effect (credit or summon).</summary>
    /// <param name="centre">The trigger's centre (<c>pAt->x/y/z</c>); null falls back to the player's position, which is inside the volume.</param>
    public static bool Run(QuestNpcServices quests, Player player, uint triggerId, (float X, float Y, float Z)? centre = null)
    {
        ArgumentNullException.ThrowIfNull(quests);
        ArgumentNullException.ThrowIfNull(player);
        return triggerId switch
        {
            TriggerRavenholdt => Ravenholdt(quests, player),
            >= TriggerLarkorwiFirst and <= TriggerLarkorwiLast => ScentOfLarkorwi(quests, player, centre ?? (player.X, player.Y, player.Z)),
            >= 3546 and <= 3552 => ChildrensWeekSpot(quests, player, triggerId),
            TriggerAncientLeaf => AncientLeaf(quests, player),
            TriggerTwilightGrove => TwilightGrove(quests, player),
            TwiggyFlatheadAI.AreaTriggerAffray => Creatures(player) is { } creatures
                && TwiggyFlatheadAI.OnAreaTrigger(creatures, player, (p, q) => StatusOf(quests, p, q) == QuestStatus.Incomplete),
            _ => false,
        };
    }

    private static QuestStatus StatusOf(QuestNpcServices quests, Player player, uint questId)
        => quests.StateOf(player) is { Loaded: true } state && ReferenceEquals(state.Quests.Player, player)
            ? state.Quests.GetStatus(questId) : QuestStatus.None;

    // AreaTrigger_at_ravenholdt: kill credit for the Manor Ravenholdt marker while the quest is incomplete (no alive/GM check in cmangos).
    private static bool Ravenholdt(QuestNpcServices quests, Player player)
    {
        if (StatusOf(quests, player, QuestManorRavenholdt) != QuestStatus.Incomplete)
        {
            return false;
        }

        quests.KilledMonsterCredit(player, NpcRavenholdt, default, inRaidGroup: false);
        return true;
    }

    // AreaTrigger_at_scent_larkorwi: Larkorwi's mate (2 min, out-of-combat timer) unless one is already within 25 yd, alive or dead.
    // cmangos summons at the trigger's centre.
    private static bool ScentOfLarkorwi(QuestNpcServices quests, Player player, (float X, float Y, float Z) centre)
    {
        if (!player.IsAlive || player.IsGameMaster || StatusOf(quests, player, QuestScentOfLarkorwi) != QuestStatus.Incomplete
            || Creatures(player) is not { } creatures || Closest(creatures, player, NpcLarkorwiMate, 25f, aliveOnly: false) is not null)
        {
            return false;
        }

        if (creatures.SummonInstanceCreature(NpcLarkorwiMate, centre.X, centre.Y, centre.Z, 3.3f) is not { } mate)
        {
            return false;
        }

        creatures.MarkTimedOutOfCombatDespawn(mate, 2 * 60_000);
        return true;
    }

    // AreaTrigger_at_ancient_leaf: with Ancient Leaf complete, the three ancients for 5 min unless any of them is alive within 50 yd.
    private static bool AncientLeaf(QuestNpcServices quests, Player player)
    {
        if (player.IsGameMaster || !player.IsAlive || StatusOf(quests, player, QuestAncientLeaf) != QuestStatus.Complete
            || Creatures(player) is not { } creatures)
        {
            return false;
        }

        foreach ((uint entry, _, _, _, _) in Ancients)
        {
            if (Closest(creatures, player, entry, 50f, aliveOnly: true) is not null)
            {
                return false;
            }
        }

        bool any = false;
        foreach ((uint entry, float x, float y, float z, float o) in Ancients)
        {
            if (creatures.SummonInstanceCreature(entry, x, y, z, o) is { } ancient)
            {
                creatures.MarkTimedDespawn(ancient, 5 * 60_000);
                any = true;
            }
        }

        return any;
    }

    // AreaTrigger_at_twilight_grove: the Twilight Corrupter (30 min, out-of-combat timer) for a player on Nightmare Corruption without the
    // Fragment of the Nightmare's Corruption, unless one is alive within 500 yd. It speaks SAY_TWILIGHT_CORRUPTER_SPAWN to the player.
    private static bool TwilightGrove(QuestNpcServices quests, Player player)
    {
        if (!player.IsAlive || player.IsGameMaster || player.Inventory.GetItemCount(ItemFragmentOfNightmare) >= 1
            || StatusOf(quests, player, QuestNightmareCorruption) is QuestStatus.Complete or QuestStatus.None
            || Creatures(player) is not { } creatures || Closest(creatures, player, NpcTwilightCorrupter, 500f, aliveOnly: true) is not null)
        {
            return false;
        }

        (float x, float y, float z, float o) = TwilightCorrupterSpawn;
        if (creatures.SummonInstanceCreature(NpcTwilightCorrupter, x, y, z, o) is not { } corrupter)
        {
            return false;
        }

        creatures.MarkTimedOutOfCombatDespawn(corrupter, 30 * 60_000);
        creatures.SayText(corrupter, TextTwilightCorrupterSpawn, player);
        return true;
    }

    // AreaTrigger_at_childrens_week_spot: on an orphan quest whose spot this is, with that orphan out as the mini-pet, the quest completes.
    // cmangos calls Player::CompleteQuest; the orphan quests have no other objective, so the event credit (which sends the same
    // SMSG_QUESTUPDATE_COMPLETE) completes them the same way.
    private static bool ChildrensWeekSpot(QuestNpcServices quests, Player player, uint triggerId)
    {
        foreach ((uint trigger, uint miniPet, uint quest) in ChildrensWeekSpots)
        {
            if (trigger != triggerId || StatusOf(quests, player, quest) != QuestStatus.Incomplete)
            {
                continue;
            }

            if (player.Map?.FindUpdater<PetMapSystem>()?.MiniPetOf(player) is { } pet && pet.Entry == miniPet)
            {
                quests.AreaExploredOrEventHappens(player, quest);
                return true;
            }
        }

        return false;
    }

    private static CreatureMapSystem? Creatures(Player player) => player.Map?.FindUpdater<CreatureMapSystem>();

    /// <summary>SD2 GetClosestCreatureWithEntry over the map's creatures (3D distance).</summary>
    internal static Creature? Closest(CreatureMapSystem creatures, Player player, uint entry, float range, bool aliveOnly)
    {
        Creature? best = null;
        float bestSq = range * range;
        foreach (Creature c in creatures.Creatures)
        {
            if (c.Entry != entry || (aliveOnly && !c.IsAlive))
            {
                continue;
            }

            float dx = c.X - player.X, dy = c.Y - player.Y, dz = c.Z - player.Z;
            float sq = (dx * dx) + (dy * dy) + (dz * dz);
            if (sq <= bestSq)
            {
                best = c;
                bestSq = sq;
            }
        }

        return best;
    }
}
