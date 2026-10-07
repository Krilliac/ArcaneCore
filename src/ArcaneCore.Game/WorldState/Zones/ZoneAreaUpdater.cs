using System.Runtime.ExceptionServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData;

namespace ArcaneCore.Game.WorldState.Zones;

/// <summary>
/// Server-side zone and area tracking for the players of one map: the 1 s zone timer of
/// vmangos <c>Player::Update</c> (Player.cpp:1215-1236), <c>UpdateZone</c> (:6586-6675) and
/// <c>UpdateArea</c> (:6560-6584), reduced to what the core owns. Everything else the vmangos
/// functions do (PvP-enforced area, rest type, zone-limited items, channels, auras) belongs to
/// other systems and reaches this one through <see cref="IPlayerLocationListener"/>.
/// <para>
/// The zone cached here is independent of the persisted <see cref="Player.ZoneId"/>: a character
/// loaded in Elwynn still gets its login zone-entry events, and a far teleport (which sets
/// <c>ZoneId</c> before the map add) is a fresh state in the new map's updater, so the entry
/// events fire again, as <c>SendInitialPacketsAfterAddToMap</c> does in vmangos. World thread.
/// </para>
/// </summary>
[DefaultMapUpdater(Order = 10)]
public sealed class ZoneAreaUpdater : IMapUpdater
{
    /// <summary>vmangos <c>ZONE_UPDATE_INTERVAL</c> (Player.cpp:88): 1 second.</summary>
    public const uint ZoneUpdateIntervalMs = 1000;

    private readonly WorldRuntime _world;
    private readonly Dictionary<Player, State> _states = [];

    internal ZoneAreaUpdater(Map map, WorldRuntime world)
    {
        _ = map;
        _world = world;
    }

    private sealed class State
    {
        /// <summary>vmangos m_zoneUpdateTimer: 0 until the first successful UpdateZone.</summary>
        public uint Timer;
        public uint Zone;
        public uint Area;

        // The explore trigger (vmangos Player::SetPosition -> m_areaCheckTimer / CheckAreaExploreAndOutdoor).
        public bool ExploreSeen;
        public float LastX;
        public float LastY;
        public float LastZ;
        public uint AreaCheckTimer;
    }

    private WorldStateHooks Hooks => WorldStateHooks.For(_world);

    /// <summary>The zone the tracker last accepted for <paramref name="player"/> (vmangos m_zoneUpdateId); 0 before the first update.</summary>
    public uint GetZone(Player player) => _states.TryGetValue(player, out State? s) ? s.Zone : 0;

    /// <summary>The area the tracker last accepted (vmangos m_areaUpdateId).</summary>
    public uint GetArea(Player player) => _states.TryGetValue(player, out State? s) ? s.Area : 0;

    /// <summary>
    /// Re-derive the zone and area now and run the zone update (vmangos
    /// <c>SendInitialPacketsAfterAddToMap</c> and <c>HandleZoneUpdateOpcode</c> both call
    /// <c>UpdateZone</c> directly). Returns false when the position resolves to no known zone (vmangos
    /// returns without changing anything).
    /// </summary>
    public bool ForceUpdate(Player player)
    {
        (uint zone, uint area, bool stored) = Resolve(player, GetState(player));
        return UpdateZone(player, GetState(player), zone, area, stored);
    }

    /// <summary>
    /// The player was moved inside this map by a teleport (vmangos <c>Unit::TeleportPositionRelocation</c>, Unit.cpp:9865-9873): the zone
    /// update runs at once when the zone changed, otherwise the area update when only the area changed. A player the tracker has not
    /// updated yet gets its first zone update.
    /// </summary>
    public void OnRelocated(Player player)
    {
        State state = GetState(player);
        (uint zone, uint area, bool stored) = Resolve(player, state);
        if (state.Timer == 0 || state.Zone != zone)
        {
            UpdateZone(player, state, zone, area, stored);
        }
        else if (state.Area != area)
        {
            UpdateArea(player, state, area);
        }
    }

    public void Update(Map map, uint diffMs)
    {
        foreach (Player player in map.Players)
        {
            State state = GetState(player);
            CheckExplore(player, state, diffMs);
            if (state.Timer == 0)
            {
                // vmangos runs the first UpdateZone from SendInitialPacketsAfterAddToMap; a player that
                // reached the map without it (tests, other entry paths) gets it on its first tick.
                (uint z, uint a, bool storedZone) = Resolve(player, state);
                UpdateZone(player, state, z, a, storedZone);
                continue;
            }

            if (diffMs < state.Timer)
            {
                state.Timer -= diffMs;
                continue;
            }

            (uint newZone, uint newArea, bool newStored) = Resolve(player, state);
            if (state.Zone != newZone)
            {
                UpdateZone(player, state, newZone, newArea, newStored);
            }
            else
            {
                if (state.Area != newArea)
                {
                    UpdateArea(player, state, newArea);
                }

                state.Timer = ZoneUpdateIntervalMs;
            }
        }
    }

    // vmangos Player::SetPosition (Player.cpp:5969-5985): a position change runs the explore check at once, or
    // arms m_areaCheckTimer when Movement.RelocationVmapsCheckDelay is set; the first sight of a player (login,
    // teleport: the map add is a "teleport" there) counts as a change.
    private void CheckExplore(Player player, State state, uint diffMs)
    {
        WorldStateHooks hooks = Hooks;
        if (hooks.Explorer is not { } explorer)
        {
            return;
        }

        uint delay = Math.Min(hooks.Zones.RelocationCheckDelayMs, 2000u);
        bool moved = !state.ExploreSeen || state.LastX != player.X || state.LastY != player.Y || state.LastZ != player.Z;
        if (moved)
        {
            state.ExploreSeen = true;
            (state.LastX, state.LastY, state.LastZ) = (player.X, player.Y, player.Z);
            if (delay == 0)
            {
                explorer.CheckAreaExplore(player);
                return;
            }

            if (state.AreaCheckTimer == 0)
            {
                state.AreaCheckTimer = delay;
            }
        }

        if (state.AreaCheckTimer > 0)
        {
            if (diffMs >= state.AreaCheckTimer)
            {
                state.AreaCheckTimer = 0;
                explorer.CheckAreaExplore(player);
            }
            else
            {
                state.AreaCheckTimer -= diffMs;
            }
        }
    }

    public void OnPlayerRemoved(Map map, Player player) => _states.Remove(player);

    private State GetState(Player player)
    {
        if (!_states.TryGetValue(player, out State? state))
        {
            _states[player] = state = new State();
        }

        return state;
    }

    // The zone to act on. Client / stored zone mode has no area information (area stays 0). A
    // position that resolves to no zone before any zone was accepted falls back to the stored zone
    // (a development allowance: terrain for the start tile may not be extracted); the third value
    // says the zone is not backed by an area entry.
    private (uint Zone, uint Area, bool Stored) Resolve(Player player, State state)
    {
        WorldStateHooks hooks = Hooks;
        if (hooks.UsesClientZone || player.Map is not { } map)
        {
            return (player.ZoneId, 0, true);
        }

        (uint zone, uint area) = hooks.Locator.Locate(map, player);
        return zone == 0 && state.Zone == 0 && player.ZoneId != 0 ? (player.ZoneId, 0, true) : (zone, area, false);
    }

    private bool UpdateZone(Player player, State state, uint newZone, uint newArea, bool stored)
    {
        WorldStateHooks hooks = Hooks;
        AreaTemplate? zoneEntry = hooks.Locator.Find(newZone);
        // vmangos: "if (!zoneEntry) return;" — nothing changes, the timer is not reset. A zone that
        // came from the client / stored value is accepted without an entry.
        if (newZone == 0 || (zoneEntry is null && !stored))
        {
            return false;
        }

        uint oldZone = state.Zone;
        uint oldArea = state.Area;
        Exception? failure = null;
        bool zoneChanged = oldZone != newZone;
        if (zoneChanged)
        {
            player.ZoneId = newZone;
            foreach (IPlayerLocationListener listener in hooks.LocationListeners.ToArray())
            {
                Invoke(() => listener.OnZoneChanged(player, oldZone, newZone, newArea, zoneEntry), ref failure);
            }
        }

        state.Zone = newZone;
        state.Timer = ZoneUpdateIntervalMs;
        UpdateArea(player, state, newArea, oldArea, ref failure);
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return true;
    }

    private void UpdateArea(Player player, State state, uint newArea)
    {
        Exception? failure = null;
        UpdateArea(player, state, newArea, state.Area, ref failure);
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private void UpdateArea(Player player, State state, uint newArea, uint oldArea, ref Exception? failure)
    {
        state.Area = newArea;
        foreach (IPlayerLocationListener listener in Hooks.LocationListeners.ToArray())
        {
            Invoke(() => listener.OnAreaChanged(player, oldArea, newArea), ref failure);
        }
    }

    // One failing listener must not stop the others; the first failure is rethrown afterwards
    // (Map.Update logs a throwing updater).
    private static void Invoke(Action call, ref Exception? failure)
    {
        try
        {
            call();
        }
        catch (Exception ex)
        {
            failure ??= ex;
        }
    }
}
