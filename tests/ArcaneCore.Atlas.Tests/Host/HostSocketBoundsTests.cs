using System.Net.WebSockets;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Host;
using ArcaneCore.Atlas.Tests.Host.Public;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Host;

/// <summary>
/// Per-connection bounds of the UI socket: how many commands one connection may have running at once, how many bad messages it
/// is answered before the host closes it, and the count of events dropped from a slow connection's queue. Nothing here waits for
/// time to pass: commands are held on a gate and the error budget runs on a manual clock.
/// </summary>
public sealed class HostSocketBoundsTests
{
    private static JsonObject Command(int id, string name) => new() { ["op"] = "command", ["id"] = id, ["name"] = name, ["args"] = new JsonObject() };

    [Fact]
    public async Task ACommandFlood_NeverRunsMoreThanTheCapAtOnce_AndEachRefusedCommandIsAnsweredOrCounted()
    {
        int running = 0;
        int peak = 0;
        int started = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using HostRig rig = await HostRig.StartAsync(
            new AtlasServerOptions { PresenceMinInterval = TimeSpan.Zero, Time = new ManualTime() },
            register: context => context.Commands.Register(new CommandDefinition(
                "test.gate",
                "Holds until the test releases it, counting how many run at once.",
                [],
                async (_, _) =>
                {
                    int now = Interlocked.Increment(ref running);
                    int seen;
                    while (now > (seen = Volatile.Read(ref peak)) && Interlocked.CompareExchange(ref peak, now, seen) != seen)
                    {
                    }

                    Interlocked.Increment(ref started);
                    await release.Task.ConfigureAwait(false);
                    Interlocked.Decrement(ref running);
                    return new JsonObject();
                })));
        await using UiClient ui = await UiClient.ConnectAsync(rig);
        int cap = AtlasServer.MaxCommandsInFlight;
        int flood = cap + 8;
        for (int i = 1; i <= flood; i++)
        {
            await ui.SendAsync(Command(i, "test.gate"));
        }

        // An unknown op after the flood is a barrier: the receive loop handles one connection's messages in order, so the barrier's error is
        // sent after every refusal was answered or counted. SemaphoreSlim does not document FIFO for its waiters, so the barrier is only sent
        // once no refusal send is waiting at the gate; the error then cannot be overtaken by a queued refusal.
        UiConnection flooded = rig.Server!.Hub.Find(ui.ConnectionId)!;
        await Wait.UntilAsync(() => flooded.PendingRefusalSends == 0, "the refusal sends to finish");
        await ui.SendTextAsync("""{"op":"barrier"}""");
        int refused = 0;
        while (true)
        {
            JsonNode m = await ui.ReceiveAsync();
            if (m.Str("op") == "error")
            {
                break;
            }

            if (m.Str("op") == "result")
            {
                Assert.Equal(CommandException.Unavailable, m.Str("result.error.code"));
                refused++;
            }
        }

        // Only one refusal send is outstanding at a time, so a refusal that finds the previous one still sending is counted instead of answered.
        Assert.True(refused > 0, "no refusal was answered");
        Assert.Equal(flood - cap, refused + flooded.DroppedRefusals);
        await Wait.UntilAsync(() => Volatile.Read(ref started) == cap, "the capped commands to start");
        Assert.True(Volatile.Read(ref peak) <= cap, $"{Volatile.Read(ref peak)} commands ran at once; the cap is {cap}");

        release.SetResult();
        int done = 0;
        while (done < cap)
        {
            JsonNode m = await ui.ReceiveAsync();
            if (m.Str("op") == "result")
            {
                Assert.True(m.At("result.ok")!.GetValue<bool>(), m.ToJsonString());
                done++;
            }
        }

        Assert.Equal(cap, Volatile.Read(ref started));
    }

    [Fact]
    public async Task ABadMessageFlood_IsAnsweredABoundedNumberOfTimes_ThenTheSocketCloses()
    {
        var time = new ManualTime();
        await using HostRig rig = await HostRig.StartAsync(new AtlasServerOptions { PresenceMinInterval = TimeSpan.Zero, Time = time });
        await using UiClient ui = await UiClient.ConnectAsync(rig);
        int burst = AtlasServer.MessageErrorBurst;

        // Within the budget every bad message is answered and the connection stays usable; time refills the budget.
        for (int i = 0; i < burst; i++)
        {
            await ui.SendTextAsync(i % 2 == 0 ? "not json" : """{"op":"nope"}""");
            await ui.ReceiveUntilAsync(m => m.Str("op") == "error", "the answer to a bad message inside the budget");
        }

        time.Advance(AtlasServer.MessageErrorRefill);
        await ui.SendTextAsync("not json");
        await ui.ReceiveUntilAsync(m => m.Str("op") == "error", "the answer after the budget refilled");
        await ui.SendAsync(Command(7, "status"));
        await ui.ReceiveUntilAsync(m => m.Str("op") == "result" && m.Num("id") == 7, "the status result while inside the budget");

        // Past the budget the host stops answering and closes the socket: four refilled answers, then the close.
        // The manual clock makes the refill exact, and the close frame is sent through the same gate as the answers, after them, so the client reads
        // exactly the refilled number of answers before the close.
        int refilled = 4;
        time.Advance(AtlasServer.MessageErrorRefill * refilled);
        int flood = burst + 24;
        for (int i = 0; i < flood; i++)
        {
            try
            {
                await ui.SendTextAsync("not json");
            }
            catch (WebSocketException)
            {
                break;
            }
        }

        int errors = 0;
        while (await ui.ReceiveTextAsync(TimeSpan.FromSeconds(10)) is { } text)
        {
            // Events (the status command's own) may interleave; only answers count against the budget.
            if (JsonNode.Parse(text).Str("op") == "error")
            {
                errors++;
            }
        }

        Assert.Equal(refilled, errors);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, ui.Socket.CloseStatus);
    }

    [Fact]
    public async Task ThirtyRefusedCommandsDuringAStall_AreAnsweredOrCounted_AndDoNotCloseTheSocket()
    {
        int started = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using HostRig rig = await HostRig.StartAsync(
            new AtlasServerOptions { PresenceMinInterval = TimeSpan.Zero, Time = new ManualTime() },
            register: context => context.Commands.Register(new CommandDefinition(
                "test.stall",
                "Holds until the test releases it, as a stalled world thread.",
                [],
                async (_, _) =>
                {
                    Interlocked.Increment(ref started);
                    await release.Task.ConfigureAwait(false);
                    return new JsonObject();
                })));
        await using UiClient ui = await UiClient.ConnectAsync(rig);
        int cap = AtlasServer.MaxCommandsInFlight;
        for (int i = 1; i <= cap; i++)
        {
            await ui.SendAsync(Command(i, "test.stall"));
        }

        await Wait.UntilAsync(() => Volatile.Read(ref started) == cap, "the stalled commands to start");
        int clicks = 30;
        for (int i = 0; i < clicks; i++)
        {
            await ui.SendAsync(Command(100 + i, "test.stall"));
        }

        // As in the flood test, the barrier waits until no refusal send is queued, so its error is not overtaken (the gate's wake order is not documented).
        UiConnection connection = rig.Server!.Hub.Find(ui.ConnectionId)!;
        await Wait.UntilAsync(() => connection.PendingRefusalSends == 0, "the refusal sends to finish");
        await ui.SendTextAsync("""{"op":"barrier"}""");
        int refused = 0;
        while (true)
        {
            JsonNode m = await ui.ReceiveAsync();
            if (m.Str("op") == "error")
            {
                break;
            }

            if (m.Str("op") == "result")
            {
                Assert.Equal(CommandException.Unavailable, m.Str("result.error.code"));
                refused++;
            }
        }

        Assert.True(refused > 0, "no refusal was answered");
        Assert.Equal(clicks, refused + connection.DroppedRefusals);
        Assert.Equal(WebSocketState.Open, ui.Socket.State);

        release.SetResult();
        int done = 0;
        while (done < cap)
        {
            JsonNode m = await ui.ReceiveAsync();
            if (m.Str("op") == "result")
            {
                Assert.True(m.At("result.ok")!.GetValue<bool>(), m.ToJsonString());
                done++;
            }
        }
    }

    [Fact]
    public async Task ARefusalFloodWhileSendsAreStalled_KeepsOneRefusalPending_DropsTheRest_AndLeavesTheSocketOpen()
    {
        int started = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using HostRig rig = await HostRig.StartAsync(
            new AtlasServerOptions { PresenceMinInterval = TimeSpan.Zero, Time = new ManualTime() },
            register: context => context.Commands.Register(new CommandDefinition(
                "test.hold",
                "Holds until the test releases it.",
                [],
                async (_, _) =>
                {
                    Interlocked.Increment(ref started);
                    await release.Task.ConfigureAwait(false);
                    return new JsonObject();
                })));
        await using UiClient ui = await UiClient.ConnectAsync(rig);
        UiConnection connection = rig.Server!.Hub.Find(ui.ConnectionId)!;
        int cap = AtlasServer.MaxCommandsInFlight;
        for (int i = 1; i <= cap; i++)
        {
            await ui.SendAsync(Command(i, "test.hold"));
        }

        await Wait.UntilAsync(() => Volatile.Read(ref started) == cap, "the held commands to start");

        // Stall every send on the connection by holding its send gate, then flood past the cap.
        await connection.SendGate.WaitAsync();
        int clicks = 40;
        try
        {
            for (int i = 0; i < clicks; i++)
            {
                await ui.SendAsync(Command(100 + i, "test.hold"));
            }

            // The first refusal waits for the gate and holds the single slot; every later one is dropped and counted.
            await Wait.UntilAsync(() => connection.DroppedRefusals == clicks - 1, "the surplus refusals to be dropped");
            Assert.Equal(1, connection.PendingRefusalSends);
            Assert.True(connection.DroppedRefusals > 0);
            Assert.Equal(WebSocketState.Open, ui.Socket.State);
        }
        finally
        {
            connection.SendGate.Release();
        }

        // Once sends resume, exactly the one pending refusal is answered and the slot is free again.
        JsonNode answer = await ui.ReceiveUntilAsync(m => m.Str("op") == "result", "the one pending refusal");
        Assert.Equal(CommandException.Unavailable, answer.Str("result.error.code"));
        await Wait.UntilAsync(() => connection.PendingRefusalSends == 0, "the refusal slot to free");
        Assert.Equal(WebSocketState.Open, ui.Socket.State);
        Assert.Equal(clicks - 1, connection.DroppedRefusals);

        release.SetResult();
    }

    [Fact]
    public void AStalledConsumer_GetsAtMostOneDroppedEventsWarningPerWindow_CarryingTheAccumulatedCount()
    {
        long now = 1_000;
        using var socket = new ClientWebSocket();
        var connection = new UiConnection(socket, "0123456789ab", 1, () => now);
        int capacity = UiConnection.EventQueueCapacity;
        for (int i = 0; i < capacity + 3; i++)
        {
            connection.EnqueueEvent("{}");
        }

        Assert.True(connection.TryReportDroppedEvents(out long first));
        Assert.Equal(3, first);

        // Still inside the window: more drops, and the pump asks after every send, but no further line is admitted.
        for (int i = 0; i < 5; i++)
        {
            connection.EnqueueEvent("{}");
            Assert.False(connection.TryReportDroppedEvents(out _));
        }

        now += (long)UiConnection.DroppedEventsLogInterval.TotalMilliseconds;
        Assert.True(connection.TryReportDroppedEvents(out long second));
        Assert.Equal(5, second);
        Assert.False(connection.TryReportDroppedEvents(out _));
    }

    [Fact]
    public void ASlowConnectionsFullEventQueue_CountsEveryDroppedEvent()
    {
        using var socket = new ClientWebSocket();
        var connection = new UiConnection(socket, "0123456789ab", 1);
        int overflow = 3;
        for (int i = 0; i < UiConnection.EventQueueCapacity + overflow; i++)
        {
            connection.EnqueueEvent($$"""{"n":{{i}}}""");
        }

        Assert.Equal(overflow, connection.TakeDroppedEvents());
        Assert.Equal(0, connection.TakeDroppedEvents());
        Assert.True(connection.PendingEvents.TryRead(out string? first));
        Assert.Equal($$"""{"n":{{overflow}}}""", first);
    }
}
