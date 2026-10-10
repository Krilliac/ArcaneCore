using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// vmangos event-driven proximity aggro (<see cref="AggroScanMode.Relocation"/>). A player or creature that moves, or joins
/// the map, schedules one AI notify after <see cref="CreatureOptions.AiRelocationNotifyDelayMs"/> unless one is already
/// pending (Unit::OnRelocated, Unit::ScheduleAINotify, Objects/Unit.cpp:10082-10160). When it runs, a player notifies the
/// creatures around it; a creature notifies the players around it and, with <see cref="CreatureOptions.CreatureAggroOnCreatures"/>,
/// the creatures around it in both directions (mangos CreatureCreatureRelocationWorker, WorldHandlers/GridNotifiersImpl.h:67-84:
/// each of the pair gets <c>MoveInLineOfSight</c> for the other), within <see cref="CreatureOptions.MaxCreatureAttackRadius"/> times
/// the aggro rate (RelocationNotifyEvent::Execute, Unit.cpp:10089-10099; PlayerRelocationNotifier / CreatureRelocationNotifier,
/// Maps/GridNotifiersImpl.h:57-119). Each notified creature runs <c>MoveInLineOfSight</c> for the mover when it is alive, in
/// control and not evading (CallAIMoveLOS, GridNotifiersImpl.h:57-69). A unit that stands still triggers nothing.
/// <para>
/// The candidates come from the map's cell index (<see cref="Maps.Grid.GridContainer.CollectObjects"/>, the cells a circle of the
/// radius touches, like vmangos' Cell::Visit), so a notify costs the objects of those cells, not the map; the scratch list is
/// reused across notifies (no per-notify allocation once it has grown). Differences from vmangos, listed in docs/areas/creature-ai.md:
/// a plain 2D radius over the cells instead of the exact cell visit; stealth and detection are the host's (CallAiMoveInLineOfSight).
/// </para>
/// Thread affinity: world thread.
/// </summary>
internal sealed class AiRelocationNotifier(CreatureMapSystem system, CreatureOptions options)
{
    private readonly Dictionary<WorldObject, long> _due = new(ReferenceEqualityComparer.Instance);
    private readonly List<WorldObject> _order = [];
    private readonly List<WorldObject> _candidates = [];
    private readonly HashSet<WorldObject> _seen = new(ReferenceEqualityComparer.Instance);
    private readonly List<WorldObject> _dueScratch = [];
    private bool _dueScratchInUse;

    /// <summary>Notifies waiting for their delay.</summary>
    public int PendingCount => _order.Count;

    /// <summary>A unit moved or joined the map at clock time <paramref name="nowMs"/>.</summary>
    public void OnRelocated(WorldObject obj, long nowMs)
    {
        if (obj is not (Player or Creature) || _due.ContainsKey(obj))
        {
            return;
        }

        _due[obj] = nowMs + options.AiRelocationNotifyDelayMs;
        _order.Add(obj);
    }

    /// <summary>Run every notify whose delay has passed (in the order they were scheduled).</summary>
    public void Update(long nowMs)
    {
        if (_order.Count == 0)
        {
            return;
        }

        // One pass: the due notifies leave the queue in their scheduled order, the rest keep theirs (removing them one by one
        // was quadratic in the queue length, which grows with every moving unit of the map).
        bool ownsScratch = !_dueScratchInUse;
        List<WorldObject> due = ownsScratch ? _dueScratch : [];
        _dueScratchInUse = true;
        try
        {
            int kept = 0;
            for (int i = 0; i < _order.Count; i++)
            {
                WorldObject obj = _order[i];
                if (_due[obj] <= nowMs)
                {
                    due.Add(obj);
                    _due.Remove(obj);
                }
                else
                {
                    _order[kept++] = obj;
                }
            }

            _order.RemoveRange(kept, _order.Count - kept);
            foreach (WorldObject obj in due)
            {
                Notify(obj);
            }
        }
        finally
        {
            due.Clear();
            if (ownsScratch)
            {
                _dueScratchInUse = false;
            }
        }
    }

    private void Notify(WorldObject obj)
    {
        float radius = options.MaxCreatureAttackRadius * options.AggroRate;
        if (radius <= 0 || !ReferenceEquals(obj.Map, system.Map))
        {
            return;
        }

        if (obj is Player player)
        {
            if (!player.IsAlive)
            {
                return;
            }

            foreach (WorldObject candidate in Collect(player, radius))
            {
                if (candidate is Creature creature && ReferenceEquals(creature.System, system) && creature.IsAlive)
                {
                    system.CallAiMoveInLineOfSight(creature, player);
                }
            }
        }
        else if (obj is Creature mover)
        {
            if (!mover.IsAlive || !ReferenceEquals(mover.System, system))
            {
                return;
            }

            bool creatures = options.CreatureAggroOnCreatures;
            foreach (WorldObject candidate in Collect(mover, radius))
            {
                if (!mover.IsAlive)
                {
                    break; // a reaction killed the mover
                }

                if (candidate is Player nearby)
                {
                    if (nearby.IsAlive)
                    {
                        system.CallAiMoveInLineOfSight(mover, nearby);
                    }
                }
                else if (creatures && candidate is Creature other && !ReferenceEquals(other, mover) && ReferenceEquals(other.System, system) && other.IsAlive)
                {
                    system.CallAiMoveInLineOfSight(other, mover);
                    if (other.IsAlive && mover.IsAlive)
                    {
                        system.CallAiMoveInLineOfSight(mover, other);
                    }
                }
            }
        }
    }

    /// <summary>
    /// The living units within <paramref name="radius"/> (2D) of <paramref name="center"/>, each once, snapshotted into the reused
    /// scratch list so the AI reactions may change the map while the caller iterates.
    /// </summary>
    private List<WorldObject> Collect(WorldObject center, float radius)
    {
        _candidates.Clear();
        _seen.Clear();
        system.Map.Grids.CollectObjects(center.X, center.Y, radius, _candidates);
        // In-place compaction in order (what RemoveAll did, without its closure and predicate per notify).
        int kept = 0;
        for (int i = 0; i < _candidates.Count; i++)
        {
            WorldObject o = _candidates[i];
            if (o is (Player or Creature) && ReferenceEquals(o.Map, system.Map) && WithinRadius(center, o, radius) && _seen.Add(o))
            {
                _candidates[kept++] = o;
            }
        }

        _candidates.RemoveRange(kept, _candidates.Count - kept);
        _seen.Clear();
        return _candidates;
    }

    private static bool WithinRadius(WorldObject a, WorldObject b, float radius)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        return (dx * dx) + (dy * dy) <= radius * radius;
    }
}
