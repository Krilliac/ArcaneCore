using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.GridTerrain;

/// <summary>
/// Characterisation of Map observer delivery (<c>BroadcastToObservers</c>) and the visibility query radius around it. Pins who receives a
/// broadcast (every observer but the source and the optional exception, none for a watcher out of range, nothing for an unobserved
/// source) so the shared send loop and the radius helper can be factored without changing delivery.
/// </summary>
public sealed class MapObserverBroadcastCharacterisationTests
{
    private static readonly byte[] Payload = [0x01, 0x02, 0x03, 0xFF];

    private sealed record Scene(WorldRuntime World, Map Map, Player A, FakeSession SessionA, Player B, FakeSession SessionB,
        Player C, FakeSession SessionC, Player Far, FakeSession SessionFar, TestUnit Unit) : IDisposable
    {
        public void Dispose() => World.Dispose();
    }

    private static Scene Build()
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var sa = new FakeSession(1);
        var sb = new FakeSession(2);
        var sc = new FakeSession(3);
        var sf = new FakeSession(4);
        Player a = TestWorld.CreatePlayer(1, 0, 0, sa);
        Player b = TestWorld.CreatePlayer(2, 3, 0, sb);
        Player c = TestWorld.CreatePlayer(3, 0, 3, sc);
        Player far = TestWorld.CreatePlayer(4, 500, 0, sf);
        world.AddPlayer(a);
        world.AddPlayer(b);
        world.AddPlayer(c);
        world.AddPlayer(far);
        var unit = new TestUnit(1, 1, 1);
        map.AddObject(unit);
        map.Update(50);
        foreach (FakeSession s in new[] { sa, sb, sc, sf })
        {
            s.Clear();
        }

        return new Scene(world, map, a, sa, b, sb, c, sc, far, sf, unit);
    }

    private static int Received(FakeSession session)
    {
        int count = 0;
        foreach ((WorldOpcode opcode, byte[] payload) in session.Sent)
        {
            if (opcode == WorldOpcode.SmsgBuyFailed)
            {
                Assert.Equal(Payload, payload);
                count++;
            }
        }

        return count;
    }

    [Fact]
    public void BroadcastFromAUnit_ReachesEveryObserverInRange_AndNoOneOutOfRange()
    {
        using Scene s = Build();
        Assert.Contains(s.Unit.Guid, s.A.VisibleObjects);
        Assert.DoesNotContain(s.Unit.Guid, s.Far.VisibleObjects);

        s.Map.BroadcastToObservers(s.Unit, WorldOpcode.SmsgBuyFailed, Payload);

        Assert.Equal(1, Received(s.SessionA));
        Assert.Equal(1, Received(s.SessionB));
        Assert.Equal(1, Received(s.SessionC));
        Assert.Equal(0, Received(s.SessionFar));
    }

    [Fact]
    public void BroadcastWithAnException_SkipsOnlyThatObserver()
    {
        using Scene s = Build();

        s.Map.BroadcastToObservers(s.Unit, WorldOpcode.SmsgBuyFailed, Payload, s.B);

        Assert.Equal(1, Received(s.SessionA));
        Assert.Equal(0, Received(s.SessionB));
        Assert.Equal(1, Received(s.SessionC));
        Assert.Equal(0, Received(s.SessionFar));
    }

    [Fact]
    public void BroadcastFromAPlayer_NeverEchoesToTheSourceItself()
    {
        using Scene s = Build();

        s.Map.BroadcastToObservers(s.A, WorldOpcode.SmsgBuyFailed, Payload);

        Assert.Equal(0, Received(s.SessionA));
        Assert.Equal(1, Received(s.SessionB));
        Assert.Equal(1, Received(s.SessionC));
        Assert.Equal(0, Received(s.SessionFar));
    }

    [Fact]
    public void BroadcastFromAnUnobservedSource_SendsNothing()
    {
        using Scene s = Build();
        var stray = new TestUnit(2, 900, 900);

        s.Map.BroadcastToObservers(stray, WorldOpcode.SmsgBuyFailed, Payload);

        Assert.Equal(0, Received(s.SessionA) + Received(s.SessionB) + Received(s.SessionC) + Received(s.SessionFar));
    }

    [Fact]
    public void BroadcastToAnEmptyPayload_DeliversAnEmptyPacket()
    {
        using Scene s = Build();

        s.Map.BroadcastToObservers(s.Unit, WorldOpcode.SmsgBuyFailed, ReadOnlySpan<byte>.Empty);

        (WorldOpcode opcode, byte[] payload) = Assert.Single(s.SessionB.Sent, p => p.Opcode == WorldOpcode.SmsgBuyFailed);
        Assert.Equal(WorldOpcode.SmsgBuyFailed, opcode);
        Assert.Empty(payload);
    }

    [Fact]
    public void ObjectVisibilityViewers_AreThePlayersInRange_NotThePlayerFiveHundredYardsAway()
    {
        using Scene s = Build();

        List<Player> viewers = s.Map.ObjectVisibilityViewers(s.Unit);

        Assert.Contains(s.A, viewers);
        Assert.Contains(s.B, viewers);
        Assert.Contains(s.C, viewers);
        Assert.DoesNotContain(s.Far, viewers);
    }
}
