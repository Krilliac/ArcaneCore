using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;
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
    private readonly Action<Map> _onMapCreated;
    private readonly HashSet<Unit> _units = [];
    private readonly List<Unit> _unitUpdateScratch = [];
    private bool _unitUpdateScratchInUse;
    private readonly List<Unit> _staleAttackersScratch = [];
    private readonly List<(Unit Holder, bool HasEntry)> _staleThreatHoldersScratch = [];
    private readonly List<Corpse> _corpses = [];
    private readonly Dictionary<Corpse, MapGrid> _corpseGrids = [];
    private CombatHooks? _hooks;
    private CombatEnvironment? _activeEnvironment;

    private CombatEnvironment Environment => _activeEnvironment ?? CombatEnvironment.For(_world);

    internal MapCombat(Map map, WorldRuntime world)
    {
        _map = map;
        _world = world;
        map.Grids.GridUnloading += grid =>
        {
            // vmangos keeps every corpse in ObjectAccessor, outside the grids: an unloading grid (an instance map that unloads)
            // takes the body out of the map, and its online owner keeps it with its map and instance id. The next map of that
            // instance puts it back (AdoptBodiesLeftOutside).
            foreach (Corpse corpse in _corpses.Where(c => ReferenceEquals(_corpseGrids.GetValueOrDefault(c), grid)).ToArray())
            {
                RemoveCorpse(corpse);
            }
        };
        _onPlayerLoggingOut = OnPlayerLoggingOut;
        world.PlayerLoggingOut += _onPlayerLoggingOut;
        world.MapUnloading += OnMapUnloading;
        _onMapCreated = OnMapCreated;
        world.MapCreated += _onMapCreated;
    }

    private void OnMapCreated(Map map)
    {
        if (!ReferenceEquals(map, _map))
        {
            return;
        }

        _world.MapCreated -= _onMapCreated;
        AdoptBodiesLeftOutside();
    }

    /// <summary>
    /// vmangos ObjectAccessor::AddCorpsesToGrid (ObjectAccessor.cpp:225-245, run when a grid of the map loads): the bodies of online
    /// ghosts that lie on this map and, for an instanceable map, in this instance, and that are in no map now (their map unloaded), go
    /// into this new map. A body whose instance was deleted points at instance 0 and never matches a dungeon map again.
    /// </summary>
    private void AdoptBodiesLeftOutside()
    {
        foreach (Player owner in _world.OnlinePlayers.ToArray())
        {
            if (owner.Combat.Corpse is { Map: null } body && body.MapId == _map.MapId && body.InstanceId == _map.InstanceId
                && !_corpses.Contains(body))
            {
                AddCorpse(body);
            }
        }
    }

    private void OnPlayerLoggingOut(Player player)
    {
        if (ReferenceEquals(player.Map, _map) || _map.IsInTransit(player) || ReferenceEquals(player.Combat.Corpse?.Map, _map))
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
            _world.MapCreated -= _onMapCreated;
        }
    }

    /// <summary>The seams into other areas; defaults to the world's registered hooks (<see cref="CombatHooks.Register"/>).</summary>
    public CombatHooks Hooks
    {
        get => _hooks ?? CombatHooks.For(_world);
        set => _hooks = value;
    }

    /// <summary>
    /// The equipment and ability answers of the player stat system (docs/areas/stats.md). Null
    /// keeps the <see cref="Hooks"/> defaults, which is what a map without the stats feature uses.
    /// </summary>
    public ICombatStatSource? Stats { get; set; }

    /// <summary>vmangos Unit::HaveOffhandWeapon: the stat source for players, otherwise the hooks.</summary>
    internal bool HasOffhandWeapon(Unit unit) => Stats?.HasOffhandWeapon(unit) ?? Hooks.HasOffhandWeapon(unit);

    /// <summary>Whether a player can parry now (vmangos GetUnitParryChance: CanParry() and a weapon to parry with).</summary>
    internal bool PlayerCanParry(Player player) => Stats?.PlayerCanParry(player) ?? Hooks.PlayerCanParry(player);

    /// <summary>Whether a player can block now (vmangos GetUnitBlockChance: ability, usable off-hand, intact shield).</summary>
    internal bool PlayerCanBlock(Player player) => Stats?.PlayerCanBlock(player) ?? Hooks.PlayerCanBlock(player);

    /// <summary>vmangos Unit::GetShieldBlockValue: the stat source for players, otherwise the hooks.</summary>
    internal uint ShieldBlockValue(Unit unit) => Stats?.ShieldBlockValue(unit) ?? Hooks.GetShieldBlockValue(unit);

    /// <summary>
    /// vmangos SpellCaster::GetWeaponSkillValue. A player's off-hand skill is 0 without an off-hand weapon;
    /// the hooks only learn about the weapon through <see cref="CombatHooks.HasOffhandWeapon"/>, so a stat
    /// source that sees one only supplies the level maximum before skills are attached. Once skills exist,
    /// the weapon's actual proficiency and bonuses determine every hit-table range (vmangos
    /// SpellCaster::GetWeaponSkillValue, SpellCaster.cpp:116-141).
    /// </summary>
    internal int WeaponSkill(Unit unit, WeaponAttackType attackType, Unit? victim)
        => unit is Player { Skills: null } && attackType == WeaponAttackType.OffAttack && Stats?.HasOffhandWeapon(unit) == true
            ? MeleeHitTable.SkillMaxForLevel(unit, victim)
            : Hooks.GetWeaponSkill(unit, attackType, victim);

    public ICombatRandom Random { get; set; } = SharedCombatRandom.Instance;

    /// <summary>One authoritative death, after its state transition (world thread). Objective adapters subscribe without replacing combat hooks.</summary>
    public event Action<Unit?, Unit>? UnitKilled;

    /// <summary>Corpses currently in this map.</summary>
    public IReadOnlyList<Corpse> Corpses => _corpses;

    /// <summary>Non-player units currently updated by combat.</summary>
    public IReadOnlyCollection<Unit> TrackedUnits => _units;

    /// <summary>
    /// Unix seconds from the world's <see cref="Death.DeathClock"/> (vmangos <c>time(nullptr)</c>):
    /// the recent-death window and the ghost time are wall-clock timestamps shared by every map,
    /// not this map's uptime, so they stay consistent when a ghost changes map or logs out.
    /// </summary>
    internal long NowSeconds => Death.DeathHooks.For(_world).Clock.UnixSeconds;

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
        CombatEnvironment? previousEnvironment = _activeEnvironment;
        _activeEnvironment = CombatEnvironment.For(_world);
        try
        {
            RemoveExpiredBones();
            UpdateHeldSwings(); // the swings held for the spell batch resolve at its boundary (MapCombat.SwingBatch.cs)
            Player[] players = _map.RentPlayerSnapshot(out int playerCount);
            try
            {
                for (int i = 0; i < playerCount; i++)
                {
                    Player player = players[i];
                    try
                    {
                        if (player is not null && player.IsInWorld && player.Map is not null && ReferenceEquals(player.Map, _map))
                        {
                            UpdateUnit(player, diffMs);
                        }
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        FaultLedger.Shared.Record("map.combat", ex);
                    }
                }
            }
            finally
            {
                Map.ReturnPlayerSnapshot(players);
            }

            if (_units.Count > 0)
            {
                bool reentrant = _unitUpdateScratchInUse;
                IReadOnlyList<Unit> units;
                if (reentrant)
                {
                    units = _units.ToArray();
                }
                else
                {
                    _unitUpdateScratchInUse = true;
                    _unitUpdateScratch.Clear();
                    foreach (Unit tracked in _units)
                    {
                        _unitUpdateScratch.Add(tracked);
                    }

                    units = _unitUpdateScratch;
                }

                try
                {
                    for (int i = 0; i < units.Count; i++)
                    {
                        Unit unit = units[i];
                        try
                        {
                            if (unit is null || !unit.IsInWorld || unit.Map is null || !ReferenceEquals(unit.Map, _map))
                            {
                                if (unit is not null)
                                {
                                    Forget(unit);
                                }

                                continue;
                            }

                            UpdateUnit(unit, diffMs);
                            if (IsIdle(unit))
                            {
                                _units.Remove(unit);
                            }
                        }
                        catch (Exception ex) when (ex is not OutOfMemoryException)
                        {
                            FaultLedger.Shared.Record("map.combat", ex);
                        }
                    }
                }
                finally
                {
                    if (!reentrant)
                    {
                        _unitUpdateScratch.Clear();
                        _unitUpdateScratchInUse = false;
                    }
                }
            }

            try
            {
                SweepIfDue(diffMs);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("map.combat", ex);
            }
        }
        finally
        {
            _activeEnvironment = previousEnvironment;
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
        // vmangos m_doExtraAttacks: a queue created during this update becomes eligible only
        // at the beginning of the next Unit::Update pass, while its count remains visible to
        // item-proc recursion guards immediately.
        c.MarkExtraAttacksReady();

        // vmangos Unit::Update: five-second rule, combat timer, swing timers.
        // The timer does not run out while the unit still channels the spell that took the mana (vmangos
        // Unit.cpp:235-253, patch 1.7 "mana was being regenerated while channelling spells that use mana").
        if (c.LastManaUseTimer != 0)
        {
            if (diff < c.LastManaUseTimer)
            {
                c.LastManaUseTimer -= diff;
            }
            else if (c.LastManaUseSpellId == 0 || unit.GetUInt32(UpdateFields.UnitChannelSpell) != c.LastManaUseSpellId)
            {
                c.LastManaUseTimer = 0;
                c.LastManaUseSpellId = 0;
            }
        }

        VerifyUnitCombatInvariants(unit);
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
            UpdatePvpContestedFlagTimer(player, diff);
            UpdatePlayerRegen(player, diff);

            if (c.DeathState == DeathState.JustDied)
            {
                KillPlayer(player);
            }

            if (c.DeathState == DeathState.Corpse && !IsInstanceableMap(player.MapId))
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

            if (c.RepopPending)
            {
                RunScheduledRepop(player);
            }

            VerifyPlayerLifeInvariants(player);
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

    public bool VerifyPlayerLifeInvariants(Player player)
    {
        bool healed = false;
        UnitCombat c = player.Combat;
        if (c.DeathState != DeathState.Dead && (player.Flags & PlayerFlags.Ghost) != 0)
        {
            _map.Logger.LogWarning(
                "life-state invariant healed for player {Player} (guid={Guid}) on map {MapId}: PLAYER_FLAGS_GHOST was set in DeathState.{DeathState}; clearing ghost flag",
                player.Name, player.Guid.Low, _map.MapId, c.DeathState);
            player.Flags &= ~PlayerFlags.Ghost;
            ReportHealed(player, "ghost-flag", $"PLAYER_FLAGS_GHOST was set in DeathState.{c.DeathState}; cleared");
            player.NeedsVisibilityUpdate = true;
            healed = true;
        }
        else if (c.DeathState == DeathState.Dead && (player.Flags & PlayerFlags.Ghost) == 0)
        {
            _map.Logger.LogWarning(
                "life-state invariant healed for player {Player} (guid={Guid}) on map {MapId}: DeathState.Dead was missing PLAYER_FLAGS_GHOST; restoring ghost flag",
                player.Name, player.Guid.Low, _map.MapId);
            player.Flags |= PlayerFlags.Ghost;
            ReportHealed(player, "ghost-flag", "DeathState.Dead was missing PLAYER_FLAGS_GHOST; restored");
            player.NeedsVisibilityUpdate = true;
            healed = true;
        }

        if (c.DeathState == DeathState.Dead && player.Health == 0)
        {
            _map.Logger.LogWarning(
                "life-state invariant healed for player {Player} (guid={Guid}) on map {MapId}: DeathState.Dead had Health=0; restoring ghost Health=1",
                player.Name, player.Guid.Low, _map.MapId);
            player.Health = 1;
            ReportHealed(player, "dead-health", "DeathState.Dead had Health=0; restored ghost Health=1");
            healed = true;
        }

        if (c.DeathState == DeathState.Alive && player.Health > 0)
        {
            byte fieldBytes0 = player.GetByte(UpdateFields.PlayerFieldBytes, 0);
            if ((fieldBytes0 & FieldByteReleaseTimer) != 0)
            {
                _map.Logger.LogWarning(
                    "life-state invariant healed for player {Player} (guid={Guid}) on map {MapId}: PLAYER_FIELD_BYTE_RELEASE_TIMER was set while Alive; clearing flag",
                    player.Name, player.Guid.Low, _map.MapId);
                player.SetByte(UpdateFields.PlayerFieldBytes, 0, (byte)(fieldBytes0 & ~FieldByteReleaseTimer));
                ReportHealed(player, "release-timer-flag", "PLAYER_FIELD_BYTE_RELEASE_TIMER was set while Alive; cleared");
                healed = true;
            }

            if (c.DeathTimer != 0)
            {
                _map.Logger.LogWarning(
                    "life-state invariant healed for player {Player} (guid={Guid}) on map {MapId}: DeathTimer={DeathTimer} was non-zero while Alive; clearing timer",
                    player.Name, player.Guid.Low, _map.MapId, c.DeathTimer);
                ReportHealed(player, "death-timer", $"DeathTimer={c.DeathTimer} was non-zero while Alive; cleared");
                c.DeathTimer = 0;
                healed = true;
            }
        }

        if (!float.IsFinite(player.X) || !float.IsFinite(player.Y) || !float.IsFinite(player.Z) || !float.IsFinite(player.Orientation))
        {
            float safeX = float.IsFinite(player.X) ? player.X : (player.Home.MapId == _map.MapId && float.IsFinite(player.Home.X) ? player.Home.X : 0f);
            float safeY = float.IsFinite(player.Y) ? player.Y : (player.Home.MapId == _map.MapId && float.IsFinite(player.Home.Y) ? player.Home.Y : 0f);
            float safeZ = float.IsFinite(player.Z) ? player.Z : (player.Home.MapId == _map.MapId && float.IsFinite(player.Home.Z) ? player.Home.Z : 0f);
            float safeO = float.IsFinite(player.Orientation) ? player.Orientation : 0f;
            _map.Logger.LogWarning(
                "coordinate invariant healed for player {Player} (guid={Guid}) on map {MapId}: non-finite position ({X},{Y},{Z},o={O}); relocating to ({SafeX},{SafeY},{SafeZ},o={SafeO})",
                player.Name, player.Guid.Low, _map.MapId, player.X, player.Y, player.Z, player.Orientation, safeX, safeY, safeZ, safeO);
            player.Relocate(safeX, safeY, safeZ, safeO, _world.NowMs);
            ReportHealed(player, "non-finite-position", "non-finite position relocated to the home or origin");
            healed = true;
        }

        return healed;
    }

    public bool VerifyUnitCombatInvariants(Unit unit)
    {
        bool healed = false;
        UnitCombat c = unit.Combat;
        if (c.Victim is { } victim)
        {
            if ((victim.Map is not null && !ReferenceEquals(victim.Map, _map)) || (victim is Player vp && !vp.IsInWorld))
            {
                _map.Logger.LogWarning(
                    "combat invariant healed for unit {Guid} on map {MapId}: Victim {VictimGuid} left this map (victimMap={VictimMapId}, inWorld={InWorld}); stopping attack",
                    unit.Guid, _map.MapId, victim.Guid, victim.Map?.MapId, victim.IsInWorld);
                AttackStop(unit);
                ReportHealed(unit, "victim-left-map", $"melee victim {victim.Guid.Value} left this map; attack stopped");
                healed = true;
            }
        }

        if (c.AttackersInternal.Count > 0)
        {
            _staleAttackersScratch.Clear();
            foreach (Unit attacker in c.AttackersInternal)
            {
                if (!ReferenceEquals(attacker.Combat.Victim, unit) || !IsAliveState(attacker)
                    || (attacker.Map is not null && !ReferenceEquals(attacker.Map, _map)))
                {
                    _staleAttackersScratch.Add(attacker);
                }
            }

            if (_staleAttackersScratch.Count > 0)
            {
                for (int i = 0; i < _staleAttackersScratch.Count; i++)
                {
                    Unit attacker = _staleAttackersScratch[i];
                    bool alive = IsAliveState(attacker);
                    _map.Logger.LogWarning(
                        "combat invariant healed for unit {Guid} on map {MapId}: pruning stale attacker {AttackerGuid} (alive={Alive}, matchesVictim={MatchesVictim}, attackerMap={AttackerMapId})",
                        unit.Guid, _map.MapId, attacker.Guid, alive, ReferenceEquals(attacker.Combat.Victim, unit), attacker.Map?.MapId);
                    c.AttackersInternal.Remove(attacker);
                    ReportHealed(unit, "stale-attacker", $"pruned stale attacker {attacker.Guid.Value} (alive={alive})");
                    healed = true;
                }

                _staleAttackersScratch.Clear();
            }
        }

        if (c.ThreatenedByInternal.Count > 0)
        {
            _staleThreatHoldersScratch.Clear();
            foreach (Unit holder in c.ThreatenedByInternal)
            {
                bool alive = IsAliveState(holder);
                bool sameMap = holder.Map is null || ReferenceEquals(holder.Map, _map);
                bool hasEntry = holder.Combat.HasThreatList && holder.Combat.Threat.Contains(unit);
                if (!alive || !hasEntry || !sameMap)
                {
                    _staleThreatHoldersScratch.Add((holder, hasEntry));
                }
            }

            if (_staleThreatHoldersScratch.Count > 0)
            {
                for (int i = 0; i < _staleThreatHoldersScratch.Count; i++)
                {
                    (Unit holder, bool hasEntry) = _staleThreatHoldersScratch[i];
                    bool alive = IsAliveState(holder);
                    _map.Logger.LogWarning(
                        "combat invariant healed for unit {Guid} on map {MapId}: pruning stale ThreatenedBy holder {HolderGuid} (alive={Alive}, hasEntry={HasEntry}, holderMap={HolderMapId})",
                        unit.Guid, _map.MapId, holder.Guid, alive, hasEntry, holder.Map?.MapId);
                    if (hasEntry)
                    {
                        holder.Combat.Threat.Remove(unit);
                    }
                    else
                    {
                        c.ThreatenedByInternal.Remove(holder);
                    }

                    ReportHealed(unit, "stale-threat-holder", $"pruned stale threat holder {holder.Guid.Value} (alive={alive})");
                    healed = true;
                }

                _staleThreatHoldersScratch.Clear();
            }
        }

        return healed;
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
        ForgetEpisodes(unit);
        UnitCombat c = unit.Combat;
        DetachRelations(unit);
        c.Tracker = null;
    }

    /// <summary>Detach a despawning unit before the map removes its object and observer ownership.</summary>
    public void Untrack(Unit unit) => Forget(unit);

    /// <summary>
    /// A player is leaving the world (logout or disconnect; vmangos WorldSession::LogoutPlayer →
    /// CombatStop / RemoveFromWorld): stop its fights, drop it from every threat list, release a
    /// spirit that logs out still waiting at its body ("If the player just died before logging
    /// out, make him appear as a ghost": BuildPlayerRepop + RepopAtGraveyard,
    /// WorldSession.cpp:694-701, taken when the death timer is running), and take the corpse out
    /// of the map. The body is persisted with the character (the logout snapshot is captured
    /// after this and reads <see cref="UnitCombat.Corpse"/>, which stays set for that reason) and
    /// is put back by <see cref="RestoreGhost"/> at the next login; it is not kept in the world
    /// while its owner is offline (docs/integration/death-persistence.md, limits).
    /// </summary>
    internal void OnPlayerLeaving(Player player)
    {
        DetachRelations(player);
        UnitCombat c = player.Combat;
        if (c.DeathTimer > 0 && !IsAliveState(player) && (player.Flags & PlayerFlags.Ghost) == 0)
        {
            RepopPlayer(player, immediate: true, leaving: true);
        }

        if (c.Corpse is { } corpse)
        {
            RemoveCorpse(corpse);
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
        corpse.InstanceId = _map.InstanceId;
        _map.AddObject(corpse, isNewObject: true);
        _corpses.Add(corpse);
        if (_map.Grids.CellOf(corpse) is { } cell && _map.Grids.GetGrid(cell.Grid) is { } grid)
        {
            // The body must remain reclaimable after the ghost leaves its grid. Until corpse
            // persistence exists, hold the grid's existing unload lock while its owner is online.
            grid.IncrementUnloadActiveLock();
            _corpseGrids.Add(corpse, grid);
        }
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
