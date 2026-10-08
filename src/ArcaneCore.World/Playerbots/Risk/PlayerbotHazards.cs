using System.Numerics;
using ArcaneCore.Game;

namespace ArcaneCore.World.Playerbots;

/// <summary>One place a living bot keeps out of: a remembered death or retreat, or a creature that kills outright (or one it fled from).</summary>
internal readonly record struct PlayerbotHazard(uint MapId, Vector3 At, float Radius, uint UntilMs, ObjectGuid Creature, string Kind)
{
    /// <summary>Whether <paramref name="point"/> lies inside (flat distance, a few yards of height either way do not matter).</summary>
    internal bool Covers(Vector3 point) => Vector2.Distance(new(At.X, At.Y), new(point.X, point.Y)) < Radius && MathF.Abs(At.Z - point.Z) < Radius;
}

/// <summary>
/// The places a bot keeps out of on every walk while it is alive (<see cref="PlayerbotNavigation"/> asks before it hands out a
/// route): the spot of a death (<see cref="DeathYards"/>), the spot where a fight turned and the bot fled (<see cref="RetreatYards"/>),
/// each creature with a spell that kills the bot outright (its aggro radius plus <see cref="LethalMarginYards"/>, following the
/// creature while the bot sees it) and each creature it fled from or died to (its aggro radius plus <see cref="FledMarginYards"/>).
/// Every entry lasts <see cref="PlayerbotRiskOptions.DangerMemorySeconds"/>. A route through one is walked round when a detour
/// clears it (<see cref="PlayerbotNavigation"/>), else refused: the goal then picks another destination (another trainer, vendor or
/// quest giver) or sets the errand aside. The same places count as camped for the ghost's revive spot (<see cref="PlayerbotRecovery"/>).
/// Bounded; the oldest goes first. World thread.
/// </summary>
internal sealed class PlayerbotHazards
{
    internal const float DeathYards = 25f;
    internal const float RetreatYards = 20f;
    internal const float LethalMarginYards = 8f;
    internal const float FledMarginYards = 3f;
    private const int Max = 64;

    private readonly List<PlayerbotHazard> _hazards = [];

    internal int Count => _hazards.Count;

    internal void Add(uint mapId, Vector3 at, float radius, uint nowMs, int seconds, string kind, ObjectGuid creature = default)
    {
        if (seconds <= 0 || radius <= 0 || !float.IsFinite(at.X) || !float.IsFinite(at.Y) || !float.IsFinite(at.Z)) return;
        uint until = unchecked(nowMs + ((uint)seconds * 1000u));
        if (!creature.IsEmpty) _hazards.RemoveAll(h => h.Creature == creature);
        Expire(nowMs);
        if (_hazards.Count >= Max) _hazards.RemoveAt(0);
        _hazards.Add(new PlayerbotHazard(mapId, at, radius, until, creature, kind));
    }

    internal IReadOnlyList<PlayerbotHazard> Active(uint mapId, uint nowMs)
    {
        Expire(nowMs);
        return [.. _hazards.Where(h => h.MapId == mapId)];
    }

    /// <summary>
    /// The first hazard <paramref name="points"/> pass through, sampled every 2 yards, or null. A hazard the walk starts in is not
    /// held against it (the bot may always walk out of one).
    /// </summary>
    internal PlayerbotHazard? FirstOnRoute(uint mapId, IReadOnlyList<Vector3> points, uint nowMs)
    {
        if (points.Count == 0) return null;
        IReadOnlyList<PlayerbotHazard> active = Active(mapId, nowMs);
        if (active.Count == 0) return null;
        Vector3 start = points[0];
        PlayerbotHazard[] relevant = [.. active.Where(h => !h.Covers(start))];
        if (relevant.Length == 0) return null;
        for (int index = 1; index < points.Count; index++)
        {
            Vector3 a = points[index - 1], b = points[index];
            int steps = Math.Max(1, (int)MathF.Ceiling(Vector3.Distance(a, b) / 2f));
            for (int step = 1; step <= steps; step++)
            {
                Vector3 point = Vector3.Lerp(a, b, (float)step / steps);
                foreach (PlayerbotHazard hazard in relevant)
                    if (hazard.Covers(point)) return hazard;
            }
        }

        return null;
    }

    internal void Clear() => _hazards.Clear();

    private void Expire(uint nowMs) => _hazards.RemoveAll(h => unchecked((int)(h.UntilMs - nowMs)) <= 0);
}
