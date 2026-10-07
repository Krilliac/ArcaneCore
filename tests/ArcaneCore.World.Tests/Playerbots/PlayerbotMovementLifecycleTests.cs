using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Numerics;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotMovementLifecycleTests
{
    [Fact]
    public async Task SeatedRouteStandsThenStartsForwardAndContinuesWithHeartbeat()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                player.SetStandState(StandState.Sit);
                var route = new PlayerbotRoute([new(player.X, player.Y, player.Z), new(player.X + 2, player.Y, player.Z)], 2);
                var options = new PlayerbotOptions { MoveSpeed = 4, MaxPathPoints = 8, MaxRouteYards = 8 };
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(PlayerbotNavigation.TryAdvance(session, route, options, 500, host.World.NowMs));
                Assert.Equal(StandState.Stand, player.StandState);
                Assert.Equal(0, session.ManagedBudget.Remaining);
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(PlayerbotNavigation.TryAdvance(session, route, options, 500, host.World.NowMs));
                Assert.True(player.Movement.HasFlag(MovementFlags.Forward));
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task BudgetZeroDoesNotMutateMovementOrConsumeRoute()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                var route = new PlayerbotRoute([new(player.X, player.Y, player.Z), new(player.X + 2, player.Y, player.Z)], 2);
                int next = route.NextPoint;
                session.ManagedBudget = new ManagedActionBudget(0);
                Assert.False(PlayerbotNavigation.TryAdvance(session, route,
                    new PlayerbotOptions { MoveSpeed = 4, MaxPathPoints = 8, MaxRouteYards = 8 }, 500, host.World.NowMs));
                Assert.Equal(next, route.NextPoint);
                Assert.False(player.Movement.HasFlag(MovementFlags.Forward));
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task ArrivalStopIsSentEvenWithAnExhaustedActionBudget()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                var options = new PlayerbotOptions { MaxPathPoints = 8, MaxRouteYards = 8 };
                Assert.True(PlayerbotNavigation.TryPlan(player, new(player.X + 2, player.Y, player.Z), options, out PlayerbotRoute? route));
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(PlayerbotNavigation.TryAdvance(session, route!, options, 500, host.World.NowMs));
                Assert.True(player.Movement.HasFlag(MovementFlags.Forward));
                // Half a second later the bot (7 yd/s) has arrived: the STOP goes out although no budget is left.
                PlayerbotMotion.ElapseForTests(player, 500);
                session.ManagedBudget = new ManagedActionBudget(0);
                Assert.True(PlayerbotNavigation.TryAdvance(session, route!, options, 500, host.World.NowMs));
                Assert.True(route!.Complete);
                Assert.Equal(MovementFlags.None, player.Movement.Flags & MovementFlags.MaskMoving);
                Assert.Equal(route.Points[^1].X, player.X, 2);
                Assert.False(PlayerbotMovementControl.Update(session, player)); // nothing left to acknowledge
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task StopLandsAtTheRoutePositionOfTheCurrentTime()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                Player player = session.Player!;
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                float start = player.X;
                Assert.True(PlayerbotNavigation.TryTerrainRoute(new(start, player.Y, player.Z),
                    new(start + 7, player.Y, player.Z), new PlayerbotOptions(), (_, _, _) => 83.53f,
                    (_, _) => true, out PlayerbotRoute? route));
                Assert.True(route!.Points.Count > 2);
                // The bot started running along the route a second ago; observers have been extrapolating since.
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(PlayerbotNavigation.TryAdvance(session, route, new PlayerbotOptions(), 500,
                    unchecked(host.World.NowMs - 1000)));
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(PlayerbotMovementControl.Stop(session, player));
                Assert.True(player.X > start + 6.5f);
                Assert.Equal(MovementFlags.None, player.Movement.Flags & MovementFlags.MaskMoving);
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task RealRoute_ObserverSeesPackedStartHeartbeatAndStopInOrder()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession mover = await EnterNamedAsync(host, "BOTMOVEA", "Movera");
        WorldSession observer = await EnterNamedAsync(host, "BOTMOVEB", "Watcherb");
        try
        {
            await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                return true;
            });
            await host.WaitForWorldAsync(() => observer.Player!.VisibleObjects.Contains(mover.Player!.Guid),
                "observer sees mover");
            observer.DrainManagedPackets();
            await host.OnWorldAsync(() =>
            {
                Player player = mover.Player!;
                var options = new PlayerbotOptions { MaxPathPoints = 8, MaxRouteYards = 8 };
                Assert.True(PlayerbotNavigation.TryPlan(player, new(player.X + 5, player.Y, player.Z), options,
                    out PlayerbotRoute? route));
                mover.ManagedBudget = new ManagedActionBudget(1);
                Assert.True(PlayerbotNavigation.TryAdvance(mover, route!, options, 500, host.World.NowMs));
                // 7 yd/s: 500 ms later a heartbeat reports 3.5 yd; 500 ms after that the bot has reached the
                // 5-yard end and stops there.
                for (int i = 0; i < 2; i++)
                {
                    PlayerbotMotion.ElapseForTests(player, 500);
                    mover.ManagedBudget = new ManagedActionBudget(1);
                    Assert.True(PlayerbotNavigation.TryAdvance(mover, route!, options, 500, host.World.NowMs));
                }
                Assert.True(route!.Complete);
                return true;
            });
            ManagedSessionPacket start = Assert.Single(observer.DrainManagedPackets(WorldOpcode.MsgMoveStartForward));
            PacketReader startReader = new(start.Payload);
            Assert.Equal(mover.Player!.Guid.Value, startReader.ReadPackedGuid());
            MovementInfo startInfo = MovementInfo.Read(ref startReader);
            Assert.True(startInfo.Flags.HasFlag(MovementFlags.Forward));
            IReadOnlyList<ManagedSessionPacket> heartbeats = observer.DrainManagedPackets(WorldOpcode.MsgMoveHeartbeat);
            Assert.Single(heartbeats);
            ManagedSessionPacket heartbeat = heartbeats[^1];
            PacketReader heartbeatReader = new(heartbeat.Payload);
            Assert.Equal(mover.Player.Guid.Value, heartbeatReader.ReadPackedGuid());
            Assert.True(MovementInfo.Read(ref heartbeatReader).X > startInfo.X);
            ManagedSessionPacket stop = Assert.Single(observer.DrainManagedPackets(WorldOpcode.MsgMoveStop));
            PacketReader stopReader = new(stop.Payload);
            Assert.Equal(mover.Player.Guid.Value, stopReader.ReadPackedGuid());
            Assert.Equal(MovementFlags.None, MovementInfo.Read(ref stopReader).Flags & MovementFlags.MaskMoving);
        }
        finally
        {
            mover.Kick(); observer.Kick();
            await mover.ManagedClosed; await observer.ManagedClosed;
        }
    }

    [Fact]
    public async Task InvalidFloorStopsAtCurrentPositionWithoutStartingMovement()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                WorldCollision.Of(host.World).Install(lineOfSight: new InvalidFloor());
                var route = new PlayerbotRoute([new(player.X, player.Y, player.Z), new(player.X + 2, player.Y, player.Z)], 2);
                Vector3 before = new(player.X, player.Y, player.Z);
                session.ManagedBudget = new ManagedActionBudget(1);
                Assert.False(PlayerbotNavigation.TryAdvance(session, route,
                    new PlayerbotOptions { MoveSpeed = 4, MaxPathPoints = 8, MaxRouteYards = 8 }, 500, host.World.NowMs));
                Assert.Equal(before, new Vector3(player.X, player.Y, player.Z));
                Assert.Equal(MovementFlags.None, player.Movement.Flags & MovementFlags.MaskMoving);
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    private static async Task<WorldSession> EnterNamedAsync(WorldTestHost host, string username, string character)
    {
        Account owner = await host.Accounts.CreateAsync(new Account
        { Username = username, Salt = new byte[32], Verifier = new byte[32] });
        WorldSession session = await WorldSession.CreateManagedAsync(owner, null, host.WorldServices, host.Opcodes,
            host.World, host.Registry, new WorldSessionOptions(), NullLogger.Instance);
        var create = new PacketWriter(); create.WriteCString(character); create.WriteByte(1); create.WriteByte(1);
        for (int i = 0; i < 8; i++) create.WriteByte(0);
        await session.DispatchManagedSessionAsync(WorldOpcode.CmsgCharCreate, create.ToArray());
        var login = new PacketWriter();
        login.WriteUInt64((ulong)(await session.Services.GetRequiredService<ICharacterStore>().GetByAccountAsync(owner.Id)).Single().Id);
        await session.DispatchManagedSessionAsync(WorldOpcode.CmsgPlayerLogin, login.ToArray());
        await host.WaitForWorldAsync(() => session.Player is not null, $"{character} login");
        return session;
    }

    private sealed class FlatFloor : ILineOfSight
    {
        public bool Enabled => true;
        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;
        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        { hit = to; return false; }
        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => 83.53f;
        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        { info = default; return false; }
    }

    private sealed class InvalidFloor : ILineOfSight
    {
        public bool Enabled => true;
        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;
        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        { hit = to; return false; }
        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => float.NaN;
        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        { info = default; return false; }
    }
}
