using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.Loot;

namespace ArcaneCore.World.Gm.Character.Boost;

/// <summary>
/// What the game-event tables bind to a running event, so a holiday item is not mistaken for a permanent one: the quests listed in
/// <c>game_event_quest</c> (inactive outside their event, vmangos GameEventMgr.cpp:578) and the creature entries whose every spawn is
/// listed under a positive event in <c>game_event_creature</c> (they exist only while the event runs; a negative event number
/// removes a permanent creature during the event and binds nothing). The Fire Festival crown and mantle (items 23323, 23324) are
/// rewards of such quests. Immutable once built; safe from any thread.
/// </summary>
internal sealed class BoostEventBinding
{
    public static BoostEventBinding None { get; } = new(new HashSet<uint>(), new HashSet<uint>());

    private BoostEventBinding(HashSet<uint> quests, HashSet<uint> creatureEntries)
    {
        Quests = quests;
        CreatureEntries = creatureEntries;
    }

    /// <summary>Quest ids listed under any event.</summary>
    public IReadOnlySet<uint> Quests { get; }

    /// <summary>Creature entries that spawn only through an event.</summary>
    public IReadOnlySet<uint> CreatureEntries { get; }

    /// <summary>Derives the binding from the loaded event rows and creature spawns.</summary>
    public static BoostEventBinding From(GameEventRows rows, CreatureContent creatures)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(creatures);
        var quests = new HashSet<uint>(rows.Quests.Values.SelectMany(list => list));
        var eventGuids = new HashSet<uint>();
        foreach ((int signedEvent, IReadOnlyList<uint> guids) in rows.Creatures)
        {
            if (signedEvent > 0)
            {
                eventGuids.UnionWith(guids);
            }
        }

        // An entry is event-only when it has spawns and none of them is outside the event lists.
        var permanent = new HashSet<uint>();
        var eventOnly = new HashSet<uint>();
        foreach (uint map in creatures.MapsWithSpawns)
        {
            foreach (CreatureSpawn spawn in creatures.GetSpawns(map))
            {
                if (eventGuids.Contains(spawn.Guid))
                {
                    eventOnly.Add(spawn.Entry);
                }
                else
                {
                    permanent.Add(spawn.Entry);
                }
            }
        }

        eventOnly.ExceptWith(permanent);
        return new BoostEventBinding(quests, eventOnly);
    }
}

/// <summary>
/// The item entries a player can actually obtain in the world: sold by a vendor (<c>npc_vendor</c>), offered by a quest
/// (<c>RewChoiceItemId1-6</c>, <c>RewItemId1-4</c>) or dropped by a loot table row (<c>mincountOrRef &gt;= 0</c>; a negative value is a
/// reference to another table, not an item), except what only an event offers (<see cref="BoostEventBinding"/>: rewards of
/// <c>game_event_quest</c> quests and wares of creatures that spawn only through <c>game_event_creature</c>; loot is not filtered by
/// event). The live item_template never sets ITEM_EXTRA_NOT_OBTAINABLE (vmangos
/// ItemPrototype.h:398) and carries test, monster-only and deprecated entries, so a source is required before an item is a gear
/// candidate. Pure over immutable content; safe from any thread.
/// </summary>
internal static class BoostItemSources
{
    /// <summary>The obtainable entries from the three sources (zero entries are ignored); event-bound quests and vendors add nothing.</summary>
    public static HashSet<uint> Build(NpcStore npcs, QuestStore quests, LootContent loot, BoostEventBinding? events = null)
    {
        events ??= BoostEventBinding.None;
        ArgumentNullException.ThrowIfNull(npcs);
        ArgumentNullException.ThrowIfNull(quests);
        ArgumentNullException.ThrowIfNull(loot);
        var entries = new HashSet<uint>();
        foreach (var vendor in npcs.Content.VendorItems)
        {
            if (!events.CreatureEntries.Contains(vendor.Entry))
            {
                entries.Add(vendor.Item);
            }
        }

        foreach (QuestTemplate quest in quests.Templates)
        {
            if (events.Quests.Contains(quest.Entry))
            {
                continue;
            }

            entries.Add(quest.RewChoiceItemId1);
            entries.Add(quest.RewChoiceItemId2);
            entries.Add(quest.RewChoiceItemId3);
            entries.Add(quest.RewChoiceItemId4);
            entries.Add(quest.RewChoiceItemId5);
            entries.Add(quest.RewChoiceItemId6);
            entries.Add(quest.RewItemId1);
            entries.Add(quest.RewItemId2);
            entries.Add(quest.RewItemId3);
            entries.Add(quest.RewItemId4);
        }

        foreach ((LootTableKind _, LootStoreRow row) in loot.Rows)
        {
            if (row.MinCountOrRef >= 0)
            {
                entries.Add(row.Item);
            }
        }

        entries.Remove(0);
        return entries;
    }

    /// <summary>
    /// The entries whose only source is a quest reward: rewarded by a quest that is not event-bound, and neither sold by a
    /// vendor that is there outside events nor dropped by a loot row. A subset of <see cref="Build"/>'s result. Used to keep
    /// reward-only cosmetics (a statless "trophy" piece) out of the boost kit (<see cref="BoostGearPlanner.IsCosmeticQuestReward"/>).
    /// </summary>
    public static HashSet<uint> QuestOnly(NpcStore npcs, QuestStore quests, LootContent loot, BoostEventBinding? events = null)
    {
        events ??= BoostEventBinding.None;
        ArgumentNullException.ThrowIfNull(npcs);
        ArgumentNullException.ThrowIfNull(quests);
        ArgumentNullException.ThrowIfNull(loot);
        var rewards = new HashSet<uint>();
        foreach (QuestTemplate quest in quests.Templates)
        {
            if (events.Quests.Contains(quest.Entry))
            {
                continue;
            }

            rewards.Add(quest.RewChoiceItemId1);
            rewards.Add(quest.RewChoiceItemId2);
            rewards.Add(quest.RewChoiceItemId3);
            rewards.Add(quest.RewChoiceItemId4);
            rewards.Add(quest.RewChoiceItemId5);
            rewards.Add(quest.RewChoiceItemId6);
            rewards.Add(quest.RewItemId1);
            rewards.Add(quest.RewItemId2);
            rewards.Add(quest.RewItemId3);
            rewards.Add(quest.RewItemId4);
        }

        foreach (var vendor in npcs.Content.VendorItems)
        {
            if (!events.CreatureEntries.Contains(vendor.Entry))
            {
                rewards.Remove(vendor.Item);
            }
        }

        foreach ((LootTableKind _, LootStoreRow row) in loot.Rows)
        {
            if (row.MinCountOrRef >= 0)
            {
                rewards.Remove(row.Item);
            }
        }

        rewards.Remove(0);
        return rewards;
    }
}
