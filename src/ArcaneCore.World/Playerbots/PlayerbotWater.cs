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

    /// <summary>The rings a search samples (yards), nearest first, and the directions on each.</summary>
    private static ReadOnlySpan<float> Radii => [8f, 16f, 32f, 64f, 96f];

    private const int Directions = 16;

    /// <summary>
    /// A route to a dry floor, sought once per escape attempt. Candidates are sampled on rings of 8, 16, 32, 64 and 96 yards (16
    /// directions each), and the dry ones are planned a ring at a time in turn: the first dry candidate of each ring, nearest ring
    /// first, then the second of each, and so on, at most <see cref="MaxPlans"/> plans. The result is the first walkable one in that
    /// order, null when none of those plans succeeds.
    /// <para>
    /// Every ring is asked within the budget. The search is the same each time from the same place (the caller retries it every two
    /// seconds while the bot stays put under water), so a budget spent on the near rings alone, when their dry candidates are a cliff
    /// top or a rock no walk reaches, never reached the beach further out, and the bot drowned waiting (bot QA #271).
    /// </para>
    /// </summary>
    internal static PlayerbotRoute? FindExit(Player player, PlayerbotOptions options)
    {
        if (player.Map is not { } map) return null;
        Vector3 from = new(player.X, player.Y, player.Z);
        float height = player.Locomotion.CollisionHeight;
        // Sample liquid cheaply before asking the mesh; a failed search is retried on a world-time bound.
        ReadOnlySpan<float> radii = Radii;
        Span<Vector3> dry = stackalloc Vector3[Radii.Length * Directions];
        Span<int> counts = stackalloc int[Radii.Length];
        for (int ring = 0; ring < radii.Length; ring++)
        {
            for (int step = 0; step < Directions; step++)
            {
                float angle = step * MathF.Tau / Directions;
                float x = from.X + radii[ring] * MathF.Cos(angle), y = from.Y + radii[ring] * MathF.Sin(angle);
                float floor = map.Collision.GetHeight(x, y, from.Z + 20f);
                if (PlayerbotNavigation.InvalidHeight(floor)) continue;
                Vector3 candidate = new(x, y, floor);
                if (Submerged(map, candidate, height)) continue;
                dry[(ring * Directions) + counts[ring]++] = candidate;
            }
        }

        int plans = 0;
        for (int turn = 0; turn < Directions; turn++)
        {
            for (int ring = 0; ring < radii.Length; ring++)
            {
                if (turn >= counts[ring]) continue;
                if (++plans > MaxPlans) return null;
                Vector3 candidate = dry[(ring * Directions) + turn];
                if (PlayerbotNavigation.TryPlan(player, candidate, options, out PlayerbotRoute? route, trial: true)
                    && route is { Complete: false } && !Submerged(map, route.Points[^1], height)) return route;
            }
        }

        return null;
    }
}
