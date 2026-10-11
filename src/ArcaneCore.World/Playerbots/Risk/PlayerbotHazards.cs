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
/// <para>
/// The creatures beyond the bot's strength (kind <see cref="StrongKind"/>, <see cref="PlayerbotRisk.ScanHazards"/>) are kept apart:
/// their own cap (<see cref="MaxStrong"/>), so the dozens a low-level bot sees in a higher zone never push a death or retreat place out;
/// updated in place by creature at each look (<see cref="SetStrong"/>), and those a look did not see alive dropped at its end
/// (<see cref="EndStrongScan"/>); each lapses on its own a few looks after the last one that saw it.
/// </para>
/// </summary>
internal sealed class PlayerbotHazards
{
    internal const float DeathYards = 25f;
    internal const float RetreatYards = 20f;
    internal const float LethalMarginYards = 8f;
    internal const float FledMarginYards = 3f;

    /// <summary>The margin round the reach of a creature beyond the bot's strength (<see cref="PlayerbotRisk.ScanHazards"/>), as round one it fled from.</summary>
    internal const float StrongMarginYards = FledMarginYards;

    /// <summary>The kind of a creature beyond the bot's strength.</summary>
    internal const string StrongKind = "strong";

    /// <summary>The most strong creatures kept (a look past it skips the rest).</summary>
    internal const int MaxStrong = 64;

    private const int Max = 64;

    private readonly List<PlayerbotHazard> _hazards = [];
    private readonly Dictionary<ObjectGuid, (PlayerbotHazard Hazard, uint Scan)> _strong = [];
    private uint _scan;

    internal int Count => _hazards.Count + _strong.Count;

    internal void Add(uint mapId, Vector3 at, float radius, uint nowMs, int seconds, string kind, ObjectGuid creature = default)
    {
        if (seconds <= 0 || radius <= 0 || !Finite(at)) return;
        uint until = unchecked(nowMs + ((uint)seconds * 1000u));
        if (!creature.IsEmpty)
            for (int i = _hazards.Count - 1; i >= 0; i--)
                if (_hazards[i].Creature == creature) _hazards.RemoveAt(i);
        Expire(nowMs);
        if (_hazards.Count >= Max) _hazards.RemoveAt(0);
        _hazards.Add(new PlayerbotHazard(mapId, at, radius, until, creature, kind));
    }

    /// <summary>Start a look over the visible creatures (<see cref="SetStrong"/> for each strong one, then <see cref="EndStrongScan"/>).</summary>
    internal void BeginStrongScan() => _scan = unchecked(_scan + 1);

    /// <summary>A strong creature seen alive by this look: its hazard is set (in place when it has one), lasting <paramref name="lifeMs"/>.</summary>
    internal void SetStrong(uint mapId, Vector3 at, float radius, uint nowMs, uint lifeMs, ObjectGuid creature)
    {
        if (creature.IsEmpty || radius <= 0 || !Finite(at)) return;
        if (_strong.Count >= MaxStrong && !_strong.ContainsKey(creature)) return;
        _strong[creature] = (new PlayerbotHazard(mapId, at, radius, unchecked(nowMs + lifeMs), creature, StrongKind), _scan);
    }

    /// <summary>End a look: drop the strong hazards of creatures it did not see alive and strong (killed, gone, out of sight, no longer too strong).</summary>
    internal void EndStrongScan()
    {
        foreach ((ObjectGuid creature, (PlayerbotHazard _, uint scan)) in _strong)
            if (scan != _scan) _strong.Remove(creature);
    }

    /// <summary>Drop every strong hazard (a death: the next life looks again).</summary>
    internal void ClearStrong() => _strong.Clear();

    /// <summary>The hazards on <paramref name="mapId"/> now (a copy: inspection, the ghost's revive spot).</summary>
    internal IReadOnlyList<PlayerbotHazard> Active(uint mapId, uint nowMs)
    {
        Expire(nowMs);
        var active = new List<PlayerbotHazard>(_hazards.Count + _strong.Count);
        foreach (PlayerbotHazard hazard in _hazards)
            if (hazard.MapId == mapId) active.Add(hazard);
        foreach ((PlayerbotHazard hazard, uint _) in _strong.Values)
            if (hazard.MapId == mapId && Live(hazard, nowMs)) active.Add(hazard);
        return active;
    }

    /// <summary>
    /// The first hazard <paramref name="points"/> pass through, sampled every 2 yards, or null. A hazard the walk starts in is not
    /// held against it (the bot may always walk out of one). Without <paramref name="strong"/> the strong creatures are not held
    /// against it either (<see cref="PlayerbotRisk.Blocking"/>'s relief). Copies nothing.
    /// </summary>
    internal PlayerbotHazard? FirstOnRoute(uint mapId, IReadOnlyList<Vector3> points, uint nowMs, bool strong = true)
    {
        if (points.Count < 2) return null;
        Expire(nowMs);
        strong &= _strong.Count > 0;
        if (_hazards.Count == 0 && !strong) return null;
        Vector3 start = points[0];
        for (int index = 1; index < points.Count; index++)
        {
            Vector3 a = points[index - 1], b = points[index];
            int steps = Math.Max(1, (int)MathF.Ceiling(Vector3.Distance(a, b) / 2f));
            for (int step = 1; step <= steps; step++)
            {
                Vector3 point = Vector3.Lerp(a, b, (float)step / steps);
                foreach (PlayerbotHazard hazard in _hazards)
                    if (hazard.MapId == mapId && hazard.Covers(point) && !hazard.Covers(start)) return hazard;
                if (!strong) continue;
                foreach ((PlayerbotHazard hazard, uint _) in _strong.Values)
                    if (hazard.MapId == mapId && Live(hazard, nowMs) && hazard.Covers(point) && !hazard.Covers(start)) return hazard;
            }
        }

        return null;
    }

    /// <summary>Lines of the escape fan (<see cref="TryFindGap"/>), 7.5 degrees apart.</summary>
    internal const int GapDirections = 48;

    /// <summary>The fan's lines are sampled every this many yards.</summary>
    internal const float GapStepYards = 2f;

    /// <summary>The fan's lines run this far.</summary>
    internal const float GapReachYards = 100f;

    /// <summary>A line clear of every hazard for its first this many yards is open: the bot is not boxed in.</summary>
    internal const float GapOpenYards = 40f;

    /// <summary>An exit lies this far past the last strong reach along its line.</summary>
    internal const float GapBeyondYards = 5f;

    /// <summary>A strong hazard's creature's own aggro reach (its radius without <see cref="StrongMarginYards"/>).</summary>
    internal static float StrongReach(in PlayerbotHazard hazard) => hazard.Radius - StrongMarginYards;

    /// <summary>
    /// The way out of a ring of strong creatures (<see cref="PlayerbotRisk.Refused"/>): a fan of <see cref="GapDirections"/> straight
    /// lines from <paramref name="from"/>, each sampled every <see cref="GapStepYards"/> to <see cref="GapReachYards"/> (at the height of
    /// <paramref name="from"/>; hazards it stands in, and other maps', are not counted, as for <see cref="FirstOnRoute"/>). False when
    /// any line is open (clear of every hazard for its first <see cref="GapOpenYards"/>): the bot is not boxed in. Otherwise the line
    /// with the least exposure (the sum, over its samples up to its exit and over the strong hazards, of how deep the sample lies inside
    /// the creature's own aggro reach, <see cref="StrongReach"/>), ties to the nearer exit, among the lines not in
    /// <paramref name="excluded"/> (bit k for line k) that leave every strong hazard within the reach (the exit
    /// <see cref="GapBeyondYards"/> past the last) and never touch another hazard (a death, retreat, lethal or fled one) up to the exit.
    /// False when none does. Allocates nothing.
    /// </summary>
    internal bool TryFindGap(uint mapId, Vector3 from, uint nowMs, ulong excluded, out Vector3 exit, out int direction)
    {
        exit = from;
        direction = -1;
        if (_strong.Count == 0) return false;
        Expire(nowMs);
        int samples = (int)(GapReachYards / GapStepYards);
        float best = float.PositiveInfinity, bestExit = float.PositiveInfinity;
        for (int k = 0; k < GapDirections; k++)
        {
            float angle = k * MathF.Tau / GapDirections;
            var along = new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0f);
            float firstBlocked = -1f, lastStrong = -1f;
            bool otherHit = false;
            float otherFirst = -1f;
            for (int i = 1; i <= samples; i++)
            {
                float distance = i * GapStepYards;
                Vector3 point = from + (along * distance);
                bool inStrong = false, inOther = false;
                foreach ((PlayerbotHazard hazard, uint _) in _strong.Values)
                    if (hazard.MapId == mapId && Live(hazard, nowMs) && hazard.Covers(point) && !hazard.Covers(from))
                    {
                        inStrong = true;
                        break;
                    }

                foreach (PlayerbotHazard hazard in _hazards)
                    if (hazard.MapId == mapId && hazard.Covers(point) && !hazard.Covers(from))
                    {
                        inOther = true;
                        break;
                    }

                if ((inStrong || inOther) && firstBlocked < 0) firstBlocked = distance;
                if (inStrong) lastStrong = distance;
                if (inOther && otherFirst < 0) otherFirst = distance;
                otherHit |= inOther;
            }

            if (firstBlocked < 0 || firstBlocked > GapOpenYards) return false; // an open line: not boxed in
            if (lastStrong < 0) continue;
            float exitAt = lastStrong + GapBeyondYards;
            if (exitAt > GapReachYards) continue;
            if (otherHit && otherFirst <= exitAt) continue;
            if ((excluded & (1UL << k)) != 0) continue;
            float exposure = 0f;
            for (int i = 1; i * GapStepYards <= exitAt; i++)
            {
                Vector3 point = from + (along * (i * GapStepYards));
                foreach ((PlayerbotHazard hazard, uint _) in _strong.Values)
                {
                    if (hazard.MapId != mapId || !Live(hazard, nowMs) || hazard.Covers(from)) continue;
                    float flat = Vector2.Distance(new Vector2(hazard.At.X, hazard.At.Y), new Vector2(point.X, point.Y));
                    exposure += MathF.Max(0f, StrongReach(hazard) - flat);
                }
            }

            if (exposure < best || (exposure == best && exitAt < bestExit))
            {
                best = exposure;
                bestExit = exitAt;
                direction = k;
                exit = from + (along * exitAt);
            }
        }

        return direction >= 0;
    }

    internal void Clear()
    {
        _hazards.Clear();
        _strong.Clear();
    }

    private static bool Live(in PlayerbotHazard hazard, uint nowMs) => unchecked((int)(hazard.UntilMs - nowMs)) > 0;

    private static bool Finite(Vector3 at) => float.IsFinite(at.X) && float.IsFinite(at.Y) && float.IsFinite(at.Z);

    private void Expire(uint nowMs)
    {
        for (int i = _hazards.Count - 1; i >= 0; i--)
            if (!Live(_hazards[i], nowMs)) _hazards.RemoveAt(i);
    }
}
