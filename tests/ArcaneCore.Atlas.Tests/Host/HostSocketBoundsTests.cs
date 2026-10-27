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
    public async Task ACommandFlood_NeverRunsMoreThanTheCapAtOnce_AndEachRefusedCommandIsAnswered()
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

        // An unknown op after the flood is a barrier: one connection is read in order, so its error follows every refusal.
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

        Assert.Equal(flood - cap, refused);
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

        // Past the budget the host stops answering and closes the socket.
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

        Assert.True(errors <= burst, $"{errors} answers for {flood} bad messages; the budget is {burst}");
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, ui.Socket.CloseStatus);
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
