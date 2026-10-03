using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.WorldState.Weather;

/// <summary>
/// The weather of one map: vmangos <c>WeatherSystem</c> (Weather.cpp:371-399) over per-zone
/// <c>Weather</c> objects (:55-84, :212-267). Zones get a <see cref="WeatherState"/> the first time a
/// player enters them (<see cref="FindOrCreate"/>), regenerate every
/// <see cref="WeatherOptions.ChangeIntervalMs"/>, and send the new weather to every player of the
/// zone when it changed. A regeneration that changed the weather while nobody is in the zone drops
/// the zone's state, so the next visitor starts fresh (vmangos "Weather will be removed if not
/// updated (no players in zone anymore)"). Every map, including instances, has its own.
/// World thread.
/// </summary>
[DefaultMapUpdater(Order = 20)]
public sealed class MapWeather : IMapUpdater
{
    private readonly Map _map;
    private readonly WorldRuntime _world;
    private readonly Dictionary<uint, ZoneWeather> _zones = [];

    internal MapWeather(Map map, WorldRuntime world)
    {
        _map = map;
        _world = world;
    }

    private sealed class ZoneWeather(WeatherState state, uint intervalMs)
    {
        public WeatherState State { get; } = state;

        /// <summary>vmangos ShortIntervalTimer: current time and fixed interval.</summary>
        public uint Current;

        public uint Interval { get; } = intervalMs;
    }

    private WorldStateHooks Hooks => WorldStateHooks.For(_world);

    /// <summary>The zones that currently have weather state (for tests and tooling).</summary>
    public IReadOnlyCollection<uint> ActiveZones => _zones.Keys;

    /// <summary>The state of a zone, or null when the zone has none yet.</summary>
    public WeatherState? Find(uint zone) => _zones.TryGetValue(zone, out ZoneWeather? w) ? w.State : null;

    /// <summary>vmangos <c>WeatherSystem::FindOrCreateWeather</c>.</summary>
    public WeatherState FindOrCreate(uint zone)
    {
        if (!_zones.TryGetValue(zone, out ZoneWeather? weather))
        {
            WorldStateHooks hooks = Hooks;
            weather = new ZoneWeather(new WeatherState(zone, hooks.WeatherChances.Get(zone)), hooks.WeatherSettings.ChangeIntervalMs);
            _zones[zone] = weather;
        }

        return weather.State;
    }

    /// <summary>vmangos <c>Weather::SendWeatherUpdateToPlayer</c>: the zone's current weather to one player.</summary>
    public void SendTo(Player player, uint zone) => player.Session.Send(WorldOpcode.SmsgWeather, FindOrCreate(zone).BuildPacket());

    /// <summary>
    /// vmangos <c>Map::SetWeather</c> / <c>Weather::SetWeather</c> (<c>.wchange</c>): set the zone's
    /// weather, flag it permanent or not, and tell the zone's players unless nothing changed.
    /// </summary>
    public void SetWeather(uint zone, WeatherType type, float grade, bool permanent)
    {
        WeatherState state = FindOrCreate(zone);
        bool same = state.Type == type && state.Grade == grade;
        state.IsPermanent = permanent;
        if (same)
        {
            return;
        }

        state.SetWeather(type, grade, permanent);
        SendToPlayersInZone(zone, state);
    }

    public void Update(Map map, uint diffMs)
    {
        WorldStateHooks hooks = Hooks;
        if (!hooks.WeatherSettings.Enabled || _zones.Count == 0)
        {
            return;
        }

        List<uint>? removed = null;
        WeatherSeason? season = null;
        foreach ((uint zone, ZoneWeather weather) in _zones)
        {
            weather.Current += diffMs;
            if (weather.Current < weather.Interval)
            {
                continue;
            }

            weather.Current -= weather.Interval; // ShortIntervalTimer::Reset
            // The table can be replaced while the zone is live (hot reload): always read the current row.
            weather.State.Chances = hooks.WeatherChances.Get(zone);
            season ??= WeatherSeasons.Of(hooks.LocalNow());
            if (weather.State.ReGenerate(season.Value, hooks.WeatherRandom) && !SendToPlayersInZone(zone, weather.State))
            {
                (removed ??= []).Add(zone);
            }
        }

        if (removed is not null)
        {
            foreach (uint zone in removed)
            {
                _zones.Remove(zone);
            }
        }
    }

    public void OnPlayerRemoved(Map map, Player player)
    {
    }

    /// <summary>vmangos <c>Weather::SendWeatherForPlayersInZone</c> + <c>Map::SendToPlayersInZone</c>; false when nobody was in the zone.</summary>
    private bool SendToPlayersInZone(uint zone, WeatherState state)
    {
        byte[] packet = state.BuildPacket();
        bool found = false;
        foreach (Player player in _map.Players)
        {
            if (player.ZoneId == zone)
            {
                player.Session.Send(WorldOpcode.SmsgWeather, packet);
                found = true;
            }
        }

        return found;
    }
}
