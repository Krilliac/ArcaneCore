using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Time;
using ArcaneCore.Game.WorldState.Weather;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.WorldState;

/// <summary>Per-map weather: vmangos WeatherSystem / Weather::Update (Weather.cpp:67-84, 212-267, 371-399).</summary>
public sealed class MapWeatherTests
{
    private sealed class ByGuidLocator : IZoneLocator
    {
        public Dictionary<ulong, uint> Zones { get; } = [];

        public bool CanDeriveZones => true;

        public (uint ZoneId, uint AreaId) Locate(Map map, Player player) => (Zones.GetValueOrDefault(player.Guid.Value, 12u), 1);

        public AreaTemplate? Find(uint areaId) => new AreaTemplate(areaId, 0, 0, areaId, 0, 1, "z", 0, 0);
    }

    private sealed class Script(params uint[] ints) : IWeatherRandom
    {
        private readonly Queue<uint> _ints = new(ints);
        private readonly Queue<float> _floats = new([0.5f]);

        public uint Next(uint min, uint max) => _ints.Dequeue();

        public float NextFloat() => _floats.Dequeue();

        public int Remaining => _ints.Count;
    }

    private static ZoneWeatherChances Rain100()
        => ZoneWeatherChances.FromColumns(12, [100, 0, 0, 100, 0, 0, 100, 0, 0, 100, 0, 0]);

    private sealed class Fixture
    {
        public WorldRuntime World { get; } = TestWorld.CreateRuntime();

        public WorldStateHooks Hooks { get; }

        public ByGuidLocator Locator { get; } = new();

        public Fixture()
        {
            Hooks = WorldStateHooks.For(World);
            Hooks.Locator = Locator;
            Hooks.Time = new FixedGameTime(new DateTimeOffset(2023, 3, 20, 12, 0, 0, TimeSpan.Zero));
            Hooks.WeatherSettings.ChangeIntervalMs = 1000;
            Hooks.WeatherChances.Replace([new KeyValuePair<uint, ZoneWeatherChances>(12, Rain100())]);
        }

        public MapWeather Weather => World.GetMap(0).FindUpdater<MapWeather>()!;

        public (Player Player, FakeSession Session) Join(uint guid, uint zone)
        {
            var session = new FakeSession();
            Player player = TestWorld.CreatePlayer(guid, 0, 0, session);
            Locator.Zones[player.Guid.Value] = zone;
            World.AddPlayer(player);
            return (player, session);
        }
    }

    private static List<byte[]> Weather(FakeSession session)
        => session.Sent.Where(p => p.Opcode == WorldOpcode.SmsgWeather).Select(p => p.Payload).ToList();

    [Fact]
    public void EveryMap_HasItsOwnWeatherSystem()
    {
        var f = new Fixture();
        Assert.Contains(typeof(MapWeather), DefaultMapUpdaters.Types);
        Assert.NotSame(f.World.GetMap(0).FindUpdater<MapWeather>(), f.World.GetMap(1).FindUpdater<MapWeather>());
    }

    [Fact]
    public void ZoneEntry_CreatesFineWeather_AndSendsIt()
    {
        var f = new Fixture();
        (Player player, FakeSession session) = f.Join(1, 12);
        f.World.RunTick(50);

        f.Weather.SendTo(player, 12);

        Assert.Equal([[0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]], Weather(session));
        Assert.Equal([12u], f.Weather.ActiveZones);
    }

    [Fact]
    public void Regeneration_HappensOnceAtTheInterval_AndOnlyZoneMembersGetTheChange()
    {
        var f = new Fixture();
        var script = new Script(40, 1); // u=40 (get fair -> fresh roll), roll 1 -> rain with chance 100
        f.Hooks.WeatherRandom = script;
        (Player a, FakeSession sa) = f.Join(1, 12);
        (_, FakeSession sb) = f.Join(2, 40);
        f.World.RunTick(50); // zone tracker places both
        f.Weather.FindOrCreate(12);
        f.Weather.FindOrCreate(40);
        sa.Clear();
        sb.Clear();

        f.World.RunTick(900);
        Assert.Equal(2, script.Remaining); // nothing regenerated before the interval
        Assert.Empty(Weather(sa));

        f.World.RunTick(100); // 1000 ms elapsed: exactly one regen of zone 12 (zone 40 has no chances: fine, no change)
        Assert.Equal(0, script.Remaining);
        byte[] packet = Assert.Single(Weather(sa));
        Assert.Equal(1u, BitConverter.ToUInt32(packet, 0)); // rain
        Assert.Equal(0.5f * 0.3333f, BitConverter.ToSingle(packet, 4));
        Assert.Equal(0u, BitConverter.ToUInt32(packet, 8)); // grade < 0.3: no sound
        Assert.Equal(0, packet[12]);
        Assert.Empty(Weather(sb));
        Assert.Equal(WeatherType.Rain, f.Weather.Find(12)!.Type);
        _ = a;
    }

    [Fact]
    public void ChangedWeatherInAnEmptyZone_DropsTheState_AndAnUnchangedOneKeepsIt()
    {
        var f = new Fixture();
        f.Join(1, 40); // a player elsewhere
        f.World.RunTick(50);
        f.Weather.FindOrCreate(12);

        f.Hooks.WeatherRandom = new Script(29); // u < 30: no change, the (empty) zone is kept
        f.World.RunTick(1000);
        Assert.Contains(12u, f.Weather.ActiveZones);

        f.Hooks.WeatherRandom = new Script(40, 1); // changes to rain, nobody in zone 12: removed
        f.World.RunTick(1000);
        Assert.DoesNotContain(12u, f.Weather.ActiveZones);

        Assert.Equal(WeatherType.Fine, f.Weather.FindOrCreate(12).Type); // the next visitor starts fresh
    }

    [Fact]
    public void ZoneWithoutChances_NeverLeavesFine_AndConsumesNoRandomness()
    {
        var f = new Fixture();
        (Player player, FakeSession session) = f.Join(1, 40);
        f.World.RunTick(50);
        f.Weather.FindOrCreate(40);
        var script = new Script();
        f.Hooks.WeatherRandom = script;

        f.World.RunTick(1000);
        f.World.RunTick(1000);

        Assert.Equal(WeatherType.Fine, f.Weather.Find(40)!.Type);
        Assert.Empty(Weather(session));
        _ = player;
    }

    [Fact]
    public void Disabled_NeverRegenerates()
    {
        var f = new Fixture();
        f.Join(1, 12);
        f.World.RunTick(50);
        f.Weather.FindOrCreate(12);
        f.Hooks.WeatherSettings.Enabled = false;
        var script = new Script(40, 1);
        f.Hooks.WeatherRandom = script;

        f.World.RunTick(5000);

        Assert.Equal(2, script.Remaining);
    }

    [Fact]
    public void SetWeather_TellsTheZone_UnlessNothingChanged_AndPermanentWeatherDoesNotRegenerate()
    {
        var f = new Fixture();
        (_, FakeSession inZone) = f.Join(1, 12);
        (_, FakeSession elsewhere) = f.Join(2, 40);
        f.World.RunTick(50);
        inZone.Clear();
        elsewhere.Clear();

        f.Weather.SetWeather(12, WeatherType.Rain, 0.5f, permanent: true);

        byte[] packet = Assert.Single(Weather(inZone));
        Assert.Equal([1, 0, 0, 0, 0, 0, 0, 0x3F, 0x55, 0x21, 0, 0, 0], packet);
        Assert.Empty(Weather(elsewhere));

        f.Weather.SetWeather(12, WeatherType.Rain, 0.5f, permanent: true); // same: not re-sent
        Assert.Single(Weather(inZone));

        var script = new Script();
        f.Hooks.WeatherRandom = script;
        f.World.RunTick(3000);
        Assert.Equal(WeatherType.Rain, f.Weather.Find(12)!.Type);
    }

    [Fact]
    public void ReplacedChances_AreSeenByLiveZones()
    {
        var f = new Fixture();
        f.Join(1, 12);
        f.World.RunTick(50);
        f.Weather.FindOrCreate(12);
        f.Hooks.WeatherChances.Replace([]); // the zone loses its row: it is forced fine on the next regen
        f.Weather.Find(12)!.SetWeather(WeatherType.Storm, 0.8f);

        f.World.RunTick(1000);

        Assert.Equal(WeatherType.Fine, f.Weather.Find(12)!.Type);
    }
}
