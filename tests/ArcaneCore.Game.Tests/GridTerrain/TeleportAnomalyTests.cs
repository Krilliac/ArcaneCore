using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData;
using Xunit;

namespace ArcaneCore.Game.Tests.GridTerrain;

/// <summary>H1 outcome checks of <see cref="TeleportService"/>: each failing teleport is reported once under <c>teleport/*</c>, a correct one is silent.</summary>
public sealed class TeleportAnomalyTests
{
    private static readonly string Ack = ServerAnomalies.Key("teleport", "ack-timeout");
    private static readonly string FarStuck = ServerAnomalies.Key("teleport", "far-stuck");
    private static readonly string Invalid = ServerAnomalies.Key("teleport", "invalid-destination");
    private static readonly string NoMap = ServerAnomalies.Key("teleport", "map-not-loaded");
    private static readonly string Under = ServerAnomalies.Key("teleport", "landed-under-map");
    private static readonly string Off = ServerAnomalies.Key("teleport", "landed-off-destination");
    private static readonly string EntryFailed = ServerAnomalies.Key("teleport", "map-entry-failed");

    private static readonly MapContent Content = new(
        [
            new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""),
            new MapTemplate(1, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Kalimdor", ""),
            new MapTemplate(2, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Test continent", ""),
        ],
        [], [], [], []);

    private sealed class Refusing : IMapResolver
    {
        public Map ResolveLoginMap(Player player) => throw new NotSupportedException();

        public bool CanEnter(Player player, uint mapId) => true;

        public Map? ResolveEntry(Player player, uint mapId) => null;

        public void OnEntered(Player player, Map map)
        {
        }
    }

    /// <summary>Refuses the first <c>refusals</c> entries, then lets the player into the requested map (the destination and the origin refuse, the bind point does not).</summary>
    private sealed class RefusingFirst(WorldRuntime world, int refusals) : IMapResolver
    {
        private int _calls;

        public Map ResolveLoginMap(Player player) => throw new NotSupportedException();

        public bool CanEnter(Player player, uint mapId) => true;

        public Map? ResolveEntry(Player player, uint mapId) => ++_calls <= refusals ? null : world.GetMap(mapId);

        public void OnEntered(Player player, Map map)
        {
        }
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(Action<Player>? beforeAdd = null)
        {
            World = TestWorld.CreateRuntime();
            WorldMaps.Of(World).Load(Content);
            Teleports = new TeleportService(World, beforeAdd ?? (_ => { }), _ => { });
            World.PlayerLoggingOut += Teleports.Forget;
        }

        public WorldRuntime World { get; }

        public TeleportService Teleports { get; }

        public Player Add(uint guid)
        {
            Player player = TestWorld.CreatePlayer(guid, 0, 0, new FakeSession((int)guid));
            World.AddPlayer(player);
            World.RunTick(50);
            return player;
        }

        public void Dispose() => World.Dispose();
    }

    [Fact]
    public void NearTeleport_WhoseAckNeverArrives_IsReportedOnce()
    {
        using var f = new Fixture();
        Player a = f.Add(9101);
        using AnomalyCapture cap = ServerAnomalies.CaptureFor(a);

        Assert.True(f.Teleports.TeleportTo(a, 0, 500, 0, 90, 0));
        f.Teleports.CheckStalled(f.World.NowMs + TeleportService.NearAckBudgetMs + 1);
        f.Teleports.CheckStalled(f.World.NowMs + TeleportService.NearAckBudgetMs + 2000);

        Assert.Equal(1, cap.Count(Ack));
        Assert.True(f.Teleports.HandleTeleportAck(a, a.Guid.Value)); // a late ack still completes
    }

    [Fact]
    public void NearTeleport_AckedInTime_IsSilent()
    {
        using var f = new Fixture();
        Player a = f.Add(9102);
        using AnomalyCapture cap = ServerAnomalies.CaptureFor(a);

        Assert.True(f.Teleports.TeleportTo(a, 0, 500, 0, 90, 0));
        f.Teleports.CheckStalled(f.World.NowMs + TeleportService.NearAckBudgetMs - 1000);
        Assert.True(f.Teleports.HandleTeleportAck(a, a.Guid.Value));
        f.Teleports.CheckStalled(f.World.NowMs + TeleportService.NearAckBudgetMs * 3);

        Assert.Empty(cap.Notices);
    }

    [Fact]
    public void FarTeleport_WithoutAWorldportAck_IsReported_AndAnAckedOneIsNot()
    {
        using var f = new Fixture();
        Player stuck = f.Add(9103);
        Player fine = f.Add(9104);
        using AnomalyCapture capStuck = ServerAnomalies.CaptureFor(stuck);
        using AnomalyCapture capFine = ServerAnomalies.CaptureFor(fine);

        Assert.True(f.Teleports.TeleportTo(stuck, 1, 1000, 2000, 30, 0));
        Assert.True(f.Teleports.TeleportTo(fine, 1, 1100, 2000, 30, 0));
        f.World.RunTick(50);
        Assert.Equal(TeleportStage.Far, f.Teleports.StageOf(stuck));
        Assert.True(f.Teleports.HandleWorldportAck(fine));
        f.World.RunTick(50);

        f.Teleports.CheckStalled(f.World.NowMs + TeleportService.FarAckBudgetMs - 1000);
        Assert.False(capStuck.Any(Ack));
        f.Teleports.CheckStalled(f.World.NowMs + TeleportService.FarAckBudgetMs + 1000);

        Assert.Equal(1, capStuck.Count(Ack));
        Assert.Empty(capFine.Notices);
    }

    [Fact]
    public void FarTeleport_NeverExecuted_IsReportedAsStuck()
    {
        using var f = new Fixture();
        Player a = f.Add(9105);
        using AnomalyCapture cap = ServerAnomalies.CaptureFor(a);

        Assert.True(f.Teleports.TeleportTo(a, 1, 1000, 2000, 30, 0)); // scheduled, the map never updates

        f.Teleports.CheckStalled(f.World.NowMs + TeleportService.ScheduledBudgetMs + 1);

        Assert.Equal(1, cap.Count(FarStuck));
    }

    [Fact]
    public void RefusedDestinations_AreReported_ByTheirCause()
    {
        using var f = new Fixture();
        Player a = f.Add(9106);
        using AnomalyCapture cap = ServerAnomalies.CaptureFor(a);

        Assert.False(f.Teleports.TeleportTo(a, 0, 0, 0, float.NaN, 0));
        Assert.False(f.Teleports.TeleportTo(a, 77, 0, 0, 0, 0));

        Assert.Equal(1, cap.Count(Invalid));
        Assert.Equal(1, cap.Count(NoMap));
        AnomalyNotice invalid = Assert.Single(cap.Notices, n => n.Key == Invalid);
        Assert.Equal(AnomalySeverity.Error, invalid.Severity); // a non-finite destination
    }

    [Fact]
    public void AcceptedTeleports_AreSilent()
    {
        using var f = new Fixture();
        Player a = f.Add(9107);
        using AnomalyCapture cap = ServerAnomalies.CaptureFor(a);

        Assert.True(f.Teleports.TeleportTo(a, 0, 500, 0, 90, 0));
        Assert.True(f.Teleports.HandleTeleportAck(a, a.Guid.Value));
        Assert.True(f.Teleports.TeleportTo(a, 1, 1000, 2000, 30, 0));
        f.World.RunTick(50);
        Assert.True(f.Teleports.HandleWorldportAck(a));
        f.World.RunTick(50);

        Assert.Same(f.World.GetMap(1), a.Map);
        Assert.Empty(cap.Notices);
    }

    [Fact]
    public void TeleportIntoTheVoid_IsReportedAsLandedUnderTheMap()
    {
        using var f = new Fixture();
        Player a = f.Add(9108);
        using AnomalyCapture cap = ServerAnomalies.CaptureFor(a);

        Assert.True(f.Teleports.TeleportTo(a, 0, 500, 0, -800, 0));
        Assert.True(f.Teleports.HandleTeleportAck(a, a.Guid.Value));

        Assert.Equal(1, cap.Count(Under));
        Assert.Equal(AnomalySeverity.Error, Assert.Single(cap.Notices).Severity);
    }

    [Fact]
    public void FarTeleport_ThatLandsElsewhere_IsReportedAsOffDestination()
    {
        Player? moved = null;
        using var f = new Fixture(p => p.Relocate(p.X + 200f, p.Y, p.Z, p.Orientation, 0)); // something displaces the player on entry
        Player a = f.Add(9109);
        moved = a;
        using AnomalyCapture cap = ServerAnomalies.CaptureFor(moved);

        Assert.True(f.Teleports.TeleportTo(a, 1, 1000, 2000, 30, 0));
        f.World.RunTick(50);
        Assert.True(f.Teleports.HandleWorldportAck(a));
        f.World.RunTick(50);

        Assert.Equal(1, cap.Count(Off));
    }

    [Fact]
    public void FarTeleport_ThatCannotEnterTheMap_IsReported_AndSoIsAFailedReturn()
    {
        using var f = new Fixture();
        Player a = f.Add(9110);
        using AnomalyCapture cap = ServerAnomalies.CaptureFor(a);

        Assert.True(f.Teleports.TeleportTo(a, 1, 1000, 2000, 30, 0));
        f.World.RunTick(50);
        f.World.MapResolver = new Refusing(); // the destination cannot be entered now, nor can the way back
        Assert.True(f.Teleports.HandleWorldportAck(a));
        f.World.RunTick(50);

        Assert.Equal(1, cap.Count(EntryFailed));
        Assert.Equal(1, cap.Count(ServerAnomalies.Key("teleport", "return-failed"))); // the way back failed: the bind point is the fallback
    }

    [Fact]
    public void FarTeleport_ThatCannotEnterTheDestinationNorTheOrigin_GoesToTheBindPointOnAnotherMap_WithANewLoadingScreen()
    {
        using var f = new Fixture();
        Player a = f.Add(9111); // map 0 at the origin
        a.Home = new HomeBind(2, 0, 321f, 654f, 40f); // a map that is neither the origin (0) nor the destination (1)
        using AnomalyCapture cap = ServerAnomalies.CaptureFor(a);

        Assert.True(f.Teleports.TeleportTo(a, 1, 1000, 2000, 30, 0));
        f.World.RunTick(50);
        f.World.MapResolver = new RefusingFirst(f.World, refusals: 2); // the destination and the way back refuse, the bind point does not
        Assert.True(f.Teleports.HandleWorldportAck(a));
        f.World.RunTick(50);

        // vmangos TeleportToHomebind: a new far teleport. The client loaded map 1 and is told to load the bind point's map.
        Assert.False(((FakeSession)a.Session).Kicked);
        Assert.Equal(TeleportStage.Far, f.Teleports.StageOf(a));
        Assert.Equal(2u, f.Teleports.DestinationOf(a)?.MapId);
        Assert.Null(a.Map);

        Assert.True(f.Teleports.HandleWorldportAck(a));
        f.World.RunTick(50);

        Assert.False(((FakeSession)a.Session).Kicked);
        Assert.NotNull(a.Map);
        Assert.Equal(2u, a.Map!.MapId);
        Assert.InRange(a.X, 320f, 322f);
        Assert.InRange(a.Y, 653f, 655f);
        Assert.Equal(1, cap.Count(EntryFailed));
        Assert.Equal(1, cap.Count(ServerAnomalies.Key("teleport", "return-failed")));
    }

    [Fact]
    public void FarTeleport_ThatCannotEnterTheBindPointEither_KicksTheSession_AndDoesNotLoop()
    {
        using var f = new Fixture();
        Player a = f.Add(9112);
        a.Home = new HomeBind(2, 0, 321f, 654f, 40f);
        using AnomalyCapture cap = ServerAnomalies.CaptureFor(a);

        Assert.True(f.Teleports.TeleportTo(a, 1, 1000, 2000, 30, 0));
        f.World.RunTick(50);
        f.World.MapResolver = new Refusing(); // nothing can be entered
        Assert.True(f.Teleports.HandleWorldportAck(a));
        f.World.RunTick(50);
        Assert.Equal(TeleportStage.Far, f.Teleports.StageOf(a)); // the bind point's loading screen
        Assert.False(((FakeSession)a.Session).Kicked);

        Assert.True(f.Teleports.HandleWorldportAck(a));
        f.World.RunTick(50);

        Assert.True(((FakeSession)a.Session).Kicked);
        Assert.Null(f.Teleports.StageOf(a));
        Assert.Equal(2, cap.Count(ServerAnomalies.Key("teleport", "return-failed"))); // the fallback, then the kick
    }
}
