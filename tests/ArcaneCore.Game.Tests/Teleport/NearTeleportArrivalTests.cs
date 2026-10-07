using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Teleport;

/// <summary>
/// The arrival of a same-map teleport (vmangos <c>Player::ExecuteTeleportNear</c>, MovementHandler.cpp:247-286, and
/// <c>Unit::TeleportPositionRelocation</c>, Unit.cpp:9845-9883): only the position of the movement block changes, and the zone or area
/// update runs at once instead of waiting for the 1 s zone timer.
/// </summary>
public sealed class NearTeleportArrivalTests
{
    private static readonly MapContent Content = new(
        [new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", "")],
        [], [], [], []);

    /// <summary>West of x = 250 is Elwynn (zone 12, area 9 or 87 north of y = 100), east of it Westfall (zone 40, area 108).</summary>
    private sealed class HalfMapLocator : IZoneLocator
    {
        private readonly Dictionary<uint, AreaTemplate> _entries = new()
        {
            [12] = new AreaTemplate(12, 0, 0, 41, 0, 1, "Elwynn Forest", 0, 0),
            [40] = new AreaTemplate(40, 0, 0, 43, 0, 10, "Westfall", 0, 0),
        };

        public bool CanDeriveZones => true;

        public (uint ZoneId, uint AreaId) Locate(Map map, Player player)
            => player.X < 250 ? (12u, player.Y < 100 ? 9u : 87u) : (40u, 108u);

        public AreaTemplate? Find(uint areaId) => _entries.GetValueOrDefault(areaId);
    }

    private sealed class Recorder : IPlayerLocationListener
    {
        public List<string> Events { get; } = [];

        public void OnZoneChanged(Player player, uint oldZone, uint newZone, uint newArea, AreaTemplate? zoneEntry)
            => Events.Add($"zone {oldZone}->{newZone} area {newArea}");

        public void OnAreaChanged(Player player, uint oldArea, uint newArea) => Events.Add($"area {oldArea}->{newArea}");
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            World = TestWorld.CreateRuntime();
            WorldMaps.Of(World).Load(Content);
            WorldStateHooks hooks = WorldStateHooks.For(World);
            hooks.Locator = new HalfMapLocator();
            hooks.AddLocationListener(Recorder);
            Teleports = new TeleportService(World, _ => { }, _ => { });
            Session = new FakeSession(1);
            Player = TestWorld.CreatePlayer(1, 0, 0, Session);
            World.AddPlayer(Player);
            World.RunTick(50);   // the first zone update: Elwynn, area 9
            Session.Clear();
            Recorder.Events.Clear();
        }

        public WorldRuntime World { get; }

        public TeleportService Teleports { get; }

        public FakeSession Session { get; }

        public Player Player { get; }

        public Recorder Recorder { get; } = new();

        public ZoneAreaUpdater Zones => Player.Map!.FindUpdater<ZoneAreaUpdater>()!;

        public void NearTeleport(float x, float y, float z, float o)
        {
            Assert.True(Teleports.TeleportTo(Player, 0, x, y, z, o));
            Assert.True(Teleports.HandleTeleportAck(Player, Player.Guid.Value));
        }

        public void Dispose() => World.Dispose();
    }

    [Fact]
    public void TheAck_KeepsTheClientsMovementState_OnlyThePositionChanges()
    {
        using var f = new Fixture();
        MovementInfo swimming = f.Player.Movement;
        swimming.Flags = MovementFlags.Swimming | MovementFlags.Levitating | MovementFlags.Forward;
        swimming.Pitch = 0.25f;
        f.Player.ApplyMovement(swimming, f.World.NowMs);

        f.NearTeleport(120, 10, 40, 2f);

        MovementInfo arrived = f.Player.Movement;
        Assert.Equal((120f, 10f, 40f, 2f), (arrived.X, arrived.Y, arrived.Z, arrived.Orientation));
        Assert.Equal((120f, 10f, 40f), (f.Player.X, f.Player.Y, f.Player.Z));
        // TeleportTo stops the motion (MOVEFLAG_MASK_MOVING_OR_TURN, Player.cpp:1887-1888); the swimming and levitating state stays.
        Assert.Equal(MovementFlags.Swimming | MovementFlags.Levitating, arrived.Flags);
        Assert.Equal(0.25f, arrived.Pitch);
    }

    [Fact]
    public void TheAck_RunsTheZoneUpdateAtOnce_WhenTheZoneChanged()
    {
        using var f = new Fixture();

        f.NearTeleport(500, 0, 40, 0f);

        Assert.Equal(["zone 12->40 area 108", "area 9->108"], f.Recorder.Events);
        Assert.Equal(40u, f.Zones.GetZone(f.Player));
        Assert.Equal(108u, f.Zones.GetArea(f.Player));
        Assert.Equal(40u, f.Player.ZoneId);
    }

    [Fact]
    public void TheAck_RunsOnlyTheAreaUpdate_WhenTheZoneIsTheSame()
    {
        using var f = new Fixture();

        f.NearTeleport(10, 200, 40, 0f);
        Assert.Equal(["area 9->87"], f.Recorder.Events);
        Assert.Equal(87u, f.Zones.GetArea(f.Player));

        f.Recorder.Events.Clear();
        f.NearTeleport(20, 210, 40, 0f);   // same zone and area: nothing to tell
        Assert.Empty(f.Recorder.Events);
    }
}
