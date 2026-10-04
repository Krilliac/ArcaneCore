using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Quests;

/// <summary>
/// The group and player lookups quest sharing needs (vmangos <c>Player::GetGroup</c>, <c>IsInSameGroupWith</c>,
/// <c>IsInSameRaidWith</c> and <c>ObjectAccessor::FindPlayer</c>). Owned by the world host, which resolves them from its
/// group manager and player registry. Without one nothing can be shared and quests that confirm to the party are withheld.
/// </summary>
public interface IQuestParty
{
    /// <summary>The online members of the player's group, the player included; empty when not grouped.</summary>
    IReadOnlyList<Player> MembersOf(Player player);

    /// <summary>Both players are in one group (vmangos Player::IsInSameGroupWith).</summary>
    bool IsInSameGroup(Player first, Player second);

    /// <summary>Both players are in one raid group (vmangos Player::IsInSameRaidWith).</summary>
    bool IsInSameRaid(Player first, Player second);

    /// <summary>An online player of any map by guid (vmangos ObjectAccessor::FindPlayer), or null.</summary>
    Player? FindPlayer(ObjectGuid guid);
}
