using System.Net.WebSockets;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Host.Public;
using ArcaneCore.Atlas.Live;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Host.Public;

/// <summary>
/// The whole in-process path (design section 5, lane D2 acceptance): a fake world feed pushes frames into the listener's own feed pool, the pool
/// fills the frame store, and a browser-style WebSocket receives what the guest projection lets through, within the per-address byte budget.
/// </summary>
public sealed class PublicEndToEndTests
{
    private const ulong ZedGuid = 0x1234_5678_90AB_CDEF;
    private const ulong PetGuid = 500;
    private const ulong BoarGuid = 501;

    private static readonly Action<PublicOptions> InProcess = o =>
    {
        o.Feed.WorldActionsEnabled = false;
        o.Feed.LiveServer = false;
        o.Feed.WorldMaxConnections = 3;
        o.Guest.MaxBytesPerSecond = 4096;
        o.Guest.FeedHz = 2;
    };

    private static async Task<bool> WaitAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < end)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return condition();
    }

    private static LiveFrame Frame(long seq)
        => Frames.Frame(
            seq,
            Frames.Entity(ZedGuid, LiveKind.Player, "Zed", 4441.5, 5551.5),
            Frames.Entity(PetGuid, LiveKind.Creature, "Wolf", 7771.5, 8881.5, entry: 17),
            Frames.Entity(BoarGuid, LiveKind.Creature, "Boar", 300.25, 400.75, motion: "idle", entry: 5));

    private static LiveFrame ManyBoars(long seq, int count)
        => Frames.Frame(seq, [.. Enumerable.Range(0, count).Select(i => Frames.Entity(1000UL + (ulong)i, LiveKind.Creature, "Dire Boar Number " + i, 100 + i, 200 + i, entry: 5))]);

    [Fact]
    public async Task FramesFromAFakeFeed_ReachAGuestSocketProjected_AndAnOversizedFrameIsDroppedNotQueued()
    {
        var time = new ManualTime();
        var feed = new FakeFeed { Linked = [PetGuid] };
        await using PublicRig rig = await PublicRig.StartAsync(InProcess, connector: feed, mode: PublicProcessMode.InProcess, time: time);
        Visitor visitor = await rig.VisitAsync();
        using ClientWebSocket socket = await rig.ConnectAsync(visitor);
        await socket.SendJsonAsync(new JsonObject { ["op"] = "auth", ["csrf"] = visitor.Csrf });
        Assert.NotNull(await socket.ReceiveUntilAsync("hello"));

        Assert.True(await WaitAsync(() => feed.Opened.Any(c => c.Ops.Contains("subscribe"))), "the socket's map was never subscribed on a pooled feed connection");
        FakeConnection connection = feed.Opened.First(c => c.Ops.Contains("subscribe"));
        Assert.Equal("role", connection.Ops.First()); // the pooled connection took its role before anything else

        // Frames arrive from the world; the guest projection removes the real player and the pet linked to a real player.
        connection.PushFrame(Frame(1));
        time.Advance(TimeSpan.FromSeconds(1));
        connection.PushFrame(Frame(2));
        var seen = new List<string>();
        for (int i = 0; i < 2; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            connection.PushFrame(Frame(3 + i));
        }

        JsonNode? boarFrame = null;
        DateTime end = DateTime.UtcNow.AddSeconds(10);
        while (boarFrame is null && DateTime.UtcNow < end)
        {
            JsonNode? next = await socket.ReceiveUntilAsync("event", "live.frame");
            seen.Add(next!.ToJsonString());
            if (seen[^1].Contains("\"Boar\"", StringComparison.Ordinal))
            {
                boarFrame = next;
            }
        }

        Assert.NotNull(boarFrame);
        foreach (string text in seen)
        {
            Assert.DoesNotContain("Zed", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Wolf", text, StringComparison.Ordinal);
            Assert.DoesNotContain("4441.5", text, StringComparison.Ordinal);
            Assert.DoesNotContain("7771.5", text, StringComparison.Ordinal);
            Assert.DoesNotContain("ABCDEF", text, StringComparison.OrdinalIgnoreCase);
        }

        // The byte budget: a frame the address cannot pay for is counted dropped and never arrives, even later.
        connection.PushFrame(Frame(99)); // the world's links answer must be fresh (within two frames) or every creature would be withheld
        long dropped = rig.Runtime.Stats.FramesDropped;
        time.Advance(TimeSpan.FromSeconds(5));
        connection.PushFrame(ManyBoars(100, 80));
        Assert.True(await WaitAsync(() => rig.Runtime.Stats.FramesDropped == dropped + 1), "the oversized frame was not counted as dropped");

        time.Advance(TimeSpan.FromSeconds(5));
        connection.PushFrame(Frame(101));
        var seqs = new List<long>();
        while (seqs.Count == 0 || seqs[^1] < 101)
        {
            JsonNode frame = (await socket.ReceiveUntilAsync("event", "live.frame"))!;
            seqs.Add(frame["data"]!["seq"]!.GetValue<long>());
        }

        Assert.Equal(101, seqs[^1]);
        Assert.DoesNotContain(100L, seqs); // the dropped frame never arrives, now or later
        Assert.Equal(dropped + 1, rig.Runtime.Stats.FramesDropped);
    }
}
