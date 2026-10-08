using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Scenarios;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;

namespace ArcaneCore.World.Tests.Playerbots.Scenarios;

/// <summary>
/// <c>World:Playerbots:MovementPackets</c>. On (the default), a bot reports its motion with the client's MSG_MOVE_* packets through
/// the movement handlers; off, the server relocates the bot itself and relays the same MSG_MOVE_* to its observers without the
/// opcode dispatch. Two bots walk the same route from the same spot in the same world-thread call (so the same server times), one
/// in each mode, beside an observer: what the observer receives of each must be the same packets, and only the first bot's moves
/// go through the movement handlers.
/// </summary>
public sealed class PlayerbotMovementPacketsTests
{
    private const string BotC = "Scngamma";

    [Fact]
    public void MovementPackets_IsOnByDefault()
        => Assert.True(new PlayerbotOptions().MovementPackets);

    [Fact]
    public async Task TheObserverStream_IsTheSameInBothModes_ForABotWalkingARoute()
    {
        Walked walked = await WalkSideBySideAsync(flipOffModeAfter: null);

        Assert.True(walked.On.Count >= 4, $"the on-mode walk produced {walked.On.Count} packets");
        Assert.Equal(WorldOpcode.MsgMoveStartForward, walked.On[0].Opcode);
        Assert.Contains(walked.On, packet => packet.Opcode == WorldOpcode.MsgMoveHeartbeat);
        Assert.Equal(WorldOpcode.MsgMoveStop, walked.On[^1].Opcode);
        Assert.Equal(walked.On.Select(p => p.Opcode), walked.Off.Select(p => p.Opcode));
        for (int i = 0; i < walked.On.Count; i++)
            Assert.True(walked.On[i].Block.AsSpan().SequenceEqual(walked.Off[i].Block), $"packet #{i} ({walked.On[i].Opcode}) differs between the modes");
        Assert.Equal(walked.OnEnd, walked.OffEnd);
    }

    [Fact]
    public async Task WithMovementPacketsOff_NoMovementHandlerDispatchHappensForTheBot()
    {
        Walked walked = await WalkSideBySideAsync(flipOffModeAfter: null);

        // The control: the on-mode bot's every observed packet went through the handler table.
        Assert.Equal(walked.On.Count, walked.OnDispatched.Count(MovementOpcodes.IsRelayable));
        Assert.DoesNotContain(walked.OffDispatched, MovementOpcodes.IsRelayable);
        Assert.NotEmpty(walked.Off);
    }

    [Fact]
    public async Task TurningMovementPacketsOff_MidRoute_TakesEffectAtTheNextPacket()
    {
        // The options object is the live one .reload config changes in place: a bot already walking switches mode at once.
        Walked walked = await WalkSideBySideAsync(flipOffModeAfter: 2);

        int dispatched = walked.OffDispatched.Count(MovementOpcodes.IsRelayable);
        Assert.Equal(2, dispatched);
        Assert.True(walked.Off.Count > dispatched, "the walk went on after the switch");
        Assert.Equal(walked.On.Select(p => p.Opcode), walked.Off.Select(p => p.Opcode));
    }

    private sealed record Packet(WorldOpcode Opcode, byte[] Block);

    private sealed record Walked(List<Packet> On, List<Packet> Off, List<WorldOpcode> OnDispatched, List<WorldOpcode> OffDispatched,
        Vector3 OnEnd, Vector3 OffEnd);

    /// <param name="flipOffModeAfter">Start the second bot in packet mode and turn the option off after this many of its moves (null: off from the start).</param>
    private static async Task<Walked> WalkSideBySideAsync(int? flipOffModeAfter)
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync();
        Walked? result = null;
        ScenarioReport report = await world.RunAsync(new DelegateScenario("movement-packets-modes", async context =>
        {
            ScenarioBot on = await context.LoginAsync(PlayerbotScenarioCatalog.BotA);
            ScenarioBot observer = await context.LoginAsync(PlayerbotScenarioCatalog.BotB);
            ScenarioBot off = await context.LoginAsync(BotC);
            await context.ReadAsync(() =>
            {
                WorldCollision.Of(context.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
                return true;
            });
            // South of the start, clear of the test map's teleport trigger (PlayerbotMovementWireTests).
            await context.PlaceAsync(observer, 0, StartX, StartY - 14f, StartZ);
            await context.PlaceAsync(on, 0, StartX, StartY - 20f, StartZ);
            await context.PlaceAsync(off, 0, StartX, StartY - 20f, StartZ);
            await context.WaitUntilAsync("observer sees both movers", () =>
            {
                Player seer = observer.RequirePlayerForTests();
                return seer.VisibleObjects.Contains(on.Guid) && seer.VisibleObjects.Contains(off.Guid);
            });

            var onDispatched = new List<WorldOpcode>();
            var offDispatched = new List<WorldOpcode>();
            on.Session!.ManagedDispatchObserver = onDispatched.Add;
            off.Session!.ManagedDispatchObserver = offDispatched.Add;
            long mark = observer.Mark();
            (Vector3 onEnd, Vector3 offEnd) = await context.ReadAsync(() =>
            {
                var onOptions = new PlayerbotOptions { Enabled = true, MaxRouteYards = 200, MovementPackets = true };
                var offOptions = new PlayerbotOptions { Enabled = true, MaxRouteYards = 200, MovementPackets = flipOffModeAfter is not null };
                Player a = on.RequirePlayerForTests(), c = off.RequirePlayerForTests();
                Vector3 destination = new(StartX + 15f, StartY - 20f, StartZ);
                Assert.True(PlayerbotNavigation.TryPlan(a, destination, onOptions, out PlayerbotRoute? routeA));
                Assert.True(PlayerbotNavigation.TryPlan(c, destination, offOptions, out PlayerbotRoute? routeC));
                uint now = context.World.NowMs;
                Assert.True(PlayerbotMotion.Follow(on.Session!, a, routeA!, onOptions, now));
                Assert.True(PlayerbotMotion.Follow(off.Session!, c, routeC!, offOptions, now));
                for (int step = 0; step < 100 && (PlayerbotMotion.IsActive(a) || PlayerbotMotion.IsActive(c)); step++)
                {
                    if (flipOffModeAfter is { } flip && offDispatched.Count(MovementOpcodes.IsRelayable) >= flip) offOptions.MovementPackets = false;
                    PlayerbotMotion.ElapseForTests(a, 300);
                    PlayerbotMotion.ElapseForTests(c, 300);
                    if (PlayerbotMotion.IsActive(a)) PlayerbotMotion.Follow(on.Session!, a, routeA!, onOptions, now);
                    if (PlayerbotMotion.IsActive(c)) PlayerbotMotion.Follow(off.Session!, c, routeC!, offOptions, now);
                }

                Assert.False(PlayerbotMotion.IsActive(a), "the on-mode bot did not arrive");
                Assert.False(PlayerbotMotion.IsActive(c), "the off-mode bot did not arrive");
                return (new Vector3(a.X, a.Y, a.Z), new Vector3(c.X, c.Y, c.Z));
            });
            on.Session!.ManagedDispatchObserver = null;
            off.Session!.ManagedDispatchObserver = null;

            List<(ulong Mover, Packet Packet)> seen = observer.Log.Snapshot()
                .Where(p => p.Sequence >= mark && p.Direction == ScenarioPacketDirection.Received && MovementOpcodes.IsRelayable(p.Opcode))
                .Select(p => (p.Opcode, View: ScenarioDecoders.Movement(p.Payload)))
                .Select(m => (m.View.Mover, new Packet(m.Opcode, Block(m.View.Info))))
                .ToList();
            result = new Walked(
                seen.Where(s => s.Mover == on.Guid.Value).Select(s => s.Packet).ToList(),
                seen.Where(s => s.Mover == off.Guid.Value).Select(s => s.Packet).ToList(),
                onDispatched, offDispatched, onEnd, offEnd);
        }));
        Assert.True(report.Passed, report.ToString());
        return result!;
    }

    private static byte[] Block(MovementInfo info)
    {
        var writer = new PacketWriter(64);
        info.Write(writer);
        return writer.ToArray();
    }
}
