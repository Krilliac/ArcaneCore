using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Spells;

/// <summary>
/// Free-for-all friendliness through the real group manager: two players of one team who both carry the free-for-all PvP flag are hostile
/// (an arena), unless they share a group (vmangos GetReactionTo, Object.cpp:3640-3663; Player::IsInRaidWith). Proves the production wiring:
/// <see cref="SpellFeature"/> registers <see cref="WorldSpellGroups.AreInSameGroup"/> over the social area's <see cref="GroupManager"/>.
/// </summary>
public sealed class FreeForAllGroupFriendlinessTests
{
    [Fact]
    public void AreInSameGroup_IsTrueForGroupMates_AndFalseForAPlayerOutsideTheGroup()
    {
        using var kit = new AreaAuraGroupKit();
        var groups = new WorldSpellGroups(() => kit.Groups);

        Assert.True(groups.AreInSameGroup(kit.Players[1], kit.Players[2]));
        kit.Groups.Leave(kit.Players[5]);
        Assert.False(groups.AreInSameGroup(kit.Players[1], kit.Players[5]));
        Assert.False(groups.AreInSameGroup(kit.Players[5], kit.Players[1]));
        Assert.True(groups.AreInSameGroup(kit.Players[1], kit.Players[2]));
    }

    [Fact]
    public void AfterSpellFeatureAttaches_GroupedFreeForAllPlayersAreFriends_AndUngroupedOnesAreHostile()
    {
        using var kit = new AreaAuraGroupKit();
        using ServiceProvider services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<TeleportFeature>()
            .AddSingleton(kit.Social)
            .BuildServiceProvider();
        services.GetRequiredService<TeleportFeature>().Attach(kit.World);
        new SpellFeature(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<SpellFeature>.Instance).Attach(kit.World);

        Player grouped = kit.Players[1];
        Player mate = kit.Players[2];
        Player outsider = kit.Players[5];
        kit.Groups.Leave(outsider);
        foreach (Player p in new[] { grouped, mate, outsider })
        {
            p.Flags |= PlayerFlags.FfaPvp;
        }

        Assert.Equal(grouped.Team, outsider.Team);
        CombatHooks hooks = CombatHooks.Default;
        Assert.True(hooks.IsFriendly(grouped, mate));
        Assert.True(hooks.IsFriendly(mate, grouped));
        Assert.False(hooks.IsFriendly(grouped, outsider));
        Assert.False(hooks.IsFriendly(outsider, mate));

        // Without the free-for-all flag the same team is friendly whether grouped or not.
        outsider.Flags &= ~PlayerFlags.FfaPvp;
        Assert.True(hooks.IsFriendly(grouped, outsider));
    }
}
