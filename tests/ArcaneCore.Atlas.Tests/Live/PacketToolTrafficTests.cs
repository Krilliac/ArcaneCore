using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Live;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Live;

/// <summary>The bounds of what a tap nobody reads may keep in Atlas (docs/areas/map-feed.md, "Packet tools").</summary>
public sealed class PacketToolTrafficTests
{
    private static JsonObject Packet(int hexChars) => new() { ["op"] = "packet", ["hex"] = new string('A', hexChars) };

    [Fact]
    public void Packets_AreBoundedByCount_AndByEstimatedBytes_WhicheverComesFirst()
    {
        var traffic = new PacketToolTraffic();
        for (int i = 0; i < PacketToolTraffic.MaxPackets + 10; i++)
        {
            traffic.Accept("packet", Packet(0));
        }

        JsonObject byCount = traffic.Drain(int.MaxValue);
        Assert.Equal(PacketToolTraffic.MaxPackets, byCount["packets"]!.AsArray().Count);
        Assert.Equal(10, byCount["droppedByAtlas"]!.GetValue<long>());

        traffic.Reset();
        int fed = PacketToolTraffic.MaxPackets;
        for (int i = 0; i < fed; i++)
        {
            traffic.Accept("packet", Packet(8192)); // a 4096-byte payload as hex
        }

        JsonObject byBytes = traffic.Drain(int.MaxValue);
        int kept = byBytes["packets"]!.AsArray().Count;
        Assert.InRange(kept, 1, PacketToolTraffic.MaxPackets - 1);
        Assert.True(kept * (2L * 8192) <= PacketToolTraffic.MaxBytes, $"{kept} packets of 16 KiB exceed the byte bound");
        Assert.Equal(fed - kept, byBytes["droppedByAtlas"]!.GetValue<long>());

        // Reading frees the bound again.
        Assert.True(traffic.Accept("packet", Packet(8192)));
        Assert.Single(traffic.Drain(int.MaxValue)["packets"]!.AsArray());
    }
}
