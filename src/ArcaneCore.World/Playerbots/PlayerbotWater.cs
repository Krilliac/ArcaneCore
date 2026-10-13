using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Terrain;

namespace ArcaneCore.World.Playerbots;

/// <summary>Breath safety for bot decisions. The live flag and candidate test follow
/// LiquidEnvironment.UpdateTerrainFlags (vmangos src/game/Objects/Player.cpp,
/// Player::UpdateTerainEnvironmentFlags:20353-20420).</summary>
internal static class PlayerbotWater
{
    internal static bool Submerged(Player player)
        => (player.Locomotion.Environment & EnvironmentFlags.Underwater) != 0;

    /// <summary>
    /// Breath is counting down and at most <paramref name="remainMs"/> is left. A water-breathing aura regenerates (positive scale)
    /// and is not running out. The flag alone is not enough: a ford sets it for a step, and the timer is what drowns the bot.
    /// </summary>
    internal static bool BreathRunningOut(Player player, uint remainMs = 20_000)
    {
        MirrorTimer breath = player.Locomotion.MirrorTimers[(int)MirrorTimerType.Breath];
        return breath.IsActive && breath.Scale < 0 && breath.Remaining <= remainMs;
    }

    /// <summary>Yards kept clear after a drowning, wider than an ordinary death mark: the lethal water is the cove, not the corpse.</summary>
    internal const float DrowningHazardYards = 80f;

    internal static bool Submerged(Map map, Vector3 at, float collisionHeight)
    {
        LiquidStatus status = map.GetLiquidStatus(at.X, at.Y, at.Z + 0.01f, LiquidTypeFlags.AllLiquids, out LiquidData liquid);
        return (status & LiquidStatus.UnderWater) != 0 && liquid.Level > at.Z + collisionHeight;
    }

    /// <summary>Most plans one exit search may ask the mesh for (each is a path query).</summary>
    private const int MaxPlans = 8;

    /// <summary>A route to a dry floor, sought once per escape attempt. Candidates are sampled on rings of 8, 16, 32, 64 and 96 yards
    /// (16 directions each), nearest ring first, and only the first <see cref="MaxPlans"/> dry candidates in that order are planned:
    /// the result is the first walkable one in ring order, not the globally nearest, and null when none of those plans succeeds
    /// (further rings are then not tried until the caller retries).</summary>
    internal static PlayerbotRoute? FindExit(Player player, PlayerbotOptions options)
    {
        if (player.Map is not { } map) return null;
        Vector3 from = new(player.X, player.Y, player.Z);
        float height = player.Locomotion.CollisionHeight;
        // Short rings first. Sample liquid cheaply before asking the mesh; a failed search is retried on a world-time bound.
        ReadOnlySpan<float> radii = [8f, 16f, 32f, 64f, 96f];
        int plans = 0;
        foreach (float radius in radii)
        {
            for (int step = 0; step < 16; step++)
            {
                float angle = step * MathF.Tau / 16f;
                float x = from.X + radius * MathF.Cos(angle), y = from.Y + radius * MathF.Sin(angle);
                float floor = map.Collision.GetHeight(x, y, from.Z + 20f);
                if (PlayerbotNavigation.InvalidHeight(floor)) continue;
                Vector3 candidate = new(x, y, floor);
                if (Submerged(map, candidate, height)) continue;
                if (++plans > MaxPlans) return null;
                if (PlayerbotNavigation.TryPlan(player, candidate, options, out PlayerbotRoute? route, trial: true)
                    && route is { Complete: false } && !Submerged(map, route.Points[^1], height)) return route;
            }
        }

        return null;
    }
}
