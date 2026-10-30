using System.Reflection;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
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

        public int AccountId { get; } = accountId;

        public AccountSecurity Security => AccountSecurity.Player;

        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload)
        {
            if (ThrowOnSend)
            {
                throw new InvalidOperationException("send fault");
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

    [Fact]
    public void EnterSerialAndFlush_ReleasesTheGate_WhenTheFlushThrows()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        var sessionA = new SendSwitchSession(1);
        var sessionB = new SendSwitchSession(2);
        Player a = TestWorld.CreatePlayer(1, 0, 0, sessionA);
        Player b = TestWorld.CreatePlayer(2, 5, 0, sessionB);
        world.AddPlayer(a);
        world.AddPlayer(b);
        world.RunTick(50);
        Map map = a.Map!;

        FieldInfo phase = typeof(WorldRuntime).GetField("_parallelPhase", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object gate = typeof(WorldRuntime).GetField("_serialGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(world)!;
        phase.SetValue(world, true); // the parallel maps phase, entered from this thread alone
        try
        {
            map.BroadcastToObservers(a, WorldOpcode.SmsgStandstateUpdate, [1]); // outside the gate: held in the outbox
            Assert.Equal(1, map.OutboxCount);
            sessionB.ThrowOnSend = true;

            Assert.Throws<InvalidOperationException>(() => map.EnterSerialAndFlush());

            Assert.False(Monitor.IsEntered(gate), "the serial gate is still held after the flush threw");
            Assert.Equal(0, map.OutboxCount); // a packet that throws must not fail every later flush as well
        }
        finally
        {
            while (Monitor.IsEntered(gate))
            {
                Monitor.Exit(gate);
            }

            phase.SetValue(world, false);
        }
    }
}
