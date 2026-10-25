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
        _ = await daemon.ReadAsync();
        daemon.Drop();
        CommandException e = await Assert.ThrowsAsync<CommandException>(() => pending.WaitAsync(FakeFeedServer.Patience));

        Assert.Equal(CommandException.Unavailable, e.Code);
    }

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

        await Assert.ThrowsAsync<ArgumentException>(() => requester.RequestAsync(new JsonObject { ["op"] = op }, CancellationToken.None));
    }
}
