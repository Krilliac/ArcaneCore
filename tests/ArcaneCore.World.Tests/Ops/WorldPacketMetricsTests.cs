using ArcaneCore.Kernel.Ops.Metrics;
using ArcaneCore.Protocol;
using ArcaneCore.World.Ops.Metrics;
using Xunit;

namespace ArcaneCore.World.Tests.Ops;

/// <summary>
/// The world per-opcode table (docs/ops/metrics.md, "Per-opcode traffic"). Read-only: the table is process-wide and shared by
/// every test that opens a session, so exact counts are asserted on private tables in the Kernel tests instead.
/// </summary>
public sealed class WorldPacketMetricsTests
{
    [Fact]
    public void Table_LabelsEveryWorldOpcodeWithItsReferenceName()
    {
        Assert.Equal("world", WorldPacketMetrics.Table.Protocol);
        foreach (WorldOpcode opcode in Enum.GetValues<WorldOpcode>().Distinct())
        {
            string name = WorldOpcodeNames.GetName(opcode);
            Assert.Equal(name, WorldPacketMetrics.Table.LabelOf((uint)opcode));
            Assert.NotEqual(OpcodeTable.UnknownLabel, name);
        }

        Assert.Equal("CMSG_PING", WorldPacketMetrics.Table.LabelOf((uint)WorldOpcode.CmsgPing));
        Assert.DoesNotContain(826, Enum.GetValues<WorldOpcode>().Select(o => (int)o)); // unassigned (vmangos Opcodes_1_12_1.h:825-828)
        Assert.All(Enum.GetValues<WorldOpcode>(), o => Assert.InRange((int)o, 0, 827));
    }

    [Theory]
    [InlineData(826u)]    // the one unassigned value below NUM_MSG_TYPES (vmangos Opcodes_1_12_1.h:825-828)
    [InlineData(828u)]    // NUM_MSG_TYPES itself
    [InlineData(0xFFFFu)]
    public void Table_CountsTheOpcodeGapAndOutOfRangeAsUnknown(uint opcode)
    {
        Assert.Equal(OpcodeTable.UnknownLabel, WorldPacketMetrics.Table.LabelOf(opcode));
    }
}
