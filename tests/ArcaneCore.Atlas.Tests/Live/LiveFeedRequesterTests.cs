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
}
