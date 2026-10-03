using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Loot;

namespace ArcaneCore.Game.Loot;

// Special loot sources (fishing bobbers and holes, pickpocketed creatures, disenchanted items): the corpse, chest and
// item flows of LootService stay as they are; these members let a source that is none of those open, validate and settle
// its own loot window (vmangos Player::SendLoot / DoLootRelease branch on the source type, LootHandler.cpp).
public sealed partial class LootService
{
    /// <summary>The container item loot (lockboxes, clams): opens a lootable item into its saved loot. Null keeps <see cref="OpenItem"/> fail-closed.</summary>
    public IItemLootSource? ItemLoot { get; set; }

    /// <summary>
    /// Register <paramref name="bag"/> as the loot of <paramref name="source"/> and open its window for
    /// <paramref name="player"/>. An older bag of the same source is closed for its viewers first. The caller sets
    /// <see cref="LootBag.ReleaseHandler"/> and friends and has checked everything the source type requires.
    /// </summary>
    public LootResult ShowSpecial(Player player, WorldObject source, LootBag bag)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(bag);
        if (_bags.TryGetValue(source.Guid, out var previous) && !ReferenceEquals(previous.Bag, bag))
        {
            CloseForViewers(previous.Bag);
        }

        _bags[source.Guid] = (source, bag);
        return Show(player, bag);
    }

    /// <summary>Forget the loot registered for <paramref name="source"/> (its window closes for every viewer without a release handler call).</summary>
    public void RemoveSpecial(WorldObject source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ForgetLoot(source);
    }

    // --- skinning --------------------------------------------------------------------------------

    /// <summary>vmangos <c>LootTemplates_Skinning.HaveLootFor(skinning_loot_id)</c>: the creature has skinning loot at all.</summary>
    public bool HasSkinningLoot(Creature creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        return Content.FindCreature(creature.Entry) is { SkinningLootId: not 0 } info && Content.HasEntry(LootTableKind.Skinning, info.SkinningLootId);
    }

    /// <summary>
    /// vmangos Creature::IsSkinnableBy (Creature.h:308): the head start of the tapper has run out, or <paramref name="player"/> is a tapper. Combat
    /// keeps no tap list yet, so the recipients of the corpse loot (the killer and their group at the time) stand in for <c>IsTappedBy</c>.
    /// </summary>
    public bool IsSkinnableBy(Player player, Creature creature)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(creature);
        return creature.SkinningForOthersMs == 0
            || (_bags.TryGetValue(creature.Guid, out var entry) && entry.Bag.Recipients.Contains(player.Guid));
    }

    /// <summary>vmangos <c>loot.isLooted()</c> of a corpse: nothing (money or items) is left to take. A corpse without loot counts as looted.</summary>
    public bool IsCorpseLooted(Creature creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        return !_bags.TryGetValue(creature.Guid, out var entry) || entry.Bag.Kind != LootSourceKind.Creature || entry.Bag.IsEmpty;
    }

    /// <summary>Forget the loot registered under <paramref name="source"/> whose object may already be gone (a disenchanted item).</summary>
    public void RemoveSpecial(ObjectGuid source)
    {
        if (_bags.Remove(source, out var entry))
        {
            CloseForViewers(entry.Bag);
        }
    }

    /// <summary>A new bag takes over the registration of <paramref name="source"/>: the windows of the older, different bag close (a pickpocketed creature dying).</summary>
    private void CloseReplacedBag(ObjectGuid source, LootBag next)
    {
        if (_bags.TryGetValue(source, out var previous) && !ReferenceEquals(previous.Bag, next))
        {
            CloseForViewers(previous.Bag);
        }
    }

    private static bool IsAliveInSameMap(Player player, WorldObject source)
        => player.IsAlive && ReferenceEquals(player.Map, source.Map);
}