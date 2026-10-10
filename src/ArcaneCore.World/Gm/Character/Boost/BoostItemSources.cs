using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.Loot;

namespace ArcaneCore.World.Gm.Character.Boost;

/// <summary>
/// The item entries a player can actually obtain in the world: sold by a vendor (<c>npc_vendor</c>), offered by a quest
/// (<c>RewChoiceItemId1-6</c>, <c>RewItemId1-4</c>) or dropped by a loot table row (<c>mincountOrRef &gt;= 0</c>; a negative value is a
/// reference to another table, not an item). The live item_template never sets ITEM_EXTRA_NOT_OBTAINABLE (vmangos
/// ItemPrototype.h:398) and carries test, monster-only and deprecated entries, so a source is required before an item is a gear
/// candidate. Pure over immutable content; safe from any thread.
/// </summary>
internal static class BoostItemSources
{
    /// <summary>The obtainable entries from the three sources (zero entries are ignored).</summary>
    public static HashSet<uint> Build(NpcStore npcs, QuestStore quests, LootContent loot)
    {
        ArgumentNullException.ThrowIfNull(npcs);
        ArgumentNullException.ThrowIfNull(quests);
        ArgumentNullException.ThrowIfNull(loot);
        var entries = new HashSet<uint>();
        foreach (var vendor in npcs.Content.VendorItems)
        {
            entries.Add(vendor.Item);
        }

        foreach (QuestTemplate quest in quests.Templates)
        {
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
}
