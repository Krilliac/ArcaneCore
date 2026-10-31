using System.Reflection;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Maps;

/// <summary>
/// <see cref="Map.EnterSerialAndFlush"/> takes the world's serial gate and then flushes the packets the map held back. If a flush throws
/// the caller never receives the scope, so the gate has to be released there: otherwise the thread keeps it for good and every other map
/// thread blocks on it at the next parallel tick.
/// </summary>
public sealed class MapGateFlushFaultTests
{
    private sealed class SendSwitchSession(int accountId) : IPlayerSession
    {
        public bool ThrowOnSend { get; set; }

        public int Received { get; private set; }

        public int AccountId { get; } = accountId;

        public AccountSecurity Security => AccountSecurity.Player;

        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload)
        {
            if (ThrowOnSend)
            {
                throw new InvalidOperationException("send fault");
            }

            if (opcode == WorldOpcode.SmsgStandstateUpdate)
            {
                Received++;
            }
        }

        public void ProcessWorldPackets(Player player)
        {
        }

        public void Kick()
        {
        }

        public void OnLoggedOut()
        {
        }
    }

    private static long SiteCount(string site)
        => FaultLedger.Shared.Snapshot().Where(f => f.Site == site).Sum(f => f.Count);

    private static (WorldRuntime World, Map Map, Player Source, SendSwitchSession[] Observers) Setup()
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        var sessions = new[] { new SendSwitchSession(1), new SendSwitchSession(2), new SendSwitchSession(3), new SendSwitchSession(4) };
        Player a = TestWorld.CreatePlayer(1, 0, 0, sessions[0]);
        world.AddPlayer(a);
        for (int i = 1; i < sessions.Length; i++)
        {
            world.AddPlayer(TestWorld.CreatePlayer((uint)(i + 1), 5 * i, 0, sessions[i]));
        }

        world.RunTick(50);
        return (world, a.Map!, a, sessions[1..]);
    }

    private static void ReleaseGate(WorldRuntime world, bool phaseWasSet)
    {
        object gate = typeof(WorldRuntime).GetField("_serialGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(world)!;
        while (Monitor.IsEntered(gate))
        {
            Monitor.Exit(gate);
        }

        if (phaseWasSet)
        {
            typeof(WorldRuntime).GetField("_parallelPhase", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(world, false);
        }
    }

    [Fact]
    public void ABroadcast_WithAThrowingObserver_StillReachesTheOtherObservers_AndIsRecordedOnce()
    {
        (WorldRuntime world, Map map, Player a, SendSwitchSession[] observers) = Setup();
        using (world)
        {
            observers[0].ThrowOnSend = true;
            long before = SiteCount("map.observer-send");

            map.BroadcastToObservers(a, WorldOpcode.SmsgStandstateUpdate, [1]);

            Assert.Equal(1, observers[1].Received);
            Assert.Equal(1, observers[2].Received);
            Assert.Equal(1, SiteCount("map.observer-send") - before);
        }
    }

    [Fact]
    public void AHeldGateFlush_WithAThrowingObserver_DeliversTheRest_EmptiesTheOutbox_AndDoesNotThrowAgain()
    {
        (WorldRuntime world, Map map, Player a, SendSwitchSession[] observers) = Setup();
        using (world)
        {
            FieldInfo phase = typeof(WorldRuntime).GetField("_parallelPhase", BindingFlags.Instance | BindingFlags.NonPublic)!;
            FieldInfo holding = typeof(Map).GetField("_holdingGate", BindingFlags.Instance | BindingFlags.NonPublic)!;
            phase.SetValue(world, true);
            try
            {
                map.BroadcastToObservers(a, WorldOpcode.SmsgStandstateUpdate, [1]);
                map.BroadcastToObservers(a, WorldOpcode.SmsgStandstateUpdate, [2]);
                Assert.Equal(2, map.OutboxCount);
                observers[0].ThrowOnSend = true;
                holding.SetValue(map, true);
                long before = SiteCount("map.observer-send");

                map.EnterSerialAndFlush();

                Assert.Equal(0, map.OutboxCount);
                Assert.Equal(2, observers[1].Received);
                Assert.Equal(2, observers[2].Received);
                Assert.Equal(2, SiteCount("map.observer-send") - before); // one record per faulting send, none duplicated by a later layer
                map.EnterSerialAndFlush(); // nothing left to fail
                Assert.Equal(2, observers[1].Received);
            }
            finally
            {
                holding.SetValue(map, false);
                ReleaseGate(world, phaseWasSet: true);
            }
        }
    }

    [Fact]
    public void AFreshGateFlush_WithAThrowingObserver_DoesNotThrow_DeliversTheRest_AndRecordsOnceInTheLedger()
    {
        (WorldRuntime world, Map map, Player a, SendSwitchSession[] observers) = Setup();
        using (world)
        {
            FieldInfo phase = typeof(WorldRuntime).GetField("_parallelPhase", BindingFlags.Instance | BindingFlags.NonPublic)!;
            phase.SetValue(world, true);
            try
            {
                map.BroadcastToObservers(a, WorldOpcode.SmsgStandstateUpdate, [1]);
                Assert.Equal(1, map.OutboxCount);
                observers[0].ThrowOnSend = true;
                long sendBefore = SiteCount("map.observer-send");
                long flushBefore = SiteCount("map.gate-flush");

                using (map.EnterSerialAndFlush())
                {
                }

                Assert.Equal(0, map.OutboxCount);
                Assert.Equal(1, observers[1].Received);
                Assert.Equal(1, observers[2].Received);
                Assert.Equal(1, SiteCount("map.observer-send") - sendBefore);
                Assert.Equal(0, SiteCount("map.gate-flush") - flushBefore);
            }
            finally
            {
                ReleaseGate(world, phaseWasSet: true);
            }
        }
    }
}
