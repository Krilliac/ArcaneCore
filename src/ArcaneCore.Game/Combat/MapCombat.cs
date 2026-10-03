using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Protocol;
using MapGrid = ArcaneCore.Game.Maps.Grid.Grid;

namespace ArcaneCore.Game.Combat;

/// <summary>
/// Combat for one map (world thread only): auto attack, the swing loop, damage, death and
/// spirit release, corpses, threat, the combat timer, PvP flag and regeneration. It is an
/// <see cref="IMapUpdater"/> that every map gets (<see cref="DefaultMapUpdaterAttribute"/>,
/// first in order), so it runs in <see cref="Map.Update"/> after the in-world packets and
/// logouts and before the visibility/values/flush phases, and every field it changes goes out
/// in the same tick. Reach it with <c>map.Combat</c> (<see cref="MapCombatExtensions"/>).
/// <para>
/// Players are always updated; other units (creatures) are updated once they take part in
/// combat (<see cref="Track"/>), until they are idle again. Method names and logic follow
/// vmangos Unit/Player (see docs/areas/combat.md).
/// </para>
/// </summary>
[DefaultMapUpdater(Order = 0)]
public sealed partial class MapCombat : IMapUpdater
{
    private readonly Map _map;
    private readonly WorldRuntime _world;
    private readonly Action<Player> _onPlayerLoggingOut;
    private readonly HashSet<Unit> _units = [];
    private readonly List<Corpse> _corpses = [];
    private readonly Dictionary<Corpse, MapGrid> _corpseGrids = [];
    private CombatHooks? _hooks;
    private long _elapsedMs;

    internal MapCombat(Map map, WorldRuntime world)
    {
        _map = map;
        _world = world;
        map.Grids.GridUnloading += grid =>
        {
            foreach (Corpse corpse in _corpses.Where(c => ReferenceEquals(_corpseGrids.GetValueOrDefault(c), grid)).ToArray())
            {
                if (world.FindOnlinePlayer(corpse.Owner) is { } owner && ReferenceEquals(owner.Combat.Corpse, corpse))
                {
                    owner.Combat.Corpse = null;
                }

                RemoveCorpse(corpse);
            }
        };
        _onPlayerLoggingOut = OnPlayerLoggingOut;
        world.PlayerLoggingOut += _onPlayerLoggingOut;
        world.MapUnloading += OnMapUnloading;
    }

    private void OnPlayerLoggingOut(Player player)
    {
        if (ReferenceEquals(player.Map, _map) || ReferenceEquals(player.Combat.Corpse?.Map, _map))
        {
            OnPlayerLeaving(player);
        }
    }

    private void OnMapUnloading(Map map)
    {
        if (ReferenceEquals(map, _map))
        {
            _world.PlayerLoggingOut -= _onPlayerLoggingOut;
            _world.MapUnloading -= OnMapUnloading;
        }
    }

    /// <summary>The seams into other areas; defaults to the world's registered hooks (<see cref="CombatHooks.Register"/>).</summary>
    public CombatHooks Hooks
    {
        get => _hooks ?? CombatHooks.For(_world);
        set => _hooks = value;
    }

    public ICombatRandom Random { get; set; } = SharedCombatRandom.Instance;

    /// <summary>One authoritative death, after its state transition (world thread). Objective adapters subscribe without replacing combat hooks.</summary>
    public event Action<Unit?, Unit>? UnitKilled;

    /// <summary>Corpses currently in this map.</summary>
    public IReadOnlyList<Corpse> Corpses => _corpses;

    /// <summary>Non-player units currently updated by combat.</summary>
    public IReadOnlyCollection<Unit> TrackedUnits => _units;

    /// <summary>Combat time of this map in seconds (drives the recent-death window).</summary>
    internal long NowSeconds => _elapsedMs / 1000;

    /// <summary>Start updating a non-player unit (swing timers, regeneration, combat timer).</summary>
    public void Track(Unit unit)
    {
        if (unit is not Player && ReferenceEquals(unit.Map, _map))
        {
            _units.Add(unit);
            unit.Combat.Tracker = this;
        }
    }

    /// <summary>A unit in this map by GUID: players, tracked units, then <see cref="CombatHooks.FindUnit"/>.</summary>
    public Unit? FindUnit(ObjectGuid guid)
    {
        if (_map.FindObject(guid) is Unit registered)
        {
            return registered;
        }

        if (_map.FindPlayer(guid) is { } player)
        {
            return player;
        }

        foreach (Unit unit in _units)
        {
            if (unit.Guid == guid)
            {
                return unit;
            }
        }

        return Hooks.FindUnit(_map, guid);
    }

    /// <inheritdoc/>
    void IMapUpdater.Update(Map map, uint diffMs) => Update(diffMs);

    /// <summary>
    /// Map departure detaches fights but keeps the body reclaimable across a far teleport.
    /// Actual logout removes the corpse through <see cref="WorldRuntime.PlayerLoggingOut"/>.
    /// </summary>
    void IMapUpdater.OnPlayerRemoved(Map map, Player player) => DetachRelations(player);

    /// <summary>One combat step (run by <see cref="Map.Update"/> through <see cref="IMapUpdater"/>).</summary>
    public void Update(uint diffMs)
    {
        _elapsedMs += diffMs;

        foreach (Player player in _map.Players.ToArray())
        {
            if (ReferenceEquals(player.Map, _map))
            {
                UpdateUnit(player, diffMs);
            }
        }

        foreach (Unit unit in _units.ToArray())
        {
            if (!ReferenceEquals(unit.Map, _map))
            {
                Forget(unit);
                continue;
            }

            UpdateUnit(unit, diffMs);
            if (IsIdle(unit))
            {
                _units.Remove(unit);
            }
        }

    }

    private void UpdateUnit(Unit unit, uint diff)
    {
        if (IsQuestSettlementPending(unit))
        {
            return;
        }

        UnitCombat c = unit.Combat;
        c.Tracker = this;

        // vmangos Unit::Update: five-second rule, combat timer, swing timers.
        c.LastManaUseTimer = c.LastManaUseTimer > diff ? c.LastManaUseTimer - diff : 0;
        UpdateCombatTimer(unit, diff);
        c.TickAttackTimers(diff);

        if (unit is Player player)
        {
            // vmangos Player::Update order: melee, PvP flag, regeneration, KillPlayer, auto release.
            if (c.IsMeleeAttacking)
            {
                UpdateMeleeAttackingState(unit);
            }

            UpdatePvpFlagTimer(player, diff);
            UpdatePlayerRegen(player, diff);

            if (c.DeathState == DeathState.JustDied)
            {
                KillPlayer(player);
            }

            if (c.DeathState == DeathState.Corpse && !Hooks.IsInstanceable(player.MapId))
            {
                if (diff >= c.DeathTimer)
                {
                    RepopPlayer(player);
                }
                else
                {
                    c.DeathTimer -= diff;
                }
            }
        }
        else
        {
            if (c.IsMeleeAttacking)
            {
                UpdateMeleeAttackingState(unit);
            }

            UpdateCreatureRegen(unit, diff);
        }
    }

    private static bool IsIdle(Unit unit)
    {
        UnitCombat c = unit.Combat;
        if (c.Victim is not null || c.IsInCombat || c.AttackersInternal.Count > 0 || c.HasThreatList)
        {
            return false;
        }

        // keep regenerating until full (vmangos Creature::RegenerateAll)
        return !IsAliveState(unit) || unit.Health >= unit.MaxHealth;
    }

    /// <summary>Drop every combat relation of a unit that left the map.</summary>
    private void Forget(Unit unit)
    {
        _units.Remove(unit);
        UnitCombat c = unit.Combat;
        DetachRelations(unit);
        c.Tracker = null;
    }

    /// <summary>Detach a despawning unit before the map removes its object and observer ownership.</summary>
    public void Untrack(Unit unit) => Forget(unit);

    /// <summary>
    /// A player is leaving the world (logout or disconnect; vmangos WorldSession::LogoutPlayer →
    /// CombatStop / RemoveFromWorld): stop its fights, drop it from every threat list and take
    /// its corpse out of the map (corpses are not persisted yet).
    /// </summary>
    internal void OnPlayerLeaving(Player player)
    {
        DetachRelations(player);
        if (player.Combat.Corpse is { } corpse)
        {
            RemoveCorpse(corpse);
            player.Combat.Corpse = null;
        }
    }

    private void DetachRelations(Unit unit)
    {
        UnitCombat c = unit.Combat;
        AttackStop(unit);
        foreach (Unit attacker in c.AttackersInternal.ToArray())
        {
            AttackStop(attacker);
        }

        c.AttackersInternal.Clear();
        foreach (Unit holder in c.ThreatenedByInternal.ToArray())
        {
            holder.Combat.Threat.Remove(unit);
        }

        if (c.HasThreatList)
        {
            c.Threat.Clear();
        }

        ClearInCombat(unit);
    }

    // --- helpers ------------------------------------------------------------------

    private static bool IsQuestSettlementPending(Unit? unit)
        => unit is Player { IsQuestSettlementPending: true };

    /// <summary>Alive in the death-state sense (vmangos Unit::IsAlive: m_deathState == ALIVE).</summary>
    internal static bool IsAliveState(Unit unit) => unit.Combat.DeathState == DeathState.Alive;

    /// <summary>vmangos Unit::IsStandingUp: standing or the "dead" stand state.</summary>
    internal static bool IsStandingUp(Unit unit) => unit.StandState is StandState.Stand or StandState.Dead;

    internal static uint GetPower(Unit unit, PowerType power) => unit.GetUInt32(UpdateFields.UnitFieldPower1 + (int)power);

    internal static uint GetMaxPower(Unit unit, PowerType power) => unit.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)power);

    /// <summary>vmangos Unit::SetPower: capped at the maximum.</summary>
    internal static void SetPower(Unit unit, PowerType power, uint value)
    {
        uint max = GetMaxPower(unit, power);
        unit.SetUInt32(UpdateFields.UnitFieldPower1 + (int)power, Math.Min(value, max));
    }

    /// <summary>vmangos Unit::ModifyPower.</summary>
    internal static void ModifyPower(Unit unit, PowerType power, int delta)
    {
        long value = (long)GetPower(unit, power) + delta;
        SetPower(unit, power, (uint)Math.Clamp(value, 0, uint.MaxValue));
    }

    /// <summary>vmangos WorldObject::HasInArc: is <paramref name="target"/> within <paramref name="arc"/> centred on the facing?</summary>
    public static bool HasInArc(WorldObject source, WorldObject target, float arc)
    {
        if (ReferenceEquals(source, target))
        {
            return true;
        }

        float angle = MathF.Atan2(target.Y - source.Y, target.X - source.X);
        angle -= source.Orientation;
        angle = NormalizeOrientation(angle);
        if (angle > MathF.PI)
        {
            angle -= 2.0f * MathF.PI;
        }

        float border = arc / 2.0f;
        return angle >= -border && angle <= border;
    }

    private static float NormalizeOrientation(float o)
    {
        float twoPi = 2.0f * MathF.PI;
        o %= twoPi;
        return o < 0 ? o + twoPi : o;
    }

    /// <summary>vmangos rand_dither: round down or up with probability equal to the fraction.</summary>
    private uint Dither(float value)
    {
        if (value <= 0f)
        {
            return 0;
        }

        uint whole = (uint)value;
        float fraction = value - whole;
        return fraction > 0f && Random.NextFloat(0f, 1f) < fraction ? whole + 1 : whole;
    }

    // --- corpses in the map -----------------------------------------------------------

    private void AddCorpse(Corpse corpse)
    {
        _map.AddObject(corpse, isNewObject: true);
        _corpses.Add(corpse);
        Maps.Grid.CellCoord cell = _map.Grids.CellOf(corpse)!.Value;
        MapGrid grid = _map.Grids.GetGrid(cell.Grid)!;
        // The body must remain reclaimable after the ghost leaves its grid. Until corpse
        // persistence exists, hold the grid's existing unload lock while its owner is online.
        grid.IncrementUnloadActiveLock();
        _corpseGrids.Add(corpse, grid);
    }

    private void RemoveCorpse(Corpse corpse)
    {
        if (corpse.Map is { } ownerMap && !ReferenceEquals(ownerMap, _map))
        {
            ownerMap.Combat.RemoveCorpse(corpse);
            return;
        }

        if (!_corpses.Remove(corpse))
        {
            return;
        }

        if (_corpseGrids.Remove(corpse, out MapGrid? grid))
        {
            grid.DecrementUnloadActiveLock();
        }

        _map.RemoveObject(corpse);
    }
}
