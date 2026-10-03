using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// vmangos event-driven proximity aggro (<see cref="AggroScanMode.Relocation"/>). A player or creature that moves, or joins
/// the map, schedules one AI notify after <see cref="CreatureOptions.AiRelocationNotifyDelayMs"/> unless one is already
/// pending (Unit::OnRelocated, Unit::ScheduleAINotify, Objects/Unit.cpp:10082-10160). When it runs, a player notifies the
/// creatures around it and a creature notifies the players around it, within
/// <see cref="CreatureOptions.MaxCreatureAttackRadius"/> times the aggro rate (RelocationNotifyEvent::Execute,
/// Unit.cpp:10089-10099; PlayerRelocationNotifier / CreatureRelocationNotifier, Maps/GridNotifiersImpl.h:57-119). Each
/// notified creature runs <c>MoveInLineOfSight</c> for the mover when it is alive, in control and not evading
/// (CallAIMoveLOS, GridNotifiersImpl.h:57-69). A unit that stands still triggers nothing.
/// <para>
/// Differences from vmangos, listed in docs/areas/creature-ai.md: the search is a plain 2D radius instead of the grid cells
/// around the mover; stealth and detection are not modelled; creature-versus-creature notifies are not run (creatures do
/// not aggro on creatures yet).
/// </para>
/// </summary>
internal sealed class AiRelocationNotifier(CreatureMapSystem system, CreatureOptions options)
{
    private readonly Dictionary<WorldObject, long> _due = new(ReferenceEqualityComparer.Instance);
    private readonly List<WorldObject> _order = [];

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

        WorldObject[] due = [.. _order.Where(o => _due[o] <= nowMs)];
        foreach (WorldObject obj in due)
        {
            _due.Remove(obj);
            _order.Remove(obj);
        }

        foreach (WorldObject obj in due)
        {
            Notify(obj);
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

            foreach (Creature creature in system.Creatures.ToArray())
            {
                if (creature.IsAlive && WithinRadius(creature, player, radius))
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

            foreach (Player nearby in system.Map.Players.ToArray())
            {
                if (nearby.IsAlive && WithinRadius(mover, nearby, radius))
                {
                    system.CallAiMoveInLineOfSight(mover, nearby);
                }
            }
        }
    }

    private static bool WithinRadius(WorldObject a, WorldObject b, float radius)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        return (dx * dx) + (dy * dy) <= radius * radius;
    }
}
