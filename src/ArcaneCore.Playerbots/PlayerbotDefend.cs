using System.Numerics;
using System.Runtime.CompilerServices;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Groups;

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

    // Who turned CMSG_TOGGLE_PVP on for a post. Keyed by the player object, not the brain: a replaced brain, a stop and a hot reload all keep the
    // bot's flag, so whoever ends the post can take the flag off again (and no persisted PvpDesired outlives it).
    private static readonly ConditionalWeakTable<Player, object> s_flagOwned = new();
    private static readonly object s_marker = new();

    /// <summary>Note that a post turned <paramref name="player"/>'s PvP flag on (it is taken off again by <see cref="ReleaseFlag"/>).</summary>
    internal static void OwnFlag(Player player) => s_flagOwned.AddOrUpdate(player, s_marker);

    /// <summary>Whether a post turned the player's PvP flag on and it has not been released yet.</summary>
    internal static bool OwnsFlag(Player player) => s_flagOwned.TryGetValue(player, out _);

    /// <summary>
    /// Take the PvP flag a post turned on off again: CMSG_TOGGLE_PVP 0 (the server's five-minute linger then runs), and the toggle itself when
    /// the packet is refused, so no desire is left to be saved with the character. A flag the post did not turn on is left alone. World thread.
    /// </summary>
    internal static void ReleaseFlag(WorldSession session, Player player)
    {
        if (!OwnsFlag(player)) return;
        if ((player.Flags & PlayerFlags.PvpDesired) != 0)
        {
            PlayerbotGroupCoordinator.Act(session, WorldOpcode.CmsgTogglePvp, [0], budgeted: false);
            if ((player.Flags & PlayerFlags.PvpDesired) != 0) player.Map?.Combat.TogglePvp(player, false);
        }

        if ((player.Flags & PlayerFlags.PvpDesired) == 0) s_flagOwned.Remove(player);
    }

    /// <summary>The radius of a post for a requested one.</summary>
    internal static float ClampRadius(float radius) => Math.Clamp(radius, MinRadius, MaxRadius);

    /// <summary>Publish that <paramref name="player"/> holds <paramref name="post"/>.</summary>
    internal static void Register(Player player, PlayerbotDefendPost post) => s_posts.AddOrUpdate(player, post);

    /// <summary>The player no longer holds a post.</summary>
    internal static void Unregister(Player player) => s_posts.Remove(player);

    /// <summary>The post <paramref name="player"/> holds, or null.</summary>
    internal static PlayerbotDefendPost? PostOf(Player player) => s_posts.TryGetValue(player, out PlayerbotDefendPost? post) ? post : null;
}
