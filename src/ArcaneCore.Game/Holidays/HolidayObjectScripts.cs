using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Holidays;

/// <summary>
/// The hourly city bells, vmangos src/scripts/world/go_scripts.cpp (go_bells): when the hourly bells event starts, every Horde Bell (175885)
/// and Alliance Bell (176573) tolls the hour on the 12-hour clock (12 at noon and midnight), one toll every four seconds starting one second
/// in. The sound depends on the bell and its zone; the Ironforge horn and the lighthouse foghorns sound once. vmangos runs this on its event 78;
/// ClassicDB's is event 1024 "Hourly Bells" (one minute each hour), and its bells are permanent spawns, so a bell tolls at every start of the
/// event rather than once per spawn.
/// </summary>
public sealed class BellAi(Func<ushort, bool> isActiveEvent, Func<DateTime>? localNow = null) : IGameObjectAi
{
    public const uint GoHordeBell = 175885;
    public const uint GoAllianceBell = 176573;
    public const ushort GameEventHourlyBells = 1024;

    public const uint BellTollHorde = 6595, BellTollTribal = 6675, BellTollAlliance = 6594, BellTollNightElf = 6674,
        BellTollDwarfGnome = 7234, LighthouseFoghorn = 7197;

    private sealed class BellState
    {
        public bool WasActive;
        public uint SoundId;
        public long ClockMs;
        public readonly List<long> Rings = [];
    }

    private readonly Dictionary<ObjectGuid, BellState> _states = [];
    private readonly Func<DateTime> _now = localNow ?? (() => DateTime.Now);

    /// <summary>vmangos go_bells constructor: the toll sound by entry, zone and lighthouse area.</summary>
    internal static uint SoundFor(uint entry, uint zoneId, uint areaId, float x, float y, float z)
    {
        if (entry == GoHordeBell)
            return zoneId is 85 or 1497 or 267 or 10 ? BellTollHorde : BellTollTribal;
        if (IsLighthouse(areaId, x, y, z))
            return LighthouseFoghorn;
        return zoneId switch
        {
            1537 or 1 => BellTollDwarfGnome,
            141 or 1657 or 331 => BellTollNightElf,
            _ => BellTollAlliance,
        };
    }

    // vmangos isLightHouseObject: Theramore's area also holds a town bell, so only the one within a yard of the lighthouse counts.
    private static bool IsLighthouse(uint areaId, float x, float y, float z) => areaId switch
    {
        513 => MathF.Sqrt(((x + 3667f) * (x + 3667f)) + ((y + 4754f) * (y + 4754f)) + ((z - 1.8f) * (z - 1.8f))) < 1f,
        2079 or 115 => true,
        _ => false,
    };

    /// <summary>How many tolls an hour gets: hour % 12, with 0 as 12; the horn and the foghorns sound once.</summary>
    internal static int RingsFor(int hour, uint soundId)
    {
        if (soundId is BellTollDwarfGnome or LighthouseFoghorn) return 1;
        int rings = hour % 12;
        return rings == 0 ? 12 : rings;
    }

    public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, Unit target) => false;

    public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs)
    {
        if (!_states.TryGetValue(go.Guid, out BellState? state))
        {
            (uint zone, uint area) = objects.Map.GetZoneAndAreaId(go.X, go.Y, go.Z);
            state = new BellState { SoundId = SoundFor(go.Entry, zone, area, go.X, go.Y, go.Z) };
            _states[go.Guid] = state;
        }

        state.ClockMs += diffMs;
        bool active = isActiveEvent(GameEventHourlyBells);
        if (active && !state.WasActive)
        {
            // EVENT_TIME one second after the start, then the tolls at i * 4 + 1 seconds after that.
            int rings = RingsFor(_now().Hour, state.SoundId);
            for (int i = 0; i < rings; i++) state.Rings.Add(state.ClockMs + 1000 + ((i * 4 + 1) * 1000L));
        }

        state.WasActive = active;
        for (int i = 0; i < state.Rings.Count; i++)
        {
            if (state.Rings[i] > state.ClockMs) continue;
            state.Rings.RemoveAt(i--);
            var sound = new PacketWriter(4);
            sound.WriteUInt32(state.SoundId);
            objects.Map.BroadcastToObservers(go, WorldOpcode.SmsgPlaySound, sound.ToArray());
        }
    }
}

/// <summary>
/// The Darkmoon Faire music, vmangos go_scripts.cpp (go_darkmoon_faire_music, object 180335): while either Darkmoon Faire event (4 Elwynn,
/// 5 Mulgore) runs, the object sends SMSG_PLAY_MUSIC 8440 to the players around it every five seconds (the sniffed interval), first one
/// second after it appears.
/// </summary>
public sealed class DarkmoonFaireMusicAi(Func<ushort, bool> isActiveEvent) : IGameObjectAi
{
    public const uint GoDarkmoonFaireMusic = 180335;
    public const uint MusicDarkmoonFaire = 8440;
    public const uint IntervalMs = 5000;

    private readonly Dictionary<ObjectGuid, long> _untilNext = [];

    public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, Unit target) => false;

    public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs)
    {
        long left = _untilNext.TryGetValue(go.Guid, out long t) ? t : 1000;
        left -= diffMs;
        if (left <= 0)
        {
            if (isActiveEvent(4) || isActiveEvent(5))
            {
                var music = new PacketWriter(4);
                music.WriteUInt32(MusicDarkmoonFaire);
                objects.Map.BroadcastToObservers(go, WorldOpcode.SmsgPlayMusic, music.ToArray());
            }

            left = IntervalMs;
        }

        _untilNext[go.Guid] = left;
    }
}
