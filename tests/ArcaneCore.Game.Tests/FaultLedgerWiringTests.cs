using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests;

/// <summary>The world's catch-and-log sites count into the process-wide fault ledger (unique exception types keep parallel tests apart).</summary>
public sealed class FaultLedgerWiringTests
{
    private sealed class WorldTickProbeException : Exception;

    private sealed class PacketProbeException : Exception;

    private sealed class ThrowingSession : IPlayerSession
    {
        public bool Kicked { get; private set; }

        public int AccountId => 1;

        public AccountSecurity Security => AccountSecurity.Player;

        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload)
        {
        }

        public void ProcessWorldPackets(Player player) => throw new PacketProbeException();

        public void Kick() => Kicked = true;

        public void OnLoggedOut()
        {
        }
    }

    private static FaultSnapshot? Find(string site, Type type)
        => FaultLedger.Shared.Snapshot().FirstOrDefault(s => s.Site == site && s.ExceptionType == type.FullName);

    [Fact]
    public void AFailingWorldTickHandler_IsCountedAtTheWorldtickSite_AndTheTickGoesOn()
    {
        using var world = TestWorld.CreateRuntime();
        var seen = new List<uint>();
        world.WorldTick += _ => throw new WorldTickProbeException();
        world.WorldTick += seen.Add;

        world.RunTick(10);
        world.RunTick(10);

        Assert.Equal([10u, 10u], seen);
        FaultSnapshot fault = Assert.IsType<FaultSnapshot>(Find("worldtick", typeof(WorldTickProbeException)));
        Assert.Equal(2, fault.Count);
    }

    [Fact]
    public void APacketHandlerFault_IsCountedAtThePacketHandlingSite_AndKicksThePlayer()
    {
        using var world = TestWorld.CreateRuntime();
        var session = new ThrowingSession();
        world.AddPlayer(TestWorld.CreatePlayer(1, 7, 5, session));

        world.RunTick(50);

        Assert.True(session.Kicked);
        Assert.NotNull(Find("packet-handling", typeof(PacketProbeException)));
    }
}
