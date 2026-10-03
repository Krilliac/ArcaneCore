using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Stealth;

/// <summary>
/// vmangos Player::HandleStealthedUnitsDetection (Player.cpp:22007-22052), driven by the player update timer
/// (Player.cpp:272, 1141-1151: first run after 1000 ms, then every 2000 ms): every player re-evaluates, in detect mode, each stealthed
/// unit of the map (the distance limit is part of the detection formula), so a unit appears when it comes into detection distance
/// and disappears when it leaves it. The updater does no work, not even timer upkeep, while no unit is stealthed (the timers restart at the next stealth, so the phase of the 2000 ms cadence is not retail-exact).
/// </summary>
public sealed class StealthDetectionUpdater : IMapUpdater
{
    /// <summary>First detection pass after a player is first seen (vmangos m_detectInvisibilityTimer = 1 s).</summary>
    public const int FirstPassMs = 1000;

    private readonly StealthRegistry _registry;
    private readonly Func<Unit, bool> _isGone;
    private readonly int _periodMs;
    private readonly Dictionary<Player, int> _timers = new(ReferenceEqualityComparer.Instance);

    /// <param name="registry">The stealthed units.</param>
    /// <param name="isGone">A stealthed unit that is not in the world and not in transit: its aura was dropped without a handler.</param>
    /// <param name="periodMs">The pass period (vmangos 2000 ms).</param>
    public StealthDetectionUpdater(StealthRegistry registry, Func<Unit, bool> isGone, int periodMs = 2000)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _isGone = isGone ?? throw new ArgumentNullException(nameof(isGone));
        _periodMs = periodMs;
    }

    /// <summary>Detection passes run so far (diagnostics and tests).</summary>
    public int PassesRun { get; private set; }

    public void Update(Map map, uint diffMs)
    {
        ArgumentNullException.ThrowIfNull(map);
        _registry.Prune(_isGone);
        if (_registry.Count == 0)
        {
            // Nothing to detect: no per-player work at all. The timers restart (first pass after 1000 ms) when a unit stealths.
            _timers.Clear();
            return;
        }

        foreach (Player player in map.Players.ToArray())
        {
            if (!_timers.TryGetValue(player, out int timer))
            {
                timer = FirstPassMs;
            }

            if (diffMs >= (uint)timer)
            {
                Pass(map, player);
                _timers[player] = _periodMs;
            }
            else
            {
                _timers[player] = timer - (int)diffMs;
            }
        }
    }

    public void OnPlayerRemoved(Map map, Player player) => _timers.Remove(player);

    private void Pass(Map map, Player player)
    {
        PassesRun++;
        foreach (Unit unit in _registry.Stealthed())
        {
            if (ReferenceEquals(unit, player) || !ReferenceEquals(unit.Map, map))
            {
                continue;
            }

            // vmangos visits whole cells around the player (Cell::VisitAllObjects) and AnyStealthedCheck has no distance test, so units
            // past the maximum detect range are evaluated too: CanDetectStealthOf says no and a unit already in the list is dropped.
            // The visibility range check inside the update ignores units too far away to matter.
            map.UpdateVisibilityWithDetection(player, unit);
        }
    }
}
