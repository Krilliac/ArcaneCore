using System.Numerics;

namespace ArcaneCore.World.Playerbots.Battlegrounds;

/// <summary>
/// The (goal, origin) pairs one bot has already reported as <c>bot-bg/objective-unreachable</c> in the current match: a refused walk is
/// reported once per pair (both quantised to <see cref="CellYards"/>) instead of once per match, so a bot that respawns at another graveyard,
/// or goes for another objective, is still told about, while the same refusal from the same spot every think is not repeated. Bounded to
/// <see cref="Capacity"/> pairs (the oldest is forgotten first). Owned by one <see cref="PlayerbotBattlegroundAI"/>; world thread only.
/// </summary>
internal sealed class PlayerbotObjectiveReports
{
    /// <summary>Side (yards) of the grid cell a goal and an origin are reduced to.</summary>
    internal const float CellYards = 10f;

    /// <summary>Most pairs remembered at once.</summary>
    internal const int Capacity = 16;

    private readonly record struct Pair(int GoalX, int GoalY, int OriginX, int OriginY);

    private readonly Queue<Pair> _pairs = new(Capacity);

    /// <summary>Number of pairs remembered.</summary>
    internal int Count => _pairs.Count;

    /// <summary>True the first time this (goal cell, origin cell) pair is seen since the last <see cref="Clear"/>; false for a repeat.</summary>
    internal bool TryAdd(Vector3 goal, Vector3 origin)
    {
        var pair = new Pair(Cell(goal.X), Cell(goal.Y), Cell(origin.X), Cell(origin.Y));
        if (_pairs.Contains(pair)) return false;
        if (_pairs.Count >= Capacity) _pairs.Dequeue();
        _pairs.Enqueue(pair);
        return true;
    }

    /// <summary>Forget every pair (the match is over).</summary>
    internal void Clear() => _pairs.Clear();

    private static int Cell(float coordinate)
        => float.IsFinite(coordinate) ? (int)MathF.Floor(coordinate / CellYards) : int.MinValue;
}
