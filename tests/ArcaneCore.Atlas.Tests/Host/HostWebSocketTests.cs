using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Host;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Host;

/// <summary>A scripted browser tab: opens <c>/api/ws</c>, sends JSON text frames and reads what the host pushes.</summary>
internal sealed class UiClient : IAsyncDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly ClientWebSocket _socket = new();

    private UiClient()
    {
        _socket.Options.Proxy = null;
    }

    public WebSocket Socket => _socket;

    public JsonNode? Hello { get; private set; }

    public string ConnectionId => Hello.Str("connection");

    /// <summary>Opens the socket and, when <paramref name="token"/> is given, authenticates and reads the hello.</summary>
    public static async Task<UiClient> ConnectAsync(HostRig rig, string? token = "", string? origin = null)
    {
        var client = new UiClient();
        if (origin is not null)
        {
            client._socket.Options.SetRequestHeader("Origin", origin);
        }

        using var timeout = new CancellationTokenSource(Patience);
        await client._socket.ConnectAsync(new Uri($"ws://127.0.0.1:{rig.Server!.Port}/api/ws"), timeout.Token);
        if (token is not null)
        {
            await client.SendAsync(new JsonObject { ["op"] = "auth", ["token"] = token.Length == 0 ? rig.Server.Token : token });
            client.Hello = await client.ReceiveAsync();
        }

        return client;
    }

    public Task SendAsync(JsonNode message) => SendTextAsync(message.ToJsonString());

    public async Task SendTextAsync(string text)
    {
        using var timeout = new CancellationTokenSource(Patience);
        await _socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, timeout.Token);
    }

    public async Task SendBinaryAsync(byte[] data)
    {
        using var timeout = new CancellationTokenSource(Patience);
        await _socket.SendAsync(data, WebSocketMessageType.Binary, true, timeout.Token);
    }

    /// <summary>The next text frame as JSON; fails the test on a close frame or when nothing arrives in time.</summary>
    public async Task<JsonNode> ReceiveAsync(TimeSpan? timeout = null)
    {
        string? text = await ReceiveTextAsync(timeout ?? Patience);
        Assert.NotNull(text);
        return JsonNode.Parse(text)!;
    }

    /// <summary>The next text frame, or null when the server closed the socket (the close status is then in <see cref="Socket"/>).</summary>
    public async Task<string?> ReceiveTextAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        byte[] buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await _socket.ReceiveAsync(buffer, cts.Token);
            }
            catch (OperationCanceledException)
            {
                Assert.Fail($"Nothing arrived within {timeout.TotalSeconds:0.##} s.");
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                // Finish the close handshake so the server does not wait for the client's half of it.
                try
                {
                    await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ack", cts.Token);
                }
                catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
                {
                    // The server already dropped the connection.
                }

                return null;
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(message.ToArray());
            }
        }
    }

    /// <summary>True when no frame arrives within <paramref name="window"/>. A timed-out read aborts a client socket, so this ends the connection's usefulness.</summary>
    public async Task<bool> NothingArrivesAsync(TimeSpan window)
    {
        using var cts = new CancellationTokenSource(window);
        try
        {
            await _socket.ReceiveAsync(new byte[1024], cts.Token);
            return false;
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException)
        {
            return true;
        }
    }

    /// <summary>Reads until a frame satisfies <paramref name="match"/>; other frames are dropped.</summary>
    public async Task<JsonNode> ReceiveUntilAsync(Func<JsonNode, bool> match, string what)
    {
        using var cts = new CancellationTokenSource(Patience);
        while (!cts.IsCancellationRequested)
        {
            JsonNode message = await ReceiveAsync(Patience);
            if (match(message))
            {
                return message;
            }
        }

        Assert.Fail($"No {what} arrived.");
        return null!;
    }

    public Task<JsonNode> ReceiveEventAsync(string topic, Func<JsonNode, bool>? where = null)
        => ReceiveUntilAsync(m => m.At("op")?.GetValue<string>() == "event" && m.Str("topic") == topic && (where?.Invoke(m.At("data")!) ?? true), $"{topic} event");

    /// <summary>Waits for the server's close frame and returns its status code.</summary>
    public async Task<int> AwaitCloseAsync(TimeSpan? timeout = null)
    {
        while (await ReceiveTextAsync(timeout ?? Patience) is not null)
        {
            // Frames sent before the close are discarded.
        }

        return (int)_socket.CloseStatus!.Value;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test over", timeout.Token);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The server already closed it.
        }

        _socket.Dispose();
    }
}

/// <summary>The UI WebSocket: authentication, event push, commands from the UI, and the capture round trip.</summary>
public sealed class HostWebSocketTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(400);

    private static JsonObject Args(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public async Task AValidAuthMessage_IsAnsweredWithHello_CarryingTheViewAndItsVersion()
    {
        await using HostRig rig = await HostRig.StartAsync();
        await rig.DataAsync("camera.goto", Args("""{"at":[-9450,20,60]}"""));

        await using UiClient ui = await UiClient.ConnectAsync(rig);

        Assert.Equal("hello", ui.Hello.Str("op"));
        Assert.Equal(AtlasVersion.Current, ui.Hello.Str("version"));
        Assert.Equal(1, ui.Hello.Num("viewVersion"));
        Assert.Equal(rig.Context.View.Current.ToJson().ToJsonString(), ui.Hello.At("view")!.ToJsonString());
        Assert.Matches("^[0-9a-f]{12}$", ui.ConnectionId);
        Assert.Equal(1, rig.Server!.Hub.ConnectionCount);
        Assert.Equal(1, (await rig.DataAsync("status")).Num("ui.connections"));
    }

    [Fact]
    public async Task ConnectionsAreRemoved_WhenTheTabCloses()
    {
        await using HostRig rig = await HostRig.StartAsync();
        UiClient ui = await UiClient.ConnectAsync(rig);
        Assert.Equal(1, rig.Server!.Hub.ConnectionCount);

        await ui.DisposeAsync();

        await Wait.UntilAsync(() => rig.Server.Hub.ConnectionCount == 0, "the connection to be removed");
    }

    [Fact]
    public async Task AWrongToken_ANonAuthFirstMessage_OrGarbage_ClosesWith4401_AndNeverRegisters()
    {
        await using HostRig rig = await HostRig.StartAsync();
        string valid = rig.Server!.Token;
        Func<UiClient, Task>[] attempts =
        [
            ui => ui.SendAsync(new JsonObject { ["op"] = "auth", ["token"] = valid + "x" }),
            ui => ui.SendAsync(new JsonObject { ["op"] = "auth", ["token"] = string.Empty }),
            ui => ui.SendAsync(new JsonObject { ["op"] = "auth" }),
            ui => ui.SendAsync(new JsonObject { ["op"] = "auth", ["token"] = 5 }),
            ui => ui.SendAsync(new JsonObject { ["op"] = "command", ["name"] = "status", ["token"] = valid }),
            ui => ui.SendAsync(new JsonObject { ["token"] = valid }),
            ui => ui.SendTextAsync("not json"),
            ui => ui.SendTextAsync("[1]"),
            ui => ui.SendBinaryAsync(Encoding.UTF8.GetBytes($$"""{"op":"auth","token":"{{valid}}"}""")),
        ];

        foreach (Func<UiClient, Task> attempt in attempts)
        {
            await using UiClient ui = await UiClient.ConnectAsync(rig, token: null);
            await attempt(ui);

            Assert.Equal(AtlasServer.CloseUnauthorized, await ui.AwaitCloseAsync());
        }

        Assert.Equal(0, rig.Server.Hub.ConnectionCount);
    }

    [Fact]
    public async Task NoAuthMessageWithinTheTimeout_ClosesWith4401()
    {
        await using HostRig rig = await HostRig.StartAsync(new AtlasServerOptions { AuthTimeout = TimeSpan.FromMilliseconds(300) });
        await using UiClient ui = await UiClient.ConnectAsync(rig, token: null);

        Assert.Equal(AtlasServer.CloseUnauthorized, await ui.AwaitCloseAsync());
    }

    [Fact]
    public async Task TheTokenInAnAuthMessageIsNotNeededInAHeader_ButTheWebSocketRouteStillWantsAWebSocket()
    {
        await using HostRig rig = await HostRig.StartAsync();

        using HttpResponseMessage plain = await rig.Anonymous.GetAsync("api/ws");

        Assert.Equal(HttpStatusCode.BadRequest, plain.StatusCode);
    }

    [Fact]
    public async Task AWebSocketFromAnotherOrigin_IsRefusedAtTheHandshake_ButTheOwnOriginAndNoOriginAreAccepted()
    {
        await using HostRig rig = await HostRig.StartAsync();
        int port = rig.Server!.Port;

        foreach (string hostile in new[] { "http://evil.example", $"http://evil.example:{port}", $"http://127.0.0.1:{port + 1}", $"https://127.0.0.1:{port}", $"http://127.0.0.1.evil.example:{port}", "null", "file://" })
        {
            WebSocketException failure = await Assert.ThrowsAsync<WebSocketException>(async () =>
            {
                await using UiClient ui = await UiClient.ConnectAsync(rig, origin: hostile);
            });
            Assert.Contains("403", failure.Message + failure.InnerException?.Message, StringComparison.Ordinal);
        }

        foreach (string allowed in new[] { $"http://127.0.0.1:{port}", $"http://localhost:{port}", $"http://LOCALHOST:{port}" })
        {
            await using UiClient ui = await UiClient.ConnectAsync(rig, origin: allowed);
            Assert.Equal("hello", ui.Hello.Str("op"));
        }

        await using UiClient noOrigin = await UiClient.ConnectAsync(rig);
        Assert.Equal("hello", noOrigin.Hello.Str("op"));
    }

    [Fact]
    public async Task ViewChangesAndCommandResults_ArePushedToEveryConnectedTab()
    {
        await using HostRig rig = await HostRig.StartAsync();
        await using UiClient first = await UiClient.ConnectAsync(rig);
        await using UiClient second = await UiClient.ConnectAsync(rig);

        await rig.DataAsync("view.set", Args("""{"camera":{"yaw":123}}"""));

        foreach (UiClient ui in new[] { first, second })
        {
            JsonNode view = await ui.ReceiveEventAsync("view");
            Assert.Equal(1, view.Num("data.version"));
            Assert.Equal("http", view.Str("data.origin"));
            Assert.Equal(123, view.Num("data.view.camera.yaw"));
            JsonNode command = await ui.ReceiveEventAsync("command");
            Assert.Equal("view.set", command.Str("data.command"));
            Assert.True(command.Flag("data.ok"));
        }
    }

    [Fact]
    public async Task ACommandFromTheUi_RunsWithAUiOrigin_AndIsAnsweredWithItsId()
    {
        await using HostRig rig = await HostRig.StartAsync();
        await using UiClient ui = await UiClient.ConnectAsync(rig);

        await ui.SendAsync(Args("""{"op":"command","id":7,"name":"camera.goto","args":{"at":[-9450,20,60],"yaw":33}}"""));

        // The view event is pushed while the command runs and the command event after it, so the three frames may arrive in
        // any order relative to the result: collect them all.
        JsonNode? result = null;
        JsonNode? view = null;
        JsonNode? logged = null;
        while (result is null || view is null || logged is null)
        {
            JsonNode message = await ui.ReceiveAsync();
            string? op = message["op"]?.GetValue<string>();
            string? topic = message["topic"]?.GetValue<string>();
            if (op == "result")
            {
                result = message;
            }
            else if (op == "event" && topic == EventHub.Topics.View)
            {
                view = message;
            }
            else if (op == "event" && topic == EventHub.Topics.Command)
            {
                logged = message;
            }
        }

        Assert.Equal(7, result.Num("id"));
        Assert.True(result.Flag("result.ok"));
        Assert.Equal("camera.goto", result.Str("result.command"));
        Assert.Equal(33, result.Num("result.data.view.camera.yaw"));
        Assert.Equal(33, rig.Context.View.Current.Camera.Yaw);
        Assert.Equal($"ui:{ui.ConnectionId}", view.Str("data.origin"));
        Assert.Equal($"ui:{ui.ConnectionId}", logged.Str("data.origin"));
    }

    [Fact]
    public async Task ACommandResultCarriesTheClientsIdBack_WhateverItsType_AndFailuresAreResultsToo()
    {
        await using HostRig rig = await HostRig.StartAsync();
        await using UiClient ui = await UiClient.ConnectAsync(rig);

        await ui.SendAsync(Args("""{"op":"command","id":"abc","name":"test.echo","args":{"text":"hi"}}"""));
        JsonNode stringId = await ui.ReceiveUntilAsync(m => m.Str("op") == "result", "result");
        await ui.SendAsync(Args("""{"op":"command","name":"no.such"}"""));
        JsonNode noId = await ui.ReceiveUntilAsync(m => m.Str("op") == "result", "result");
        await ui.SendAsync(Args("""{"op":"command","id":3,"name":"camera.goto","args":{}}"""));
        JsonNode bad = await ui.ReceiveUntilAsync(m => m.Str("op") == "result", "result");

        Assert.Equal("abc", stringId.Str("id"));
        Assert.Equal("hi", stringId.Str("result.data.echo"));
        Assert.Equal("Ui", stringId.Str("result.data.origin"));
        Assert.Null(noId.At("id"));
        Assert.Equal(CommandException.UnknownCommand, noId.Str("result.error.code"));
        Assert.Equal(3, bad.Num("id"));
        Assert.Equal(CommandException.BadArguments, bad.Str("result.error.code"));
    }

    [Fact]
    public async Task MalformedMessages_GetAnErrorReply_AndTheConnectionStaysUsable()
    {
        await using HostRig rig = await HostRig.StartAsync();
        await using UiClient ui = await UiClient.ConnectAsync(rig);
        string[] malformed =
        [
            "not json",
            "[1,2]",
            "42",
            """{"no":"op"}""",
            """{"op":5}""",
            """{"op":"teleport"}""",
            """{"op":"command"}""",
            """{"op":"command","name":""}""",
            """{"op":"command","name":"status","args":[1]}""",
            """{"op":"command","name":"status","args":{"a":1,"a":2}}""",
        ];

        foreach (string text in malformed)
        {
            await ui.SendTextAsync(text);
            JsonNode error = await ui.ReceiveUntilAsync(m => m.Str("op") == "error", $"error reply to {text}");
            Assert.Equal("bad-request", error.Str("code"));
        }

        await ui.SendAsync(Args("""{"op":"auth","token":"again"}"""));
        await ui.SendAsync(Args("""{"op":"command","id":1,"name":"test.echo"}"""));
        JsonNode result = await ui.ReceiveUntilAsync(m => m.Str("op") is "result" or "error", "result");
        Assert.Equal("result", result.Str("op"));
        Assert.True(result.Flag("result.ok"));
    }

    [Fact]
    public async Task ABinaryFrame_OrAnOversizeMessage_ClosesTheConnectionWithInvalidPayloadData()
    {
        await using HostRig rig = await HostRig.StartAsync();
        await using UiClient binary = await UiClient.ConnectAsync(rig);
        await using UiClient huge = await UiClient.ConnectAsync(rig);

        await binary.SendBinaryAsync([1, 2, 3]);
        await huge.SendTextAsync(new string('x', (1024 * 1024) + 1));

        Assert.Equal((int)WebSocketCloseStatus.InvalidPayloadData, await binary.AwaitCloseAsync());
        Assert.Equal((int)WebSocketCloseStatus.InvalidPayloadData, await huge.AwaitCloseAsync());
        await Wait.UntilAsync(() => rig.Server!.Hub.ConnectionCount == 0, "both connections to be removed");
    }

    [Fact]
    public async Task AMessageJustUnderTheLimit_IsAccepted()
    {
        await using HostRig rig = await HostRig.StartAsync();
        await using UiClient ui = await UiClient.ConnectAsync(rig);
        string padding = new('y', 1024 * 1024 - 200);

        await ui.SendTextAsync($$$"""{"op":"command","id":1,"name":"test.echo","args":{"text":"{{{padding}}}"}}""");

        JsonNode result = await ui.ReceiveUntilAsync(m => m.Str("op") == "result", "result");
        Assert.True(result.Flag("result.ok"));
        Assert.Equal(padding.Length, result.Str("result.data.echo").Length);
    }

    [Fact]
    public async Task Capture_AsksTheUiToRender_AndReturnsThePngItPosts()
    {
        await using HostRig rig = await HostRig.StartAsync();
        await using UiClient ui = await UiClient.ConnectAsync(rig);
        string shots = Path.Combine(rig.Directory, "ui-shots");
        Task<JsonObject> call = rig.PostCommandAsync("capture", Args($$"""{"width":32,"height":16,"out":{{JsonValue.Create(shots)!.ToJsonString()}}}"""));

        JsonNode request = await ui.ReceiveUntilAsync(m => m.Str("op") == "capture", "capture request");
        byte[] png = TestPng.Create(32, 16, 0x40);
        using HttpResponseMessage answer = await rig.Http.PostAsync($"api/capture/{request.Str("id")}", new ByteArrayContent(png));
        JsonObject result = await call;

        Assert.Equal(HttpStatusCode.NoContent, answer.StatusCode);
        Assert.Equal(32, request.Num("width"));
        Assert.Equal(16, request.Num("height"));
        Assert.Null(request.At("view"));
        Assert.True(result.Flag("ok"));
        Assert.Equal("ui", result.Str("data.source"));
        Assert.Equal(32, result.Num("data.width"));
        Assert.Equal(16, result.Num("data.height"));
        Assert.Equal(png, await File.ReadAllBytesAsync(result.Str("data.path")));
        Assert.Empty(rig.Renderer!.Views);
    }

    [Fact]
    public async Task Capture_WithAViewPatch_SendsTheCompleteViewToRender_AndLeavesTheSharedViewAlone()
    {
        await using HostRig rig = await HostRig.StartAsync();
        await using UiClient ui = await UiClient.ConnectAsync(rig);
        Task<JsonObject> call = rig.PostCommandAsync("capture", Args("""{"source":"ui","width":16,"height":16,"view":{"camera":{"yaw":77}}}"""));

        JsonNode request = await ui.ReceiveUntilAsync(m => m.Str("op") == "capture", "capture request");
        using HttpResponseMessage answer = await rig.Http.PostAsync($"api/capture/{request.Str("id")}", new ByteArrayContent(TestPng.Create(16, 16)));
        JsonObject result = await call;

        Assert.Equal(77, request.Num("view.camera.yaw"));
        Assert.NotNull(request.At("view.layers.terrain"));
        Assert.True(result.Flag("ok"));
        Assert.Equal(ViewState.Default.Camera.Yaw, rig.Context.View.Current.Camera.Yaw);
    }

    [Fact]
    public async Task Capture_FailsWithTheUisReason_WhenItReportsCaptureFailed()
    {
        await using HostRig rig = await HostRig.StartAsync();
        await using UiClient ui = await UiClient.ConnectAsync(rig);
        Task<JsonObject> call = rig.PostCommandAsync("capture", Args("""{"source":"ui","width":16,"height":16}"""));

        JsonNode request = await ui.ReceiveUntilAsync(m => m.Str("op") == "capture", "capture request");
        await ui.SendAsync(new JsonObject { ["op"] = "captureFailed", ["id"] = request.Str("id"), ["message"] = "WebGL context lost" });
        JsonObject result = await call;

        Assert.False(result.Flag("ok"));
        Assert.Equal(CommandException.Failed, result.Str("error.code"));
        Assert.Contains("WebGL context lost", result.Str("error.message"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capture_FailsWhenTheUiNeverAnswers_OrGoesAway()
    {
        await using HostRig rig = await HostRig.StartAsync(new AtlasServerOptions { CaptureTimeout = TimeSpan.FromMilliseconds(400) });
        await using UiClient silent = await UiClient.ConnectAsync(rig);

        JsonObject timedOut = await rig.PostCommandAsync("capture", Args("""{"source":"ui","width":16,"height":16}"""));

        Assert.False(timedOut.Flag("ok"));
        Assert.Equal(CommandException.Failed, timedOut.Str("error.code"));
        Assert.Contains("did not answer", timedOut.Str("error.message"), StringComparison.Ordinal);

        await using HostRig leaving = await HostRig.StartAsync();
        UiClient quitter = await UiClient.ConnectAsync(leaving);
        Task<JsonObject> call = leaving.PostCommandAsync("capture", Args("""{"source":"ui","width":16,"height":16}"""));
        await quitter.ReceiveUntilAsync(m => m.Str("op") == "capture", "capture request");
        await quitter.DisposeAsync();
        JsonObject gone = await call;

        Assert.False(gone.Flag("ok"));
        Assert.Equal(CommandException.Unavailable, gone.Str("error.code"));
        Assert.Contains("disconnected", gone.Str("error.message"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capture_RejectsAPostedBodyThatIsNotAPng_AndAnAnswerCanOnlyBeGivenOnce()
    {
        await using HostRig rig = await HostRig.StartAsync();
        await using UiClient ui = await UiClient.ConnectAsync(rig);
        Task<JsonObject> call = rig.PostCommandAsync("capture", Args("""{"source":"ui","width":16,"height":16}"""));
        JsonNode request = await ui.ReceiveUntilAsync(m => m.Str("op") == "capture", "capture request");
        string id = request.Str("id");

        using HttpResponseMessage bad = await rig.Http.PostAsync($"api/capture/{id}", new ByteArrayContent("<html>not an image</html>"u8.ToArray()));
        JsonObject result = await call;
        using HttpResponseMessage late = await rig.Http.PostAsync($"api/capture/{id}", new ByteArrayContent(TestPng.Create(16, 16)));

        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.False(result.Flag("ok"));
        Assert.Contains("not a PNG", result.Str("error.message"), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, late.StatusCode);
    }

    [Fact]
    public async Task Capture_GoesToTheOldestConnection_AndAutoPrefersAConnectedUiOverTheRenderer()
    {
        await using HostRig rig = await HostRig.StartAsync();
        await using UiClient oldest = await UiClient.ConnectAsync(rig);
        await using UiClient newer = await UiClient.ConnectAsync(rig);
        Task<JsonObject> call = rig.PostCommandAsync("capture", Args("""{"width":16,"height":16}"""));

        JsonNode request = await oldest.ReceiveUntilAsync(m => m.Str("op") == "capture", "capture request");
        bool newerIsAsked = !await newer.NothingArrivesAsync(Short);
        using HttpResponseMessage answer = await rig.Http.PostAsync($"api/capture/{request.Str("id")}", new ByteArrayContent(TestPng.Create(16, 16)));
        JsonObject result = await call;

        Assert.Equal(HttpStatusCode.NoContent, answer.StatusCode);
        Assert.True(result.Flag("ok"));
        Assert.Equal("ui", result.Str("data.source"));
        Assert.Empty(rig.Renderer!.Views);
        Assert.False(newerIsAsked);
    }

    [Fact]
    public async Task Capture_WithoutAConnectedUi_UsesTheHeadlessRenderer()
    {
        await using HostRig rig = await HostRig.StartAsync();

        JsonObject result = await rig.PostCommandAsync("capture", Args("""{"width":16,"height":16}"""));

        Assert.True(result.Flag("ok"));
        Assert.Equal("headless", result.Str("data.source"));
        Assert.Single(rig.Renderer!.Views);
    }

    [Fact]
    public void TheEventQueueOfASlowTab_DropsTheOldestEntriesFirst()
    {
        using var socket = WebSocket.CreateFromStream(Stream.Null, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.Zero);
        var connection = new UiConnection(socket, "slow", 1);

        for (int i = 0; i < UiConnection.EventQueueCapacity + 88; i++)
        {
            connection.EnqueueEvent($"e{i}");
        }

        var kept = new List<string>();
        while (connection.PendingEvents.TryRead(out string? json))
        {
            kept.Add(json);
        }

        Assert.Equal(UiConnection.EventQueueCapacity, kept.Count);
        Assert.Equal("e88", kept[0]);
        Assert.Equal($"e{UiConnection.EventQueueCapacity + 87}", kept[^1]);
    }

    [Fact]
    public async Task StoppingTheServer_ClosesOpenTabs_AndRemovesTheDiscoveryFile()
    {
        await using HostRig rig = await HostRig.StartAsync();
        UiClient ui = await UiClient.ConnectAsync(rig);
        string file = rig.Server!.DiscoveryFile!;
        Assert.True(File.Exists(file));

        await rig.Server.DisposeAsync();

        Assert.False(File.Exists(file));
        await ui.AwaitCloseAsync();
        await ui.DisposeAsync();
    }

    [Fact]
    public async Task TheDiscoveryFile_NamesThisInstance_AndIsNotWrittenWhenDisabled()
    {
        await using HostRig rig = await HostRig.StartAsync();
        await using HostRig quiet = await HostRig.StartAsync(new AtlasServerOptions { WriteDiscovery = false });

        DiscoveryRecord record = Assert.Single(Discovery.Live(rig.State));

        Assert.Equal(Environment.ProcessId, record.Pid);
        Assert.Equal(rig.Server!.Port, record.Port);
        Assert.Equal(rig.Server.Token, record.Token);
        Assert.Equal(AtlasVersion.Current, record.Version);
        Assert.Equal(Path.Combine(rig.State.InstancesDirectory, $"{Environment.ProcessId}.json"), rig.Server.DiscoveryFile);
        Assert.Null(quiet.Server!.DiscoveryFile);
        Assert.Empty(Discovery.Live(quiet.State));
    }
}
