using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Quests;
using ArcaneCore.World.Social;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Npc;

/// <summary>
/// <see cref="IQuestParty"/> over the world host: the group manager of the social feature (when it is attached; otherwise
/// everyone is solo, as in the reward distribution) and the online player registry.
/// </summary>
public sealed class WorldQuestParty(IServiceProvider services, Func<WorldRuntime?> world) : IQuestParty
{
    private Group? GroupOf(Player player)
    {
        try
        {
            return services.GetService<SocialFeature>()?.Context.Groups.GetGroup(player.Guid);
        }
        catch (InvalidOperationException)
        {
            return null; // social feature not attached (tests, early startup)
        }
    }

    public IReadOnlyList<Player> MembersOf(Player player)
        => GroupOf(player) is { } group
            ? [.. group.Members.Select(m => FindPlayer(m.Guid)).OfType<Player>()]
            : [];

    public bool IsInSameGroup(Player first, Player second)
    {
        if (ReferenceEquals(first, second))
        {
            return true;
        }

        // vmangos Group::SameSubGroup: one group and the same sub-group.
        return GroupOf(first) is { } group && ReferenceEquals(group, GroupOf(second))
            && group.Find(first.Guid)?.SubGroup == group.Find(second.Guid)?.SubGroup;
    }

    public bool IsInSameRaid(Player first, Player second)
        => ReferenceEquals(first, second) || (GroupOf(first) is { } group && ReferenceEquals(group, GroupOf(second)));

    public Player? FindPlayer(ObjectGuid guid) => world()?.FindOnlinePlayer(guid);
}
