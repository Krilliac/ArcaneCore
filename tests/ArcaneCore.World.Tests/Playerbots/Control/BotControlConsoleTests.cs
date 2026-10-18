using System.Text.Json.Nodes;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Tests.Playerbots.Party;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Control;

/// <summary>
/// <c>.botctl</c> from the server console (docs/ops/botctl.md): take a bot over, query it as JSON, walk it, script it, release it;
/// failures are machine-readable (<see cref="CommandContext.Failure"/>, the reason RA reports as <c>#done error &lt;reason&gt;</c>).
/// </summary>
public sealed class BotControlConsoleTests
{
    /// <summary>Run a console line and wait until the command settled (botctl verbs answer later, off the world thread).</summary>
    private static async Task<(string[] Lines, string? Failure)> ExecAsync(FleetWorld world, string line)
    {
        int skip = world.Console.Lines.Length;
        CommandContext? context = await world.Host.World.InvokeAsync(() => world.Console.Executor.Run(line));
        Assert.NotNull(context);
        Task settled = context!.WhenSettled;
        Assert.Same(settled, await Task.WhenAny(settled, Task.Delay(TimeSpan.FromSeconds(40))));
        return ([.. world.Console.Lines.Skip(skip)], context.Failure);
    }

    private static JsonObject JsonLine(string[] lines) => Assert.IsType<JsonObject>(JsonNode.Parse(Assert.Single(lines, l => l.StartsWith('{'))));

    [Fact]
    public async Task Take_State_Release_RoundTrip()
    {
        await using FleetWorld world = await FleetWorld.StartAsync();
        await world.AddBotAsync("Botcaa", 10);

        (string[] take, string? takeFailure) = await ExecAsync(world, ".botctl take Botcaa");
        Assert.Null(takeFailure);
        Assert.Contains(take, l => l.StartsWith("OK take (1/1 bots ok)", StringComparison.Ordinal));

        (string[] state, string? stateFailure) = await ExecAsync(world, ".botctl state Botcaa --json");
        Assert.Null(stateFailure);
        JsonObject json = JsonLine(state);
        Assert.True(json["ok"]!.GetValue<bool>());
        JsonObject bot = json["bots"]!.AsArray()[0]!.AsObject();
        Assert.Equal("Botcaa", bot["bot"]!.GetValue<string>());
        JsonObject data = bot["data"]!.AsObject();
        Assert.True(data["controlled"]!.GetValue<bool>());
        Assert.True(data["alive"]!.GetValue<bool>());
        Assert.Equal(FleetWorld.BaseX + 10, data["x"]!.GetValue<float>(), 1);

        (_, string? releaseFailure) = await ExecAsync(world, ".botctl release Botcaa");
        Assert.Null(releaseFailure);
        (string[] after, _) = await ExecAsync(world, ".botctl state Botcaa --json");
        Assert.False(JsonLine(after)["bots"]!.AsArray()[0]!["data"]!["controlled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Failures_AreMachineReadable()
    {
        await using FleetWorld world = await FleetWorld.StartAsync();
        await world.AddBotAsync("Botcba", 10);

        (string[] nobody, string? nobodyFailure) = await ExecAsync(world, ".botctl take Nobodyhere");
        Assert.Equal("no-match", nobodyFailure);
        Assert.Contains(nobody, l => l.StartsWith("ERR no-match take", StringComparison.Ordinal));

        (_, string? notControlled) = await ExecAsync(world, ".botctl cast Botcba 133");
        Assert.Equal("not-controlled", notControlled);

        (_, string? badFilter) = await ExecAsync(world, ".botctl list where:colour=red");
        Assert.Equal("bad-selector", badFilter);

        (_, string? syntax) = await ExecAsync(world, ".botctl move");
        Assert.Equal("syntax", syntax);

        (_, string? unknown) = await ExecAsync(world, ".botctl frobnicate Botcba");
        Assert.Equal("unknown-subcommand", unknown);

        (_, string? timeout) = await ExecAsync(world, ".botctl wait Botcba dead timeout 0.3");
        Assert.Equal("timeout", timeout);
    }

    [Fact]
    public async Task Run_Sequence_StopsAtTheFirstFailure()
    {
        await using FleetWorld world = await FleetWorld.StartAsync();
        await world.AddBotAsync("Botcca", 10);

        (string[] ok, string? okFailure) = await ExecAsync(world, ".botctl run Botcca take defensive ; mode passive ; wait alive and nocombat timeout 5 ; state ; release");
        Assert.Null(okFailure);
        Assert.Contains(ok, l => l.StartsWith("OK run", StringComparison.Ordinal) && l.Contains("5/5 steps ok", StringComparison.Ordinal));

        (string[] bad, string? badFailure) = await ExecAsync(world, ".botctl run Botcca take ; cast 0 ; release");
        Assert.Equal("step-failed", badFailure);
        Assert.Contains(bad, l => l.Contains("step 2", StringComparison.Ordinal));
        (string[] state, _) = await ExecAsync(world, ".botctl state Botcca --json");
        Assert.True(JsonLine(state)["bots"]!.AsArray()[0]!["data"]!["controlled"]!.GetValue<bool>());
        await ExecAsync(world, ".botctl release Botcca");
    }

    [Fact]
    public async Task Move_WalksToThePoint_AndWaitSeesArrival()
    {
        await using FleetWorld world = await FleetWorld.StartAsync();
        await PartyTestHost.InstallFlatGroundAsync(world.Host, FleetWorld.BaseZ);
        await world.AddBotAsync("Botcda", 10);

        float x = FleetWorld.BaseX + 18;
        (_, string? takeFailure) = await ExecAsync(world, ".botctl take Botcda");
        Assert.Null(takeFailure);
        (string[] move, string? moveFailure) = await ExecAsync(world, FormattableString.Invariant($".botctl move Botcda {x} {FleetWorld.BaseY} {FleetWorld.BaseZ}"));
        Assert.True(moveFailure is null, string.Join(" | ", move));
        (string[] wait, string? waitFailure) = await ExecAsync(world, ".botctl wait Botcda arrived timeout 30");
        Assert.True(waitFailure is null, string.Join(" | ", wait));
        (float X, float Y, float Z) at = await world.PositionAsync("Botcda");
        Assert.InRange(at.X, x - 2.5f, x + 2.5f);
        await ExecAsync(world, ".botctl release Botcda");
    }

    [Fact]
    public async Task Events_RecordTheServersRefusal()
    {
        await using FleetWorld world = await FleetWorld.StartAsync();
        await world.AddBotAsync("Botcea", 10);
        await ExecAsync(world, ".botctl take Botcea");

        // A spell the bot does not know: the server refuses it (SMSG_CAST_RESULT), and the refusal is the command's result.
        (string[] cast, string? castFailure) = await ExecAsync(world, ".botctl cast Botcea 10 self");
        Assert.True(castFailure is "server-error" or "refused" or "ignored", castFailure + ": " + string.Join(" | ", cast));
        (string[] errors, _) = await ExecAsync(world, ".botctl errors Botcea --json");
        Assert.NotEmpty(JsonLine(errors)["bots"]!.AsArray()[0]!["data"]!["events"]!.AsArray());
        (string[] events, _) = await ExecAsync(world, ".botctl events Botcea kind action --json");
        Assert.Contains(JsonLine(events)["bots"]!.AsArray()[0]!["data"]!["events"]!.AsArray(), e => e!["text"]!.GetValue<string>().Contains("CMSG_CAST_SPELL", StringComparison.Ordinal));
        await ExecAsync(world, ".botctl release Botcea");
    }
}