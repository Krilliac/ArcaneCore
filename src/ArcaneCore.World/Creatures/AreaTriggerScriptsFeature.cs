using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.World.Features;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Creatures;

/// <summary>
/// cmangos <c>world/areatrigger_scripts.cpp</c>, slice 1: the quest triggers that need only quest state, a proximity check and a
/// player summon. Bound by the cmangos <c>scripted_areatrigger</c> ids (scriptdev2.sql) rather than a table, since the imported world has none.
/// Runs on CMSG_AREATRIGGER after the volume check (<see cref="IAreaTriggerListener"/>); like the smart trigger seam it cannot end the
/// handler, which matches these scripts: each of them returns false in cmangos, or true only after it did nothing further.
/// Not yet ported: at_childrens_week_spot (mini-pet), at_murkdeep and at_hive_tower (map script data), at_huldar_miran (temporary
/// faction), at_wondervolt (trap use) and at_stormwind_recruiter (EventAI custom event).
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

        Run(quests, player, triggerId);
    }

    /// <summary>Runs the script bound to <paramref name="triggerId"/>; returns true when one ran an effect (credit or summon).</summary>
    public static bool Run(QuestNpcServices quests, Player player, uint triggerId)
    {
        ArgumentNullException.ThrowIfNull(quests);
        ArgumentNullException.ThrowIfNull(player);
        return triggerId switch
        {
            TriggerRavenholdt => Ravenholdt(quests, player),
            >= TriggerLarkorwiFirst and <= TriggerLarkorwiLast => ScentOfLarkorwi(quests, player),
            TriggerAncientLeaf => AncientLeaf(quests, player),
            TriggerTwilightGrove => TwilightGrove(quests, player),
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
    // cmangos summons at the trigger's centre; the player is inside the trigger volume, so the player's position stands in for it.
    private static bool ScentOfLarkorwi(QuestNpcServices quests, Player player)
    {
        if (!player.IsAlive || player.IsGameMaster || StatusOf(quests, player, QuestScentOfLarkorwi) != QuestStatus.Incomplete
            || Creatures(player) is not { } creatures || Closest(creatures, player, NpcLarkorwiMate, 25f, aliveOnly: false) is not null)
        {
            return false;
        }

        if (creatures.SummonInstanceCreature(NpcLarkorwiMate, player.X, player.Y, player.Z, 3.3f) is not { } mate)
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
    // Fragment of the Nightmare's Corruption, unless one is alive within 500 yd. The spawn whisper (SD2 -1000411) is not sent yet.
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
        return true;
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
