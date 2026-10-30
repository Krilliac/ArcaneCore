using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Social;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.GameObjects;

/// <summary>
/// <see cref="ILootQuestJournal"/> over the quest feature's journals (vmangos
/// Player::HasQuestForItem, GetQuestStatus, CastedCreatureOrGO). Without
/// the quest feature every answer is "no" and events are dropped. Looted items are counted by the quest feature's inventory
/// listener (ItemAddedQuestCheck), not here; money and item loot only queue a character save. World thread.
/// </summary>
internal sealed class QuestJournalAdapter(IServiceProvider services, WorldRuntime world) : ILootQuestJournal
{
    private QuestNpcServices? Quests => services.GetService<QuestNpcFeature>()?.Services;

    /// <summary>
    /// vmangos Player::HasQuestForItem over the quest feature's journal (the raid-group rule needs the groups).
    /// Fails closed: when the social feature is registered but not attached the raid fact is unknown, so the
    /// item is not offered (logged), rather than showing quest items a raid member must not see.
    /// </summary>
    public bool NeedsQuestItem(Player player, uint itemId)
        => Quests is { } quests && InRaidGroup(player) is { } inRaid && quests.HasQuestForItem(player, itemId, inRaid);

    /// <summary>True in a raid group; false when grouped otherwise or without the social feature; null when unknown.</summary>
    private bool? InRaidGroup(Player player)
    {
        if (services.GetService<SocialFeature>() is not { } social)
        {
            return false; // no social feature, so no groups exist
        }

        SocialContext context;
        try
        {
            context = social.Context;
        }
        catch (InvalidOperationException ex)
        {
            services.GetService<ILoggerFactory>()?.CreateLogger<QuestJournalAdapter>()
                .LogWarning(ex, "Quest item visibility for {Player} unknown: social feature not attached", player.Guid);
            return null;
        }

        return context.Groups.GetGroup(player.Guid)?.IsRaid == true;
    }
    public bool IsQuestIncomplete(Player player, uint questId)
        => Quests?.StateOf(player) is { Loaded: true } state && state.Quests.GetStatus(questId) == QuestStatus.Incomplete;

    public void ItemLooted(Player player, uint itemId, uint count)
    {
        // The quest feature already hears every add through PlayerInventory.ItemCountChanged (vmangos StoreNewItem ->
        // ItemAddedQuestCheck), which AddItem raised for this loot. Reporting it again here counted each looted quest
        // item twice, so quest 789's counter hit 10 after 5 tails and the drop stopped.
        world.SavePlayer(player);
    }

    public void MoneyLooted(Player player)
    {
        Quests?.MoneyChanged(player);
        world.SavePlayer(player);
    }

    public void GameObjectUsed(Player player, uint entry, ObjectGuid guid)
        => Quests?.CastedCreatureOrGo(player, entry, guid, isCreature: false, spellId: 0);

    public void GameObjectUsed(Player member, uint entry, ObjectGuid guid, bool originalCaster)
        => Quests?.CastedCreatureOrGo(member, entry, guid, isCreature: false, spellId: 0, originalCaster);
}
