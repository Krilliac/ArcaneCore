using System.Diagnostics.Metrics;
using ArcaneCore.Kernel.Ops.Metrics;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Ops;

/// <summary>Per-opcode traffic accounting (docs/ops/metrics.md, "Per-opcode traffic"): labels, the unknown bucket, validation and export.</summary>
public sealed class OpcodeTrafficTests
{
    private static string UniqueMeter() => "ArcaneCore.Test." + Guid.NewGuid().ToString("N");

    private static MeterListener Listen(Meter meter)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (ReferenceEquals(instrument.Meter, meter))
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.Start();
        return listener;
    }

    private static KeyValuePair<int, string>[] Known() =>
    [
        new(0, "T_ZERO"),
        new(2, "T_TWO"),
        new(10, "T_TEN"),
    ];

    [Fact]
    public void Table_CountsKnownOpcodesUnderTheirNames()
    {
        using var meter = new Meter(UniqueMeter());
        var traffic = new OpcodeTrafficMeter(meter);
        OpcodeTable table = traffic.Register("t", Known());
        using MeterListener listener = Listen(meter);

        table.RecordIn(2, 10);
        table.RecordIn(2, 5);
        table.RecordOut(2, 7);
        table.RecordIn(0, 1);

        Assert.Equal("T_TWO", table.LabelOf(2));
        Assert.Equal(new OpcodeCounts(2, 15, 1, 7), table.Read(2));
        Assert.Equal(new OpcodeCounts(1, 1, 0, 0), table.Read(0));
        Assert.Equal(default, table.Read(10));
        Assert.Equal(default, table.Read(1)); // the gap's slot is the unknown slot, untouched
    }

    [Theory]
    [InlineData(1u)]          // a gap between registered values
    [InlineData(11u)]         // just past the table
    [InlineData(0xFFFFu)]
    [InlineData(uint.MaxValue)]
    public void Table_CountsUnknownOpcodesInOneBucket(uint opcode)
    {
        using var meter = new Meter(UniqueMeter());
        var traffic = new OpcodeTrafficMeter(meter);
        OpcodeTable table = traffic.Register("t", Known());
        using MeterListener listener = Listen(meter);

        table.RecordIn(opcode, 3);
        table.RecordOut(opcode, 4);

        Assert.Equal(OpcodeTable.UnknownLabel, table.LabelOf(opcode));
        // Every unknown value reads the same slot, whichever value was recorded.
        foreach (uint other in new uint[] { 1, 11, 0xFFFF, uint.MaxValue })
        {
            Assert.Equal(new OpcodeCounts(1, 3, 1, 4), table.Read(other));
        }

        Assert.Equal(default, table.Read(2));
    }

    [Fact]
    public void Table_ContinuationBytesAddBytesWithoutPackets()
    {
        using var meter = new Meter(UniqueMeter());
        var traffic = new OpcodeTrafficMeter(meter);
        OpcodeTable table = traffic.Register("t", Known());
        using MeterListener listener = Listen(meter);

        table.RecordIn(10, 1);
        table.RecordInBytes(10, 4);
        table.RecordInBytes(10, 20);

        Assert.Equal(new OpcodeCounts(1, 25, 0, 0), table.Read(10));
    }

    [Fact]
    public void Table_WithoutAListener_RecordsNothing()
    {
        using var meter = new Meter(UniqueMeter());
        var traffic = new OpcodeTrafficMeter(meter);
        OpcodeTable table = traffic.Register("t", Known());

        Assert.False(traffic.Enabled);
        table.RecordIn(2, 10);
        table.RecordInBytes(2, 10);
        table.RecordOut(2, 10);

        Assert.Equal(default, table.Read(2));
    }

    [Fact]
    public void Register_RejectsDuplicateProtocolValueAndInvalidNames()
    {
        using var meter = new Meter(UniqueMeter());
        var traffic = new OpcodeTrafficMeter(meter);
        traffic.Register("t", Known());

        Assert.Throws<ArgumentException>(() => traffic.Register("t", Known()));
        Assert.Throws<ArgumentException>(() => traffic.Register("", Known()));
        Assert.Throws<ArgumentException>(() => traffic.Register("a", [new(1, "A"), new(1, "B")]));
        Assert.Throws<ArgumentException>(() => traffic.Register("b", [new(1, "")]));
        Assert.Throws<ArgumentException>(() => traffic.Register("c", [new(1, "unknown")]));
        Assert.Throws<ArgumentException>(() => traffic.Register("d", [new(65536, "BIG")]));
        Assert.Throws<ArgumentException>(() => traffic.Register("e", [new(-1, "NEG")]));

        // A rejected registration leaves no protocol behind.
        OpcodeTable ok = traffic.Register("a", [new(1, "A")]);
        Assert.Equal("a", ok.Protocol);
    }

    [Fact]
    public void Store_ExportsOnlyNonZeroOpcodeSeriesWithProtocolAndOpcodeLabels()
    {
        string meterName = UniqueMeter();
        using var meter = new Meter(meterName);
        var traffic = new OpcodeTrafficMeter(meter);
        OpcodeTable table = traffic.Register("t", [new(1, "CMSG_X"), new(2, "CMSG_Y"), new(3, "CMSG_Z")]);
        using var store = new MetricsStore();
        store.Start();

        table.RecordIn(1, 6);
        table.RecordIn(1, 4);
        table.RecordOut(2, 9);
        table.RecordIn(99, 1);

        string text = PrometheusFormatter.Render([.. store.Collect().Where(m => m.MeterName == meterName)]);

        Assert.Contains("arcanecore_net_opcode_packets_in_total{protocol=\"t\",opcode=\"CMSG_X\"} 2\n", text);
        Assert.Contains("arcanecore_net_opcode_bytes_in_bytes_total{protocol=\"t\",opcode=\"CMSG_X\"} 10\n", text);
        Assert.Contains("arcanecore_net_opcode_packets_out_total{protocol=\"t\",opcode=\"CMSG_Y\"} 1\n", text);
        Assert.Contains("arcanecore_net_opcode_bytes_out_bytes_total{protocol=\"t\",opcode=\"CMSG_Y\"} 9\n", text);
        Assert.Contains("arcanecore_net_opcode_packets_in_total{protocol=\"t\",opcode=\"unknown\"} 1\n", text);
        Assert.DoesNotContain("CMSG_Z", text); // a zero slot has no series
        Assert.DoesNotContain("opcode=\"CMSG_Y\"} 0", text);
        Assert.DoesNotContain("packets_out_total{protocol=\"t\",opcode=\"CMSG_X\"", text);
    }
}
