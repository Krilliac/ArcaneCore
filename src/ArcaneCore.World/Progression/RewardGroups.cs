using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Progression;
using ArcaneCore.World.Social;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Progression;

/// <summary>Resolves a player's group for kill/cast rewards from the social feature, when it is attached.</summary>
public static class RewardGroups
{
    public static Func<Player, RewardGroup?> Resolver(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        SocialFeature? social = services.GetService<SocialFeature>();
        return player =>
        {
            Group? group;
            try
            {
                group = social?.Context.Groups.GetGroup(player.Guid);
            }
            catch (InvalidOperationException)
            {
                return null; // social feature not attached (tests, early startup): everyone is solo
            }

            return group is null ? null : new RewardGroup(group.Members.Select(m => m.Guid).ToArray(), group.IsRaid);
        };
    }
}
