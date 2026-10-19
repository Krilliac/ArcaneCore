using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Diagnostics;

namespace ArcaneCore.Game.Stealth;

/// <summary>
/// vmangos Player::HandleStealthedUnitsDetection (Player.cpp:22007-22052), driven by the player update timer
/// (Player.cpp:272, 1141-1151: first run after 1000 ms, then every 2000 ms): every player re-evaluates, in detect mode, each stealthed
/// unit of the map that the check can still change. A unit the player does not see, standing beyond
/// <see cref="StealthOptions.MaxPlayerDetectRange"/>, cannot be detected and is skipped. A unit already visible is always
/// rechecked, so it disappears when it walks out. The updater does no work, not even timer upkeep, while no unit is stealthed
/// (the timers restart at the next stealth, so the phase of the 2000 ms cadence is not retail-exact).
/// </summary>
public sealed class StealthDetectionUpdater : IMapUpdater
{
    /// <summary>First detection pass after a player is first seen (vmangos m_detectInvisibilityTimer = 1 s).</summary>
    public const int FirstPassMs = 1000;

    private readonly StealthRegistry _registry;
    private readonly Func<Unit, bool> _isGone;
    private readonly int _periodMs;
    private readonly Dictionary<Player, int> _timers = new(ReferenceEqualityComparer.Instance);
    private readonly List<Player> _players = [];
    private readonly List<Unit> _mapStealthed = [];

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
        try
        {
            _registry.Prune(_isGone);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("map-stealth", ex);
        }

        if (_registry.Count == 0)
        {
            // Nothing to detect: no per-player work at all. The timers restart (first pass after 1000 ms) when a unit stealths.
            _timers.Clear();
            return;
        }

        _mapStealthed.Clear();
        using (HashSet<Unit>.Enumerator e = _registry.EnumerateStealthed())
        {
            while (e.MoveNext())
            {
                Unit unit = e.Current;
                if (unit is not null && unit.IsInWorld && ReferenceEquals(unit.Map, map))
                {
                    _mapStealthed.Add(unit);
                }
            }
        }

        float range = (StealthServices.Find(map)?.Options ?? StealthOptions.Default).MaxPlayerDetectRange;
        float rangeSq = range * range;

        _players.Clear();
        _players.AddRange(map.Players);
        foreach (Player player in _players)
        {
            try
            {
                if (player is null || !player.IsInWorld || player.Map is null || !ReferenceEquals(player.Map, map))
                {
                    continue;
                }

                if (!_timers.TryGetValue(player, out int timer))
                {
                    timer = FirstPassMs;
                }

                if (diffMs >= (uint)timer)
                {
                    Pass(map, player, rangeSq);
                    _timers[player] = _periodMs;
                }
                else
                {
                    _timers[player] = timer - (int)diffMs;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("map-stealth", ex);
            }
        }
    }

    public void OnPlayerRemoved(Map map, Player player) => _timers.Remove(player);

    private void Pass(Map map, Player player, float rangeSq)
    {
        PassesRun++;
        // Player detectors use MaxPlayerDetectRange (StealthDetection; creature detectors use MaxCreatureDetectRange in
        // StealthServices). The distance is the 3D center distance StealthVisibilityRule feeds CanDetectStealthOf, which
        // rejects anything past the cap. A unit the player already sees is still rechecked so it drops on the way out.
        foreach (Unit unit in _mapStealthed)
        {
            try
            {
                if (!unit.IsInWorld || unit.Map is null || ReferenceEquals(unit, player) || !ReferenceEquals(unit.Map, map))
                {
                    continue;
                }

                if (!player.VisibleObjects.Contains(unit.Guid))
                {
                    float dx = player.X - unit.X;
                    float dy = player.Y - unit.Y;
                    float dz = player.Z - unit.Z;
                    if ((dx * dx) + (dy * dy) + (dz * dz) > rangeSq)
                    {
                        continue;
                    }
                }

                map.UpdateVisibilityWithDetection(player, unit);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("map-stealth", ex);
            }
        }
    }
}
