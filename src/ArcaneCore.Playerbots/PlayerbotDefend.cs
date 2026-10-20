using System.Numerics;
using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// A defend post (<c>.playerbot defend</c>): bots holding it fight hostile players and help friendly creatures inside <see cref="Radius"/>
/// yards of <see cref="Centre"/> on <see cref="MapId"/>. <see cref="PostId"/> names the command that set it: bots of one post heal each other.
/// Memory only: a restart, a stop or a clear ends it; nothing is persisted.
/// </summary>
internal sealed record PlayerbotDefendPost(uint MapId, Vector3 Centre, float Radius, int PostId);

/// <summary>The rules and the roster of defend posts (<see cref="PlayerbotDefendPost"/>).</summary>
internal static class PlayerbotDefend
{
    /// <summary>The smallest and greatest radius of a post, in yards ([AC]: a post smaller than a few bodies, or wider than a city, is a typo).</summary>
    internal const float MinRadius = 10f;

    internal const float MaxRadius = 200f;

    // Who holds which post. Keyed by the player object, so the roster of one world never meets another's and a bot that leaves takes its entry along.
    private static readonly ConditionalWeakTable<Player, PlayerbotDefendPost> s_posts = new();

    /// <summary>The radius of a post for a requested one.</summary>
    internal static float ClampRadius(float radius) => Math.Clamp(radius, MinRadius, MaxRadius);

    /// <summary>Publish that <paramref name="player"/> holds <paramref name="post"/>.</summary>
    internal static void Register(Player player, PlayerbotDefendPost post) => s_posts.AddOrUpdate(player, post);

    /// <summary>The player no longer holds a post.</summary>
    internal static void Unregister(Player player) => s_posts.Remove(player);

    /// <summary>The post <paramref name="player"/> holds, or null.</summary>
    internal static PlayerbotDefendPost? PostOf(Player player) => s_posts.TryGetValue(player, out PlayerbotDefendPost? post) ? post : null;
}
