using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Social;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.GameObjects;

/// <summary>
/// <see cref="ILootQuestJournal"/> over the quest feature's journals (vmangos
/// Player::HasQuestForItem, GetQuestStatus, ItemAddedQuestCheck, CastedCreatureOrGO). Without
/// the quest feature every answer is "no" and events are dropped. Money and item changes also
/// queue a character save. World thread.
/// </summary>
internal sealed class QuestJournalAdapter(IServiceProvider services, WorldRuntime world) : ILootQuestJournal
{
    private QuestNpcServices? Quests => services.GetService<QuestNpcFeature>()?.Services;

    /// <summary>vmangos Player::HasQuestForItem over the quest feature's journal (the raid-group rule needs the groups).</summary>
    public bool NeedsQuestItem(Player player, uint itemId)
        => Quests?.HasQuestForItem(player, itemId, InRaidGroup(player)) ?? false;

    private bool InRaidGroup(Player player)
    {
        if (services.GetService<SocialFeature>() is not { } social)
        {
            return false;
        }

        try
        {
            return social.Context.Groups.GetGroup(player.Guid)?.IsRaid == true;
        }
        catch (InvalidOperationException)
        {
            return false; // social feature not attached
        }
    }

    public bool IsQuestIncomplete(Player player, uint questId)
        => Quests?.StateOf(player) is { Loaded: true } state && state.Quests.GetStatus(questId) == QuestStatus.Incomplete;

    public void ItemLooted(Player player, uint itemId, uint count)
    {
        // The inventory's ItemCountChanged is not routed to quests yet; loot reports its own
        // additions (vmangos StoreNewItem → ItemAddedQuestCheck).
        Quests?.ItemAdded(player, itemId, count);
        world.SavePlayer(player);
    }

    public void MoneyLooted(Player player)
    {
        Quests?.MoneyChanged(player);
        world.SavePlayer(player);
    }

    public void GameObjectUsed(Player player, uint entry, ObjectGuid guid)
        => Quests?.CastedCreatureOrGo(player, entry, guid, isCreature: false, spellId: 0);
}
