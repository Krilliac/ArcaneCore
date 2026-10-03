using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Loot;

// Special loot sources (fishing bobbers and holes, pickpocketed creatures, disenchanted items): the corpse, chest and
// item flows of LootService stay as they are; these members let a source that is none of those open, validate and settle
// its own loot window (vmangos Player::SendLoot / DoLootRelease branch on the source type, LootHandler.cpp).
public sealed partial class LootService
{
    /// <summary>
    /// Offered first by <see cref="Open"/> (CMSG_LOOT): a non-null answer is the result, null falls through to the corpse path.
    /// The pickpocket area answers for a rogue reopening a pickpocketed creature (vmangos LootHandler.cpp:100-110).
    /// </summary>
    public Func<Player, ObjectGuid, LootResult?>? SpecialOpen { get; set; }

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

    private static bool IsAliveInSameMap(Player player, WorldObject source)
        => player.IsAlive && ReferenceEquals(player.Map, source.Map);
}