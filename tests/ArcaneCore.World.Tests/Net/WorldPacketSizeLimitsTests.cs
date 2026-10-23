using ArcaneCore.World.Net;
using Xunit;

namespace ArcaneCore.World.Tests.Net;

/// <summary>
/// Pins the world-socket packet size limits (NetGuard pre-allocation DoS guard). They are
/// referenced from Gateway, GatewayWorkerTransport and [InlineData], so they must stay
/// compile-time constants with exactly these values however they are declared.
/// </summary>
public sealed class WorldPacketSizeLimitsTests
{
    [Fact]
    public void MaxClientPacketSize_IsVmangosLimit()
    {
        // vmangos WorldSocket::handle_input_header rejects sizes outside [4, 0x2800].
        Assert.Equal(0x2800, WorldSession.MaxClientPacketSize);
        Assert.Equal(10240, WorldSession.MaxClientPacketSize);
    }

    [Fact]
    public void MaxServerPayload_LeavesRoomForOpcodeInSixteenBitSize()
    {
        // The SMSG size field is 16 bits and counts the 2 opcode bytes.
        Assert.Equal(ushort.MaxValue - 2, WorldSession.MaxServerPayload);
        Assert.Equal(65533, WorldSession.MaxServerPayload);
        Assert.Equal(ushort.MaxValue, WorldSession.MaxServerPayload + 2);
    }

    [Theory]
    [InlineData(WorldSession.MaxClientPacketSize)]
    [InlineData(WorldSession.MaxServerPayload)]
    public void Limits_AreCompileTimeConstants(int value)
    {
        // [InlineData] only compiles if both are const; this guards the forwarding-const requirement.
        Assert.True(value > 4);
    }
}
