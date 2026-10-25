using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Live;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Live;

/// <summary>The binding of the dev UI's daemon seam (<see cref="IFeedRequester"/>) to the live connection.</summary>
public sealed class LiveFeedRequesterTests
{
    private static async Task AnswerCapsAsync(FakeFeedServer daemon, int? settleSeconds)
    {
        JsonObject caps = await daemon.ReadAsync();
        Assert.Equal("caps", caps["op"]!.GetValue<string>());
        var fields = new List<(string, JsonNode?)> { ("actions", true) };
        if (settleSeconds is { } s)
        {
            fields.Add(("settleSeconds", s));
        }

        await daemon.WriteAsync(Wire.Result(caps["id"]!.GetValue<long>(), [.. fields]));
    }

    [Theory]
    [InlineData(1, 2 * (10 + 2) + 15)]
    [InlineData(30, 2 * (10 + 60) + 15)]
    [InlineData(600, 2 * (10 + 1200) + 15)]
    [InlineData(0, 2 * (10 + 1200) + 15)]
    [InlineData(601, 2 * (10 + 1200) + 15)]
    public void TheActionWait_IsTheInvokePlusTwoSettlesAndAQueuedLine_PlusAMargin(int settleSeconds, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), LiveFeedRequester.ActionWaitFor(settleSeconds));
    }

    [Fact]
    public void WithoutAnAdvertisedSettle_TheActionWaitUsesTheMaximum()
    {
        Assert.Equal(LiveFeedRequester.ActionWaitFor(600), LiveFeedRequester.ActionWaitFor(null));
    }

    [WindowsFact]
    public async Task TheActionWait_IsDerivedFromTheSettleSecondsCapsAdvertises_AndAskedOncePerConnection()
    {
        await using var rig = new LiveRig();
        FakeFeedServer daemon = await rig.ConnectAsync();
        var requester = new LiveFeedRequester(rig.Feed);

        Task<TimeSpan> first = requester.ActionWaitAsync(CancellationToken.None);
        await AnswerCapsAsync(daemon, 7);
        TimeSpan wait = await first.WaitAsync(FakeFeedServer.Patience);
        TimeSpan again = await requester.ActionWaitAsync(CancellationToken.None).WaitAsync(FakeFeedServer.Patience);

        Assert.Equal(TimeSpan.FromSeconds((2 * (10 + 14)) + 15), wait);
        Assert.Equal(wait, again);
    }

    [WindowsFact]
    public async Task WhenCapsHasNoSettleSeconds_TheActionWaitFallsBackToTheMaximum()
    {
        await using var rig = new LiveRig();
        FakeFeedServer daemon = await rig.ConnectAsync();
        var requester = new LiveFeedRequester(rig.Feed);

        Task<TimeSpan> pending = requester.ActionWaitAsync(CancellationToken.None);
        await AnswerCapsAsync(daemon, null);

        Assert.Equal(LiveFeedRequester.ActionWaitFor(null), await pending.WaitAsync(FakeFeedServer.Patience));
    }

    [Fact]
    public void TheDevUiAndLiveModulesTogether_ResolveTheSeamToTheLiveFeed()
    {
        using AtlasContext context = AtlasModules.Compose(
            new AtlasPaths(null, null), NullLoggerFactory.Instance, headless: true, [new LiveModule(), new DevUiModule()]);

        IFeedRequester? seam = context.Services.Get<IFeedRequester>();

        Assert.IsType<LiveFeedRequester>(seam);
    }

    [Fact]
    public async Task WithoutAConnection_TheRequestIsUnavailable()
    {
        await using var rig = new LiveRig();
        IFeedRequester requester = new LiveFeedRequester(rig.Feed);

        CommandException e = await Assert.ThrowsAsync<CommandException>(
            () => requester.RequestAsync(new JsonObject { ["op"] = "caps" }, CancellationToken.None));

        Assert.Equal(CommandException.Unavailable, e.Code);
    }

    [WindowsFact]
    public async Task ARequest_GoesOutWithAnIdAndReturnsTheWholeAnswer()
    {
        await using var rig = new LiveRig();
        FakeFeedServer daemon = await rig.ConnectAsync();
        IFeedRequester requester = new LiveFeedRequester(rig.Feed);
        var message = new JsonObject { ["op"] = "caps" };

        Task<JsonObject> pending = requester.RequestAsync(message, CancellationToken.None);
        JsonObject sent = await daemon.ReadAsync();
        await daemon.WriteAsync(Wire.Result(sent["id"]!.GetValue<long>(), ("actions", true), ("role", "local")));
        JsonObject answer = await pending.WaitAsync(FakeFeedServer.Patience);

        Assert.Equal("caps", sent["op"]!.GetValue<string>());
        Assert.NotNull(sent["id"]);
        Assert.False(message.ContainsKey("id"));
        Assert.True(answer["ok"]!.GetValue<bool>());
        Assert.Equal("local", answer["role"]!.GetValue<string>());
        Assert.Equal(sent["id"]!.GetValue<long>(), answer["id"]!.GetValue<long>());
    }

    [WindowsFact]
    public async Task AnOkFalseAnswer_PassesThroughAsAnAnswer()
    {
        await using var rig = new LiveRig();
        FakeFeedServer daemon = await rig.ConnectAsync();
        IFeedRequester requester = new LiveFeedRequester(rig.Feed);

        Task<JsonObject> pending = requester.RequestAsync(new JsonObject { ["op"] = "action" }, CancellationToken.None);
        await AnswerCapsAsync(daemon, 30);
        JsonObject sent = await daemon.ReadAsync();
        await daemon.WriteAsync(new JsonObject
        {
            ["op"] = "result",
            ["id"] = sent["id"]!.GetValue<long>(),
            ["ok"] = false,
            ["code"] = "denied",
            ["message"] = "actions are off",
        });
        JsonObject answer = await pending.WaitAsync(FakeFeedServer.Patience);

        Assert.False(answer["ok"]!.GetValue<bool>());
        Assert.Equal("denied", answer["code"]!.GetValue<string>());
        Assert.Equal("actions are off", answer["message"]!.GetValue<string>());
    }

    [WindowsFact]
    public async Task Cancellation_PropagatesAsOperationCanceled()
    {
        await using var rig = new LiveRig();
        FakeFeedServer daemon = await rig.ConnectAsync();
        IFeedRequester requester = new LiveFeedRequester(rig.Feed);
        using var cts = new CancellationTokenSource();

        Task<JsonObject> pending = requester.RequestAsync(new JsonObject { ["op"] = "caps" }, cts.Token);
        await daemon.ReadAsync();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(FakeFeedServer.Patience));
    }

    [WindowsFact]
    public async Task AnActionAnsweredAfterTheQueryTimeout_StillGetsItsAnswer()
    {
        await using var rig = new LiveRig(o => o with { QueryTimeout = TimeSpan.FromMilliseconds(300) });
        FakeFeedServer daemon = await rig.ConnectAsync();
        IFeedRequester requester = new LiveFeedRequester(rig.Feed);

        Task<JsonObject> pending = requester.RequestAsync(new JsonObject { ["op"] = "action", ["command"] = "creature ai pause" }, CancellationToken.None);
        await AnswerCapsAsync(daemon, 30);
        JsonObject sent = await daemon.ReadAsync();
        await Task.Delay(900);
        Assert.False(pending.IsCompleted);
        await daemon.WriteAsync(Wire.Result(sent["id"]!.GetValue<long>(), ("settled", false)));
        JsonObject answer = await pending.WaitAsync(FakeFeedServer.Patience);

        Assert.True(answer["ok"]!.GetValue<bool>());
        Assert.False(answer["settled"]!.GetValue<bool>());
    }

    [WindowsFact]
    public async Task ACapsRequestThatIsNotAnswered_FailsAfterTheQueryTimeout()
    {
        await using var rig = new LiveRig(o => o with { QueryTimeout = TimeSpan.FromMilliseconds(300) });
        FakeFeedServer daemon = await rig.ConnectAsync();
        IFeedRequester requester = new LiveFeedRequester(rig.Feed);

        Task<JsonObject> pending = requester.RequestAsync(new JsonObject { ["op"] = "caps" }, CancellationToken.None);
        _ = await daemon.ReadAsync();
        CommandException e = await Assert.ThrowsAsync<CommandException>(() => pending.WaitAsync(FakeFeedServer.Patience));

        Assert.Equal(CommandException.Failed, e.Code);
        Assert.Contains("timed out", e.Message, StringComparison.Ordinal);
    }

    [WindowsFact]
    public async Task ADisconnectDuringARequest_IsUnavailable()
    {
        await using var rig = new LiveRig();
        FakeFeedServer daemon = await rig.ConnectAsync();
        IFeedRequester requester = new LiveFeedRequester(rig.Feed);

        Task<JsonObject> pending = requester.RequestAsync(new JsonObject { ["op"] = "action" }, CancellationToken.None);
        await AnswerCapsAsync(daemon, 30);
        _ = await daemon.ReadAsync();
        daemon.Drop();
        CommandException e = await Assert.ThrowsAsync<CommandException>(() => pending.WaitAsync(FakeFeedServer.Patience));

        Assert.Equal(CommandException.Unavailable, e.Code);
    }

    // The refusal happens before the connection is looked at, so the refused-op theory needs no pipe.
    [Theory]
    [InlineData("subscribe")]
    [InlineData("unsubscribe")]
    [InlineData("tap")]
    [InlineData("hello")]
    [InlineData("query")]
    public async Task AnyOtherOp_IsRefusedBeforeItIsSent(string op)
    {
        await using var rig = new LiveRig();
        IFeedRequester requester = new LiveFeedRequester(rig.Feed);

        CommandException e = await Assert.ThrowsAsync<CommandException>(() => requester.RequestAsync(new JsonObject { ["op"] = op }, CancellationToken.None));

        Assert.Equal(CommandException.BadArguments, e.Code);
    }
}
