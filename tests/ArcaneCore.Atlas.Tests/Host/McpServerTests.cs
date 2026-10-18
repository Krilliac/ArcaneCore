using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Host;
using ArcaneCore.Atlas.Host.Cli;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Host;

/// <summary>A command that a fake client holds until the test releases it (or the call is cancelled).</summary>
internal sealed class ClientGate
{
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _cancelled;

    public Task Started => _started.Task;

    public int Cancelled => Volatile.Read(ref _cancelled);

    public void Release() => _release.TrySetResult();

    public async Task<JsonObject> RunAsync(string name, CancellationToken cancellationToken)
    {
        _started.TrySetResult();
        try
        {
            await _release.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _cancelled);
            throw;
        }

        return FakeCommandClient.Ok(name, new JsonObject { ["released"] = true });
    }
}

/// <summary>A command target the MCP tests script: named handlers, a recorded call log and an optional list failure.</summary>
internal sealed class FakeCommandClient : ICommandClient
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Func<JsonObject, CancellationToken, Task<JsonObject>>> _handlers = new(StringComparer.Ordinal);
    private readonly List<CommandInfo> _commands = [];
    private readonly List<(string Name, JsonObject Args)> _calls = [];
    private int _lists;

    public string Description => "fake";

    /// <summary>Thrown by <see cref="ListAsync"/> while set.</summary>
    public CommandException? ListFailure { get; set; }

    /// <summary>How many times the command list was fetched.</summary>
    public int Lists => Volatile.Read(ref _lists);

    public IReadOnlyList<(string Name, JsonObject Args)> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    public static JsonObject Ok(string command, JsonNode? data = null)
        => new() { ["command"] = command, ["ok"] = true, ["elapsedMs"] = 0, ["data"] = data };

    public static JsonObject Failed(string command, string code, string message)
        => new() { ["command"] = command, ["ok"] = false, ["elapsedMs"] = 0, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    public void Add(string name, Func<JsonObject, CancellationToken, Task<JsonObject>> handler, string summary = "A fake command.")
    {
        lock (_gate)
        {
            _handlers[name] = handler;
            _commands.RemoveAll(c => c.Name == name);
            _commands.Add(new CommandInfo(name, summary, new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }));
        }
    }

    /// <summary>A command that returns its arguments as <c>data.echo</c>.</summary>
    public void AddEcho(string name = "test.echo") => Add(name, (args, _) => Task.FromResult(Ok(name, new JsonObject { ["echo"] = args.DeepClone() })));

    public ClientGate AddGate(string name = "test.wait")
    {
        var gate = new ClientGate();
        Add(name, (_, cancellationToken) => gate.RunAsync(name, cancellationToken));
        return gate;
    }

    public Task<JsonObject> InvokeAsync(string name, JsonObject? args, CancellationToken cancellationToken)
    {
        Func<JsonObject, CancellationToken, Task<JsonObject>>? handler;
        lock (_gate)
        {
            _calls.Add((name, (JsonObject)(args ?? []).DeepClone()));
            _handlers.TryGetValue(name, out handler);
        }

        return handler is null
            ? Task.FromResult(Failed(name, CommandException.UnknownCommand, $"No command named '{name}'."))
            : handler(args ?? [], cancellationToken);
    }

    public Task<IReadOnlyList<CommandInfo>> ListAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _lists);
        if (ListFailure is { } failure)
        {
            throw failure;
        }

        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<CommandInfo>>([.. _commands]);
        }
    }
}

/// <summary>An <see cref="McpServer"/> running on a pipe, with helpers to send requests and wait for the answer to one id.</summary>
internal sealed class McpSession : IAsyncDisposable
{
    public const string Meta = """{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{}}""";
    public const string ServerInfoKey = "io.modelcontextprotocol/serverInfo";

    private readonly CancellationTokenSource _cancel = new();

    public McpSession(ICommandClient client, TimeSpan? drainTimeout = null, Action<InputPipe>? prefill = null)
    {
        Input = new InputPipe();
        prefill?.Invoke(Input);
        Out = new LineSink();
        Log = new LineSink();
        Server = new McpServer(client, Input, Out, Log, drainTimeout);
        Run = Task.Run(() => Server.RunAsync(_cancel.Token));
    }

    public InputPipe Input { get; }

    public LineSink Out { get; }

    public LineSink Log { get; }

    public McpServer Server { get; }

    public Task Run { get; }

    public static string Req(int id, string method, string? parameters = null)
        => "{\"jsonrpc\":\"2.0\",\"id\":" + id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"method\":\"" + method + "\""
            + (parameters is null ? string.Empty : ",\"params\":" + parameters) + "}";

    public static string Notify(string method, string? parameters = null)
        => "{\"jsonrpc\":\"2.0\",\"method\":\"" + method + "\"" + (parameters is null ? string.Empty : ",\"params\":" + parameters) + "}";

    public static string Initialize(int id, string version = "2025-06-18")
        => Req(id, "initialize", "{\"protocolVersion\":\"" + version + "\",\"capabilities\":{},\"clientInfo\":{\"name\":\"test\",\"version\":\"1\"}}");

    public static string List(int id, bool modern = false)
        => Req(id, "tools/list", modern ? "{\"_meta\":" + Meta + "}" : null);

    public static string Call(int id, string tool, string arguments = "{}", bool modern = false)
        => Req(id, "tools/call", "{" + (modern ? "\"_meta\":" + Meta + "," : string.Empty) + "\"name\":\"" + tool + "\",\"arguments\":" + arguments + "}");

    public void Send(string line) => Input.Send(line);

    /// <summary>Sends the handshake and waits for its answer, so later requests are served by the handshake rules.</summary>
    public async Task HandshakeAsync(int id = 1000, string version = "2025-06-18")
    {
        Send(Initialize(id, version));
        await ResponseAsync(id);
        Send(Notify("notifications/initialized"));
    }

    public async Task<JsonNode> ResponseAsync(int id, int timeoutMs = 20_000)
    {
        JsonNode? found = null;
        await Wait.UntilAsync(() => (found = Messages().FirstOrDefault(m => HasId(m, id))) is not null, $"the response to request {id}; output so far: {Out.Text}", timeoutMs);
        return found!;
    }

    public async Task<JsonNode> RequestAsync(string line, int id)
    {
        Send(line);
        return await ResponseAsync(id);
    }

    public IEnumerable<JsonNode> Messages() => Out.Lines.Select(l => JsonNode.Parse(l)!);

    /// <summary>Ends the input and waits for the server to stop.</summary>
    public async Task FinishAsync(int timeoutMs = 30_000)
    {
        Input.End();
        await Run.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
    }

    /// <summary>Cancels the server as Ctrl+C does and waits for it to stop.</summary>
    public async Task StopAsync(int timeoutMs = 30_000)
    {
        await _cancel.CancelAsync();
        await Run.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
    }

    public static int ErrorCode(JsonNode response) => (int)response.Num("error.code");

    public static JsonNode ToolText(JsonNode response) => JsonNode.Parse(response.Str("result.content.0.text"))!;

    public static string ServerName(JsonNode response) => response["result"]!["_meta"]![ServerInfoKey]!["name"]!.GetValue<string>();

    public async ValueTask DisposeAsync()
    {
        Input.End();
        await _cancel.CancelAsync();
        try
        {
            await Run.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException)
        {
            // A server that ignores cancellation has already failed its test.
        }

        _cancel.Dispose();
    }

    private static bool HasId(JsonNode message, int id)
        => message is JsonObject o && o["id"] is JsonValue v && JsonScalars.TryGetDouble(v, out double value) && value == id;
}

/// <summary>A temporary folder, deleted on dispose.</summary>
internal sealed class ScratchFolder : IDisposable
{
    public ScratchFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "atlas-scratch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left for the operating system's temp cleanup.
        }
    }
}

public sealed class McpLegacyTests
{
    private static FakeCommandClient Client()
    {
        var client = new FakeCommandClient();
        client.AddEcho();
        client.Add("camera.goto", (_, _) => Task.FromResult(FakeCommandClient.Ok("camera.goto")));
        return client;
    }

    [Fact]
    public async Task Handshake_ThenList_ThenCall_FollowsTheLifecycle()
    {
        FakeCommandClient client = Client();
        await using var session = new McpSession(client);

        JsonNode init = await session.RequestAsync(McpSession.Initialize(1), 1);
        session.Send(McpSession.Notify("notifications/initialized"));
        JsonNode list = await session.RequestAsync(McpSession.List(2), 2);
        JsonNode call = await session.RequestAsync(McpSession.Call(3, "test_echo", """{"text":"hi"}"""), 3);

        Assert.Equal("2.0", init.Str("jsonrpc"));
        Assert.Equal("2025-06-18", init.Str("result.protocolVersion"));
        Assert.False(init.Flag("result.capabilities.tools.listChanged"));
        Assert.Equal("arcane-atlas", init.Str("result.serverInfo.name"));
        Assert.Equal(AtlasVersion.Current, init.Str("result.serverInfo.version"));
        Assert.False(string.IsNullOrWhiteSpace(init.Str("result.instructions")));
        Assert.Null(init.At("result.resultType"));

        JsonArray tools = list.At("result.tools")!.AsArray();
        Assert.Equal(new[] { "camera_goto", "test_echo" }, tools.Select(t => t.Str("name")));
        Assert.All(tools, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Str("description")));
            Assert.Equal("object", t.Str("inputSchema.type"));
        });
        Assert.Null(list.At("result.resultType"));
        Assert.Null(list.At("result.ttlMs"));
        Assert.Null(list.At("result._meta"));

        Assert.False(call.Flag("result.isError"));
        Assert.Equal("text", call.Str("result.content.0.type"));
        JsonNode outcome = McpSession.ToolText(call);
        Assert.True(outcome.Flag("ok"));
        Assert.Equal("hi", outcome.Str("data.echo.text"));
        Assert.Null(call.At("result.resultType"));
        (string name, JsonObject args) = Assert.Single(client.Calls);
        Assert.Equal("test.echo", name);
        Assert.Equal("""{"text":"hi"}""", args.ToJsonString());

        Assert.Equal(3, session.Out.Lines.Count);
        await session.FinishAsync();
        Assert.Empty(session.Log.Lines);
    }

    [Theory]
    [InlineData("2025-11-25", "2025-11-25")]
    [InlineData("2025-06-18", "2025-06-18")]
    [InlineData("2025-03-26", "2025-03-26")]
    [InlineData("2024-11-05", "2024-11-05")]
    [InlineData("2026-07-28", "2025-11-25")]
    [InlineData("1999-01-01", "2025-11-25")]
    [InlineData("not-a-version", "2025-11-25")]
    public async Task Initialize_AnswersWithTheClientsVersionWhenSupported_ElseTheNewestHandshakeRevision(string requested, string expected)
    {
        await using var session = new McpSession(Client());

        JsonNode init = await session.RequestAsync(McpSession.Initialize(1, requested), 1);

        Assert.Equal(expected, init.Str("result.protocolVersion"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("""{"protocolVersion":5}""")]
    [InlineData("""{"protocolVersion":""}""")]
    [InlineData("[1]")]
    public async Task Initialize_WithoutAStringVersion_IsInvalidParams(string? parameters)
    {
        await using var session = new McpSession(Client());

        JsonNode response = await session.RequestAsync(McpSession.Req(1, "initialize", parameters), 1);

        Assert.Equal(McpServer.InvalidParams, McpSession.ErrorCode(response));
        Assert.Null(response.At("result"));
    }

    [Fact]
    public async Task APipelinedHandshake_IsAppliedInOrder_SoTheListThatFollowsWorks()
    {
        await using var session = new McpSession(Client(), prefill: input =>
        {
            input.Send(McpSession.Initialize(1));
            input.Send(McpSession.Notify("notifications/initialized"));
            input.Send(McpSession.List(2));
            input.Send(McpSession.Call(3, "test_echo"));
        });

        JsonNode list = await session.ResponseAsync(2);
        JsonNode call = await session.ResponseAsync(3);

        Assert.NotNull(list.At("result.tools"));
        Assert.False(call.Flag("result.isError"));
    }

    [Fact]
    public async Task ARequestWithoutTheHandshakeOrPerRequestMetadata_IsInvalidParams_AndNamesTheFix()
    {
        await using var session = new McpSession(Client());

        JsonNode list = await session.RequestAsync(McpSession.List(1), 1);
        JsonNode call = await session.RequestAsync(McpSession.Call(2, "test_echo"), 2);

        Assert.Equal(McpServer.InvalidParams, McpSession.ErrorCode(list));
        Assert.Equal(McpServer.InvalidParams, McpSession.ErrorCode(call));
        Assert.Contains("initialize", list.Str("error.message"), StringComparison.Ordinal);
        Assert.Contains("_meta", list.Str("error.message"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AfterInitialize_RequestsMayCarryOtherMetadata_LikeAProgressToken()
    {
        await using var session = new McpSession(Client());
        await session.HandshakeAsync();

        JsonNode list = await session.RequestAsync(McpSession.Req(1, "tools/list", """{"_meta":{"progressToken":"p1"}}"""), 1);

        Assert.NotNull(list.At("result.tools"));
        Assert.Null(list.At("result.resultType"));
    }

    [Fact]
    public async Task Ping_ReturnsAnEmptyResult_AndKeepsTheIdType()
    {
        await using var session = new McpSession(Client());

        session.Send("""{"jsonrpc":"2.0","id":7,"method":"ping"}""");
        session.Send("""{"jsonrpc":"2.0","id":"abc","method":"ping"}""");
        await session.ResponseAsync(7);
        await Wait.UntilAsync(() => session.Out.Lines.Count == 2, "both ping answers");

        Assert.Equal("""{"jsonrpc":"2.0","id":7,"result":{}}""", session.Out.Lines[0]);
        Assert.Equal("""{"jsonrpc":"2.0","id":"abc","result":{}}""", session.Out.Lines[1]);
    }
}

public sealed class McpModernTests
{
    private static readonly string[] AllVersions = ["2026-07-28", "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];

    private static FakeCommandClient Client()
    {
        var client = new FakeCommandClient();
        client.AddEcho();
        return client;
    }

    [Fact]
    public async Task ListAndCall_NeedNoHandshake_AndAnswerWithCompleteResults()
    {
        FakeCommandClient client = Client();
        await using var session = new McpSession(client);

        JsonNode list = await session.RequestAsync(McpSession.List(1, modern: true), 1);
        JsonNode call = await session.RequestAsync(McpSession.Call(2, "test_echo", """{"text":"x"}""", modern: true), 2);

        Assert.Equal("complete", list.Str("result.resultType"));
        Assert.Equal(0, list.Num("result.ttlMs"));
        Assert.Equal("private", list.Str("result.cacheScope"));
        Assert.Equal("arcane-atlas", McpSession.ServerName(list));
        Assert.Equal("test_echo", list.Str("result.tools.0.name"));
        Assert.Equal("complete", call.Str("result.resultType"));
        Assert.False(call.Flag("result.isError"));
        Assert.Equal("arcane-atlas", McpSession.ServerName(call));
        Assert.Equal("x", McpSession.ToolText(call).Str("data.echo.text"));
    }

    [Fact]
    public async Task ModernMetadata_IsHonouredEvenAfterAnInitialize()
    {
        await using var session = new McpSession(Client());
        await session.HandshakeAsync();

        JsonNode list = await session.RequestAsync(McpSession.List(1, modern: true), 1);

        Assert.Equal("complete", list.Str("result.resultType"));
    }

    [Fact]
    public async Task ServerDiscover_ListsEveryVersion_Capabilities_AndTheCachingHints()
    {
        await using var session = new McpSession(Client());

        JsonNode bare = await session.RequestAsync(McpSession.Req(1, "server/discover"), 1);
        JsonNode withMeta = await session.RequestAsync(McpSession.Req(2, "server/discover", "{\"_meta\":" + McpSession.Meta + "}"), 2);

        Assert.Equal(AllVersions, bare.At("result.supportedVersions")!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.NotNull(bare.At("result.capabilities.tools"));
        Assert.Equal("complete", bare.Str("result.resultType"));
        Assert.Equal(0, bare.Num("result.ttlMs"));
        Assert.Equal("private", bare.Str("result.cacheScope"));
        Assert.False(string.IsNullOrWhiteSpace(bare.Str("result.instructions")));
        Assert.Equal("arcane-atlas", McpSession.ServerName(bare));
        Assert.Equal(AllVersions, withMeta.At("result.supportedVersions")!.AsArray().Select(v => v!.GetValue<string>()));
    }

    [Fact]
    public async Task AnUnsupportedVersion_IsErrorMinus32022_ListingWhatIsSupported()
    {
        await using var session = new McpSession(Client());
        const string Old = """{"_meta":{"io.modelcontextprotocol/protocolVersion":"1999-01-01","io.modelcontextprotocol/clientCapabilities":{}}}""";

        JsonNode list = await session.RequestAsync(McpSession.Req(1, "tools/list", Old), 1);
        JsonNode discover = await session.RequestAsync(McpSession.Req(2, "server/discover", Old), 2);

        foreach (JsonNode response in new[] { list, discover })
        {
            Assert.Equal(McpServer.UnsupportedProtocolVersion, McpSession.ErrorCode(response));
            Assert.Equal(AllVersions, response.At("error.data.supported")!.AsArray().Select(v => v!.GetValue<string>()));
            Assert.Equal("1999-01-01", response.Str("error.data.requested"));
        }
    }

    [Theory]
    [InlineData("""{"io.modelcontextprotocol/protocolVersion":"2025-06-18","io.modelcontextprotocol/clientCapabilities":{}}""", "handshake revision")]
    [InlineData("""{"io.modelcontextprotocol/protocolVersion":5,"io.modelcontextprotocol/clientCapabilities":{}}""", "must be a string")]
    [InlineData("""{"io.modelcontextprotocol/protocolVersion":"2026-07-28"}""", "clientCapabilities")]
    [InlineData("""{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":[1]}""", "clientCapabilities")]
    public async Task BadPerRequestMetadata_IsInvalidParams_WithTheReason(string meta, string reason)
    {
        await using var session = new McpSession(Client());

        JsonNode response = await session.RequestAsync(McpSession.Req(1, "tools/list", "{\"_meta\":" + meta + "}"), 1);

        Assert.Equal(McpServer.InvalidParams, McpSession.ErrorCode(response));
        Assert.Contains(reason, response.Str("error.message"), StringComparison.Ordinal);
    }
}

public sealed class McpToolTests
{
    [Fact]
    public async Task ACommandThatFails_IsAToolResultWithIsError_NotAProtocolError()
    {
        var client = new FakeCommandClient();
        client.Add("test.fail", (_, _) => Task.FromResult(FakeCommandClient.Failed("test.fail", CommandException.BadArguments, "nope")));
        await using var session = new McpSession(client);
        await session.HandshakeAsync();

        JsonNode response = await session.RequestAsync(McpSession.Call(1, "test_fail"), 1);

        Assert.Null(response.At("error"));
        Assert.True(response.Flag("result.isError"));
        JsonNode outcome = McpSession.ToolText(response);
        Assert.False(outcome.Flag("ok"));
        Assert.Equal(CommandException.BadArguments, outcome.Str("error.code"));
        Assert.Equal("nope", outcome.Str("error.message"));
    }

    [Fact]
    public async Task BothTheToolNameAndTheDottedCommandNameReachTheCommand()
    {
        var client = new FakeCommandClient();
        client.AddEcho("camera.goto");
        await using var session = new McpSession(client);
        await session.HandshakeAsync();

        await session.RequestAsync(McpSession.Call(1, "camera_goto"), 1);
        await session.RequestAsync(McpSession.Call(2, "camera.goto"), 2);

        Assert.Equal(new[] { "camera.goto", "camera.goto" }, client.Calls.Select(c => c.Name));
    }

    [Fact]
    public async Task ToolsAreListedSortedByName_OncePerCommand()
    {
        var client = new FakeCommandClient();
        client.AddEcho("zeta.b");
        client.AddEcho("alpha.a");
        client.AddEcho("mid.dle.name");
        await using var session = new McpSession(client);
        await session.HandshakeAsync();

        JsonNode list = await session.RequestAsync(McpSession.List(1), 1);

        Assert.Equal(new[] { "alpha_a", "mid_dle_name", "zeta_b" }, list.At("result.tools")!.AsArray().Select(t => t.Str("name")));
        Assert.Equal("alpha_a", McpServer.ToolName("alpha.a"));
    }

    [Theory]
    [InlineData("""{"name":"nope"}""", McpServer.InvalidParams, "Unknown tool: nope")]
    [InlineData("""{"arguments":{}}""", McpServer.InvalidParams, "'name'")]
    [InlineData("""{"name":""}""", McpServer.InvalidParams, "'name'")]
    [InlineData("""{"name":5}""", McpServer.InvalidParams, "'name'")]
    [InlineData("""{"name":"test_echo","arguments":[1]}""", McpServer.InvalidParams, "'arguments'")]
    [InlineData("""{"name":"test_echo","arguments":"x"}""", McpServer.InvalidParams, "'arguments'")]
    [InlineData("[1]", McpServer.InvalidParams, "'params'")]
    [InlineData(null, McpServer.InvalidParams, "'name'")]
    public async Task MalformedToolCalls_AreProtocolErrors_NotToolResults(string? parameters, int code, string messagePart)
    {
        var client = new FakeCommandClient();
        client.AddEcho();
        await using var session = new McpSession(client);
        await session.HandshakeAsync();

        JsonNode response = await session.RequestAsync(McpSession.Req(1, "tools/call", parameters), 1);

        Assert.Equal(code, McpSession.ErrorCode(response));
        Assert.Contains(messagePart, response.Str("error.message"), StringComparison.Ordinal);
        Assert.Null(response.At("result"));
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task AToolListCursor_IsInvalidParams_BecauseEveryToolComesInOnePage()
    {
        var client = new FakeCommandClient();
        client.AddEcho();
        await using var session = new McpSession(client);
        await session.HandshakeAsync();

        JsonNode response = await session.RequestAsync(McpSession.Req(1, "tools/list", """{"cursor":"page-2"}"""), 1);
        JsonNode fine = await session.RequestAsync(McpSession.Req(2, "tools/list", """{"cursor":null}"""), 2);

        Assert.Equal(McpServer.InvalidParams, McpSession.ErrorCode(response));
        Assert.Contains("cursor", response.Str("error.message"), StringComparison.Ordinal);
        Assert.NotNull(fine.At("result.tools"));
    }

    [Fact]
    public async Task ACommandAddedLater_IsFoundByLookingAtTheListAgain()
    {
        var client = new FakeCommandClient();
        client.AddEcho();
        await using var session = new McpSession(client);
        await session.HandshakeAsync();

        JsonNode missing = await session.RequestAsync(McpSession.Call(1, "late_cmd"), 1);
        int listsAfterMiss = client.Lists;
        client.AddEcho("late.cmd");
        JsonNode found = await session.RequestAsync(McpSession.Call(2, "late_cmd", """{"a":1}"""), 2);

        Assert.Equal(McpServer.InvalidParams, McpSession.ErrorCode(missing));
        Assert.True(listsAfterMiss >= 1, "a miss refreshes the list");
        Assert.False(found.Flag("result.isError"));
        Assert.Equal("late.cmd", Assert.Single(client.Calls).Name);
    }

    [Fact]
    public async Task AnUnreachableTarget_IsAnErrorForThatRequest_AndTheServerKeepsServing()
    {
        var client = new FakeCommandClient();
        client.AddEcho();
        client.ListFailure = new CommandException(CommandException.Unavailable, "Cannot reach the Atlas instance pid 1 on port 2: refused");
        await using var session = new McpSession(client);
        await session.HandshakeAsync();

        JsonNode down = await session.RequestAsync(McpSession.List(1), 1);
        client.ListFailure = null;
        JsonNode up = await session.RequestAsync(McpSession.List(2), 2);

        Assert.Equal(McpServer.InternalError, McpSession.ErrorCode(down));
        Assert.Contains("Cannot reach", down.Str("error.message"), StringComparison.Ordinal);
        Assert.NotNull(up.At("result.tools"));
    }

    [Fact]
    public async Task ACommandThatThrows_IsAnInternalError_LoggedOnTheLogWriterOnly()
    {
        var client = new FakeCommandClient();
        client.Add("test.boom", (_, _) => throw new InvalidOperationException("boom"));
        await using var session = new McpSession(client);
        await session.HandshakeAsync();

        JsonNode response = await session.RequestAsync(McpSession.Call(1, "test_boom"), 1);

        Assert.Equal(McpServer.InternalError, McpSession.ErrorCode(response));
        Assert.Contains("boom", response.Str("error.message"), StringComparison.Ordinal);
        Assert.Contains("boom", session.Log.Text, StringComparison.Ordinal);
        Assert.All(session.Messages(), m => Assert.Equal("2.0", m.Str("jsonrpc")));
    }
}

public sealed class McpFramingTests
{
    private static FakeCommandClient Client()
    {
        var client = new FakeCommandClient();
        client.AddEcho();
        return client;
    }

    [Fact]
    public async Task UnknownMethods_AreMethodNotFound_ButUnknownNotificationsAreIgnored()
    {
        await using var session = new McpSession(Client());

        session.Send(McpSession.Notify("something/else"));
        session.Send(McpSession.Notify("notifications/roots/list_changed", "{}"));
        JsonNode response = await session.RequestAsync(McpSession.Req(1, "resources/list"), 1);
        await session.FinishAsync();

        Assert.Equal(McpServer.MethodNotFound, McpSession.ErrorCode(response));
        Assert.Contains("resources/list", response.Str("error.message"), StringComparison.Ordinal);
        Assert.Single(session.Out.Lines);
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":null,"method":"ping"}""", true)]
    [InlineData("""{"jsonrpc":"2.0","id":{},"method":"ping"}""", true)]
    [InlineData("""{"jsonrpc":"2.0","id":true,"method":"ping"}""", true)]
    [InlineData("""{"jsonrpc":"1.0","id":4,"method":"ping"}""", false)]
    [InlineData("""{"id":4,"method":"ping"}""", false)]
    [InlineData("""{"jsonrpc":"2.0","id":4,"method":5}""", false)]
    [InlineData("""{"jsonrpc":"2.0","id":4,"method":""}""", false)]
    [InlineData("5", true)]
    [InlineData("\"text\"", true)]
    [InlineData("null", true)]
    public async Task InvalidRequests_AreErrorMinus32600(string line, bool idIsNull)
    {
        await using var session = new McpSession(Client());

        session.Send(line);
        await Wait.UntilAsync(() => session.Out.Lines.Count >= 1, "the error answer");
        JsonNode response = session.Messages().First();

        Assert.Equal(McpServer.InvalidRequest, McpSession.ErrorCode(response));
        if (idIsNull)
        {
            Assert.True(response.AsObject().ContainsKey("id"));
            Assert.Null(response["id"]);
        }
        else
        {
            Assert.Equal(4, response.Num("id"));
        }
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"id":2,"method":"ping"}""")]
    public async Task UnparseableLines_AreErrorMinus32700_WithANullId_AndLaterLinesStillWork(string line)
    {
        await using var session = new McpSession(Client());

        session.Send(line);
        JsonNode ping = await session.RequestAsync(McpSession.Req(9, "ping"), 9);
        JsonNode parse = session.Messages().First(m => m["id"] is null);

        Assert.Equal(McpServer.ParseError, McpSession.ErrorCode(parse));
        Assert.NotNull(ping.At("result"));
    }

    [Fact]
    public async Task BlankLinesAndResponsesFromTheClient_AreIgnored()
    {
        await using var session = new McpSession(Client());

        session.Send(string.Empty);
        session.Send("   \t ");
        session.Send("""{"jsonrpc":"2.0","id":9,"result":{}}""");
        session.Send("""{"jsonrpc":"2.0","id":9,"error":{"code":-1,"message":"x"}}""");
        await session.RequestAsync(McpSession.Req(1, "ping"), 1);
        await session.FinishAsync();

        Assert.Single(session.Out.Lines);
    }

    [Fact]
    public async Task ABatch_IsAnsweredWithOneArray_InRequestOrder_SkippingNotifications()
    {
        await using var session = new McpSession(Client());

        session.Send("[" + McpSession.Req(1, "ping") + "," + McpSession.Notify("notifications/initialized") + "," + McpSession.List(2, modern: true) + ",5]");
        await Wait.UntilAsync(() => session.Out.Lines.Count == 1, "the batch answer");
        await session.FinishAsync();

        JsonArray answers = JsonNode.Parse(session.Out.Lines[0])!.AsArray();
        Assert.Equal(3, answers.Count);
        Assert.Equal(1, answers[0].Num("id"));
        Assert.NotNull(answers[0].At("result"));
        Assert.Equal(2, answers[1].Num("id"));
        Assert.Equal("test_echo", answers[1].Str("result.tools.0.name"));
        Assert.Equal(McpServer.InvalidRequest, McpSession.ErrorCode(answers[2]!));
    }

    [Fact]
    public async Task AnEmptyBatchIsInvalid_AndABatchOfNotificationsIsAnsweredWithNothing()
    {
        await using var session = new McpSession(Client());

        session.Send("[]");
        session.Send("[" + McpSession.Notify("notifications/initialized") + "]");
        JsonNode ping = await session.RequestAsync(McpSession.Req(1, "ping"), 1);
        await session.FinishAsync();

        Assert.Equal(2, session.Out.Lines.Count);
        JsonNode empty = session.Messages().First(m => m["id"] is null);
        Assert.Equal(McpServer.InvalidRequest, McpSession.ErrorCode(empty));
        Assert.NotNull(ping.At("result"));
    }

    [Fact]
    public async Task EveryOutputLineIsOneJsonRpcMessage_WithNoEmbeddedNewlines()
    {
        await using var session = new McpSession(Client());
        await session.HandshakeAsync();

        await session.RequestAsync(McpSession.Call(1, "test_echo", "{\"text\":\"line one\\nline two\"}"), 1);
        await session.RequestAsync(McpSession.Req(2, "nope"), 2);
        session.Send("not json");
        await Wait.UntilAsync(() => session.Out.Lines.Count == 4, "four output lines");
        await session.FinishAsync();

        Assert.All(session.Out.Lines, line =>
        {
            Assert.DoesNotContain('\n', line);
            Assert.Equal("2.0", JsonNode.Parse(line).Str("jsonrpc"));
        });
        // The text content is the result JSON (which escapes the newline); the outer line escapes that text once more.
        Assert.Contains("line one\\nline two", JsonNode.Parse(session.Out.Lines[1]).Str("result.content.0.text"), StringComparison.Ordinal);
        Assert.Empty(session.Log.Lines);
    }
}

public sealed class McpConcurrencyTests
{
    [Fact]
    public async Task ACancelledRequest_StopsRunning_AndGetsNoResponse()
    {
        var client = new FakeCommandClient();
        ClientGate gate = client.AddGate();
        await using var session = new McpSession(client);
        await session.HandshakeAsync();

        session.Send(McpSession.Call(5, "test_wait"));
        await gate.Started;
        session.Send(McpSession.Notify("notifications/cancelled", """{"requestId":5,"reason":"user"}"""));
        await Wait.UntilAsync(() => gate.Cancelled == 1, "the cancellation to reach the command");
        await session.RequestAsync(McpSession.Req(6, "ping"), 6);
        await session.RequestAsync(McpSession.Req(7, "ping"), 7);

        Assert.DoesNotContain(session.Messages(), m => m["id"] is JsonValue v && JsonScalars.TryGetDouble(v, out double d) && d == 5);
        Assert.Equal(3, session.Out.Lines.Count);
    }

    [Fact]
    public async Task CancellationMatchesTheIdByTypeAndValue_AndAnUnknownIdChangesNothing()
    {
        var client = new FakeCommandClient();
        ClientGate gate = client.AddGate();
        await using var session = new McpSession(client);
        await session.HandshakeAsync();

        session.Send("""{"jsonrpc":"2.0","id":"abc","method":"tools/call","params":{"name":"test_wait","arguments":{}}}""");
        await gate.Started;
        session.Send(McpSession.Notify("notifications/cancelled", """{"requestId":"zzz"}"""));
        session.Send(McpSession.Notify("notifications/cancelled", """{"requestId":"abc "}"""));
        session.Send(McpSession.Notify("notifications/cancelled", """{"requestId":99}"""));
        session.Send(McpSession.Notify("notifications/cancelled", "{}"));
        session.Send(McpSession.Notify("notifications/cancelled"));
        await session.RequestAsync(McpSession.Req(1, "ping"), 1);

        Assert.Equal(0, gate.Cancelled);

        session.Send(McpSession.Notify("notifications/cancelled", """{"requestId":"abc"}"""));
        await Wait.UntilAsync(() => gate.Cancelled == 1, "the cancellation by string id");
    }

    [Fact]
    public async Task ASlowRequestDoesNotBlockLaterOnes()
    {
        var client = new FakeCommandClient();
        client.AddEcho();
        ClientGate gate = client.AddGate();
        await using var session = new McpSession(client);
        await session.HandshakeAsync();

        session.Send(McpSession.Call(1, "test_wait"));
        await gate.Started;
        JsonNode fast = await session.RequestAsync(McpSession.Call(2, "test_echo"), 2);
        Assert.Null(session.Messages().FirstOrDefault(m => m["id"] is JsonValue v && JsonScalars.TryGetDouble(v, out double d) && d == 1));
        gate.Release();
        JsonNode slow = await session.ResponseAsync(1);

        Assert.False(fast.Flag("result.isError"));
        Assert.True(McpSession.ToolText(slow).Flag("data.released"));
        Assert.Equal(new[] { 1000.0, 2.0, 1.0 }, session.Messages().Select(m => m.Num("id")));
    }

    [Fact]
    public async Task ARequestIdThatIsStillRunning_CannotBeReused()
    {
        var client = new FakeCommandClient();
        client.AddEcho();
        ClientGate gate = client.AddGate();
        await using var session = new McpSession(client);
        await session.HandshakeAsync();

        session.Send(McpSession.Call(1, "test_wait"));
        await gate.Started;
        JsonNode second = await session.RequestAsync(McpSession.Call(1, "test_echo"), 1);
        gate.Release();
        await Wait.UntilAsync(() => session.Out.Lines.Count == 3, "the first call's answer");

        Assert.Equal(McpServer.InvalidRequest, McpSession.ErrorCode(second));
        Assert.Contains("already running", second.Str("error.message"), StringComparison.Ordinal);
        Assert.Single(session.Messages(), m => m.At("result") is not null && m["id"] is JsonValue v && JsonScalars.TryGetDouble(v, out double d) && d == 1);
    }

    [Fact]
    public async Task WhenInputEnds_RunningRequestsStillGetTheirAnswers()
    {
        var client = new FakeCommandClient();
        ClientGate gate = client.AddGate();
        await using var session = new McpSession(client);
        await session.HandshakeAsync();

        session.Send(McpSession.Call(1, "test_wait"));
        await gate.Started;
        session.Input.End();
        await Task.Delay(300);
        Assert.False(session.Run.IsCompleted, "the server waits for the running request");
        gate.Release();
        await session.Run.WaitAsync(TimeSpan.FromSeconds(30));

        JsonNode answer = await session.ResponseAsync(1);
        Assert.False(answer.Flag("result.isError"));
    }

    [Fact]
    public async Task WhenInputEndsAndARequestNeverFinishes_ItIsCancelledAfterTheDrainTimeout()
    {
        var client = new FakeCommandClient();
        ClientGate gate = client.AddGate();
        await using var session = new McpSession(client, drainTimeout: TimeSpan.FromMilliseconds(300));
        await session.HandshakeAsync();

        session.Send(McpSession.Call(1, "test_wait"));
        await gate.Started;
        await session.FinishAsync(timeoutMs: 20_000);

        Assert.Equal(1, gate.Cancelled);
        Assert.Single(session.Out.Lines);
    }

    [Fact]
    public async Task ShuttingDownCancelsRunningRequests_AndStopsPromptly()
    {
        var client = new FakeCommandClient();
        ClientGate gate = client.AddGate();
        await using var session = new McpSession(client);
        await session.HandshakeAsync();

        session.Send(McpSession.Call(1, "test_wait"));
        await gate.Started;
        await session.StopAsync(timeoutMs: 20_000);

        Assert.Equal(1, gate.Cancelled);
        Assert.Single(session.Out.Lines);
    }

    [Fact]
    public async Task ManyConcurrentCalls_AllGetTheirOwnAnswer()
    {
        var client = new FakeCommandClient();
        client.AddEcho();
        await using var session = new McpSession(client);
        await session.HandshakeAsync();

        for (int id = 1; id <= 50; id++)
        {
            session.Send(McpSession.Call(id, "test_echo", "{\"n\":" + id + "}"));
        }

        await Wait.UntilAsync(() => session.Out.Lines.Count == 51, "50 answers");
        await session.FinishAsync();

        Assert.Equal(
            Enumerable.Range(1, 50).Select(i => (double)i),
            session.Messages().Where(m => m.Num("id") != 1000).OrderBy(m => m.Num("id")).Select(m => m.Num("id")));
        Assert.All(session.Messages().Where(m => m.Num("id") != 1000), m => Assert.Equal(m.Num("id"), McpSession.ToolText(m).Num("data.echo.n")));
    }
}

public sealed class McpCaptureTests
{
    private static (FakeCommandClient Client, ScratchFolder Folder) CaptureClient(Func<string, byte[]?> makeFile)
    {
        var folder = new ScratchFolder();
        var client = new FakeCommandClient();
        client.Add("capture", async (args, cancellationToken) =>
        {
            string path = folder.File("shot.png");
            byte[]? bytes = makeFile(path);
            if (bytes is not null)
            {
                await File.WriteAllBytesAsync(path, bytes, cancellationToken);
            }

            return FakeCommandClient.Ok("capture", new JsonObject { ["path"] = path, ["width"] = 37, ["height"] = 19 });
        });
        return (client, folder);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACapture_ReturnsTheTextResultAndTheImageItself(bool modern)
    {
        byte[] png = TestPng.Create(37, 19, shade: 0x40);
        (FakeCommandClient client, ScratchFolder folder) = CaptureClient(_ => png);
        using ScratchFolder cleanup = folder;
        await using var session = new McpSession(client);
        if (!modern)
        {
            await session.HandshakeAsync();
        }

        JsonNode response = await session.RequestAsync(McpSession.Call(1, "capture", "{}", modern), 1);

        Assert.False(response.Flag("result.isError"));
        JsonArray content = response.At("result.content")!.AsArray();
        Assert.Equal(2, content.Count);
        Assert.Equal("text", content[0].Str("type"));
        Assert.Equal("image", content[1].Str("type"));
        Assert.Equal("image/png", content[1].Str("mimeType"));
        byte[] returned = Convert.FromBase64String(content[1].Str("data"));
        Assert.Equal(png, returned);
        Assert.Equal((37, 19), CaptureCommand.PngSize(returned));
        Assert.Equal(modern, response.At("result.resultType") is not null);
    }

    [Fact]
    public async Task AFileThatIsNotAPng_IsNotSentAsAnImage()
    {
        (FakeCommandClient client, ScratchFolder folder) = CaptureClient(_ => "definitely not a png"u8.ToArray());
        using ScratchFolder cleanup = folder;
        await using var session = new McpSession(client);
        await session.HandshakeAsync();

        JsonNode response = await session.RequestAsync(McpSession.Call(1, "capture"), 1);

        Assert.Single(response.At("result.content")!.AsArray());
    }

    [Fact]
    public async Task AMissingFile_GivesTextOnly_AndLogsWhy()
    {
        (FakeCommandClient client, ScratchFolder folder) = CaptureClient(_ => null);
        using ScratchFolder cleanup = folder;
        await using var session = new McpSession(client);
        await session.HandshakeAsync();

        JsonNode response = await session.RequestAsync(McpSession.Call(1, "capture"), 1);

        Assert.Single(response.At("result.content")!.AsArray());
        Assert.False(response.Flag("result.isError"));
        Assert.Contains("missing", session.Log.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedCapture_HasNoImage_AndOtherCommandsNeverGetOne()
    {
        var client = new FakeCommandClient();
        client.Add("capture", (_, _) => Task.FromResult(FakeCommandClient.Failed("capture", CommandException.Unavailable, "no renderer")));
        using var folder = new ScratchFolder();
        string path = folder.File("other.png");
        await File.WriteAllBytesAsync(path, TestPng.Create(16, 16));
        client.Add("test.path", (_, _) => Task.FromResult(FakeCommandClient.Ok("test.path", new JsonObject { ["path"] = path })));
        await using var session = new McpSession(client);
        await session.HandshakeAsync();

        JsonNode failed = await session.RequestAsync(McpSession.Call(1, "capture"), 1);
        JsonNode other = await session.RequestAsync(McpSession.Call(2, "test_path"), 2);

        Assert.True(failed.Flag("result.isError"));
        Assert.Single(failed.At("result.content")!.AsArray());
        Assert.False(other.Flag("result.isError"));
        Assert.Single(other.At("result.content")!.AsArray());
    }
}

/// <summary>The same protocol against real command registries: in-process headless and a running instance.</summary>
public sealed class McpOverRealCommandsTests
{
    [Fact]
    public async Task Headless_ListsExactlyTheHeadlessCommands_AndMovesTheView()
    {
        var instanceOnly = new CommandDefinition("test.instance", "Needs serve.", [], (_, _) => Task.FromResult<JsonNode?>(new JsonObject()), CommandSurfaces.Instance);
        await using HostRig rig = HostRig.Create(headless: true, register: c => c.Commands.Register(instanceOnly));
        var client = new LocalCommandClient(rig.Context, CommandOrigin.Mcp);
        await using var session = new McpSession(client);
        await session.HandshakeAsync();

        JsonNode list = await session.RequestAsync(McpSession.List(1), 1);
        JsonNode move = await session.RequestAsync(McpSession.Call(2, "camera_goto", """{"x":-9450,"y":20,"z":60,"yaw":123}"""), 2);
        JsonNode bad = await session.RequestAsync(McpSession.Call(3, "camera_goto"), 3);
        JsonNode blocked = await session.RequestAsync(McpSession.Call(4, "test_instance"), 4);

        string[] expected = [.. rig.Context.Commands.All.Where(c => c.Surfaces.HasFlag(CommandSurfaces.Headless)).Select(c => McpServer.ToolName(c.Name)).Order(StringComparer.Ordinal)];
        string[] listed = [.. list.At("result.tools")!.AsArray().Select(t => t.Str("name"))];
        Assert.Equal(expected, listed);
        Assert.DoesNotContain("test_instance", listed);
        Assert.Contains("query_pick", listed);
        Assert.Contains("capture", listed);
        Assert.Contains("camera_goto", listed);

        Assert.False(move.Flag("result.isError"));
        Assert.Equal(123, rig.Context.View.Current.Camera.Yaw);
        Assert.Equal("mcp", Assert.Single(rig.EventsOf(EventHub.Topics.View)).Str("origin"));

        Assert.True(bad.Flag("result.isError"));
        Assert.Equal(CommandException.BadArguments, McpSession.ToolText(bad).Str("error.code"));
        Assert.Equal(McpServer.InvalidParams, McpSession.ErrorCode(blocked));
    }

    [Fact]
    public async Task Headless_Capture_ReturnsThePngTheRendererMade()
    {
        await using HostRig rig = HostRig.Create(headless: true);
        using var folder = new ScratchFolder();
        var client = new LocalCommandClient(rig.Context, CommandOrigin.Mcp);
        await using var session = new McpSession(client);

        JsonNode response = await session.RequestAsync(McpSession.Call(1, "capture", "{\"width\":40,\"height\":24,\"out\":" + JsonValue.Create(folder.Path)!.ToJsonString() + "}", modern: true), 1);

        Assert.False(response.Flag("result.isError"));
        string path = McpSession.ToolText(response).Str("data.path");
        byte[] fileBytes = await File.ReadAllBytesAsync(path);
        byte[] returned = Convert.FromBase64String(response.Str("result.content.1.data"));
        Assert.Equal(fileBytes, returned);
        Assert.Equal((40, 24), CaptureCommand.PngSize(returned));
        Assert.Equal("headless", McpSession.ToolText(response).Str("data.source"));
    }

    [Fact]
    public async Task AttachedToAnInstance_ListsItsCommands_IncludingInstanceOnlyOnes_AndLabelsCallsAsMcp()
    {
        var instanceOnly = new CommandDefinition("test.instance", "Needs serve.", [], (_, _) => Task.FromResult<JsonNode?>(new JsonObject { ["ran"] = true }), CommandSurfaces.Instance);
        await using HostRig server = await HostRig.StartAsync(register: c => c.Commands.Register(instanceOnly));
        DiscoveryRecord record = Assert.Single(Discovery.Live(server.State));
        using var client = new RemoteCommandClient(record, CommandOrigin.Mcp);
        await using var session = new McpSession(client);

        JsonNode list = await session.RequestAsync(McpSession.List(1, modern: true), 1);
        JsonNode ran = await session.RequestAsync(McpSession.Call(2, "test_instance", "{}", modern: true), 2);
        JsonNode echo = await session.RequestAsync(McpSession.Call(3, "test_echo", """{"text":"over http"}""", modern: true), 3);

        string[] listed = [.. list.At("result.tools")!.AsArray().Select(t => t.Str("name"))];
        Assert.Equal(server.Context.Commands.All.Count, listed.Length);
        Assert.Contains("test_instance", listed);
        Assert.True(McpSession.ToolText(ran).Flag("data.ran"));
        Assert.Equal("Mcp", McpSession.ToolText(echo).Str("data.origin"));
        Assert.False(McpSession.ToolText(echo).Flag("data.headless"));
    }

    [Fact]
    public async Task AttachedToAnInstance_Capture_ReturnsTheImageTheInstanceWrote()
    {
        await using HostRig server = await HostRig.StartAsync();
        DiscoveryRecord record = Assert.Single(Discovery.Live(server.State));
        using var client = new RemoteCommandClient(record, CommandOrigin.Mcp);
        await using var session = new McpSession(client);
        await session.HandshakeAsync();

        JsonNode response = await session.RequestAsync(McpSession.Call(1, "capture", """{"width":48,"height":32}"""), 1);

        Assert.False(response.Flag("result.isError"));
        string path = McpSession.ToolText(response).Str("data.path");
        byte[] returned = Convert.FromBase64String(response.Str("result.content.1.data"));
        Assert.Equal(await File.ReadAllBytesAsync(path), returned);
        Assert.Equal((48, 32), CaptureCommand.PngSize(returned));
        Assert.StartsWith(server.State.CapturesDirectory, path, StringComparison.Ordinal);
    }
}

/// <summary>A one-connection HTTP server that answers the first request with a fixed response (an answer a real Atlas never gives).</summary>
internal sealed class CannedHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _serving;
    private string _request = string.Empty;

    public CannedHttpServer(int status, string contentType, string body)
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _serving = Task.Run(() => ServeAsync(status, contentType, body));
    }

    public int Port { get; }

    /// <summary>The request the server received (headers and body), once it has been read.</summary>
    public string RequestText => Volatile.Read(ref _request);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _serving.WaitAsync(TimeSpan.FromSeconds(10));
        _stop.Dispose();
    }

    private static int IndexOfHeaderEnd(List<byte> bytes)
    {
        for (int i = 0; i + 3 < bytes.Count; i++)
        {
            if (bytes[i] == '\r' && bytes[i + 1] == '\n' && bytes[i + 2] == '\r' && bytes[i + 3] == '\n')
            {
                return i;
            }
        }

        return -1;
    }

    private static int ContentLength(string head)
    {
        foreach (string line in head.Split("\r\n"))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) && int.TryParse(line["Content-Length:".Length..].Trim(), out int length))
            {
                return length;
            }
        }

        return 0;
    }

    private async Task ServeAsync(int status, string contentType, string body)
    {
        try
        {
            using TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
            await using NetworkStream stream = client.GetStream();
            var received = new List<byte>();
            byte[] buffer = new byte[4096];
            int headerEnd;
            while ((headerEnd = IndexOfHeaderEnd(received)) < 0)
            {
                int read = await stream.ReadAsync(buffer, _stop.Token);
                if (read == 0)
                {
                    return;
                }

                received.AddRange(buffer.AsSpan(0, read).ToArray());
            }

            int expected = headerEnd + 4 + ContentLength(Encoding.ASCII.GetString([.. received], 0, headerEnd));
            while (received.Count < expected)
            {
                int read = await stream.ReadAsync(buffer, _stop.Token);
                if (read == 0)
                {
                    break;
                }

                received.AddRange(buffer.AsSpan(0, read).ToArray());
            }

            Volatile.Write(ref _request, Encoding.UTF8.GetString([.. received]));
            byte[] payload = Encoding.UTF8.GetBytes(body);
            string header = $"HTTP/1.1 {status} Canned\r\nContent-Type: {contentType}\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), _stop.Token);
            await stream.WriteAsync(payload, _stop.Token);
            await stream.FlushAsync(_stop.Token);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
            // The test finished (or the client went away) before the canned server answered.
        }
    }
}

public sealed class CommandClientTests
{
    [Fact]
    public async Task Local_RunsHeadless_ReportsFailuresAsResults_AndOffersOnlyHeadlessCommands()
    {
        var instanceOnly = new CommandDefinition("test.instance", "Needs serve.", [], (_, _) => Task.FromResult<JsonNode?>(new JsonObject()), CommandSurfaces.Instance);
        await using HostRig rig = HostRig.Create(headless: true, register: c => c.Commands.Register(instanceOnly));
        var client = new LocalCommandClient(rig.Context, CommandOrigin.Cli);

        JsonObject ok = await client.InvokeAsync("test.echo", new JsonObject { ["text"] = "t" }, CancellationToken.None);
        JsonObject unknown = await client.InvokeAsync("no.such", null, CancellationToken.None);
        JsonObject blocked = await client.InvokeAsync("test.instance", null, CancellationToken.None);
        IReadOnlyList<CommandInfo> commands = await client.ListAsync(CancellationToken.None);

        Assert.Equal("headless", client.Description);
        Assert.True(ok.Flag("ok"));
        Assert.True(ok.Flag("data.headless"));
        Assert.Equal(CommandException.UnknownCommand, unknown.Str("error.code"));
        Assert.Equal(CommandException.Unavailable, blocked.Str("error.code"));
        Assert.DoesNotContain(commands, c => c.Name == "test.instance");
        Assert.Contains(commands, c => c.Name == "test.echo" && c.Schema.Str("type") == "object" && c.Schema.At("properties.text") is not null);
    }

    [Fact]
    public async Task Remote_CarriesTheOrigin_ReturnsTheSameShape_AndListsTheInstancesCommands()
    {
        await using HostRig server = await HostRig.StartAsync();
        DiscoveryRecord record = Assert.Single(Discovery.Live(server.State));
        using var client = new RemoteCommandClient(record, CommandOrigin.Script);

        JsonObject ok = await client.InvokeAsync("test.echo", new JsonObject { ["text"] = "t" }, CancellationToken.None);
        JsonObject unknown = await client.InvokeAsync("no.such", null, CancellationToken.None);
        JsonObject bad = await client.InvokeAsync("camera.goto", null, CancellationToken.None);
        IReadOnlyList<CommandInfo> commands = await client.ListAsync(CancellationToken.None);

        Assert.Equal($"instance pid {record.Pid} on port {record.Port}", client.Description);
        Assert.Equal("Script", ok.Str("data.origin"));
        Assert.False(ok.Flag("data.headless"));
        Assert.Equal(CommandException.UnknownCommand, unknown.Str("error.code"));
        Assert.Equal(CommandException.BadArguments, bad.Str("error.code"));
        Assert.Equal(server.Context.Commands.All.Count, commands.Count);
        Assert.All(commands, c => Assert.Equal("object", c.Schema.Str("type")));
    }

    [Fact]
    public async Task Remote_AWrongToken_IsAnUnauthorizedResult_AndAFailedList()
    {
        await using HostRig server = await HostRig.StartAsync();
        DiscoveryRecord good = Assert.Single(Discovery.Live(server.State));
        using var client = new RemoteCommandClient(good with { Token = "wrong" }, CommandOrigin.Cli);

        JsonObject result = await client.InvokeAsync("status", null, CancellationToken.None);
        CommandException list = await Assert.ThrowsAsync<CommandException>(() => client.ListAsync(CancellationToken.None));

        Assert.False(result.Flag("ok"));
        Assert.Equal("unauthorized", result.Str("error.code"));
        Assert.Equal(CommandException.Unavailable, list.Code);
        Assert.Contains("401", list.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(200, "text/html", "<html>not the api</html>", CommandException.Failed, "HTTP 200")]
    [InlineData(500, "application/json", """{"ok":false,"error":{"code":"failed","message":"the instance broke"}}""", CommandException.Failed, "the instance broke")]
    [InlineData(404, "application/json", """{"error":{"code":"unknown-command","message":"No command named 'x'."}}""", CommandException.UnknownCommand, "No command named")]
    [InlineData(200, "application/json", "[1,2]", CommandException.Failed, "HTTP 200")]
    [InlineData(200, "application/json", "{\"no\":\"ok flag\"}", CommandException.Failed, "HTTP 200")]
    public async Task Remote_AnAnswerThatIsNotACommandResult_IsAFailureResult_NotACrash(int status, string contentType, string body, string expectedCode, string expectedMessagePart)
    {
        await using var canned = new CannedHttpServer(status, contentType, body);
        using var client = new RemoteCommandClient(new DiscoveryRecord(Environment.ProcessId, canned.Port, "token", DateTimeOffset.UtcNow, "x", null, null), CommandOrigin.Cli);

        JsonObject result = await client.InvokeAsync("status", null, CancellationToken.None);

        Assert.False(result.Flag("ok"));
        Assert.Equal("status", result.Str("command"));
        Assert.Equal(expectedCode, result.Str("error.code"));
        Assert.Contains(expectedMessagePart, result.Str("error.message"), StringComparison.Ordinal);
        Assert.Contains("Bearer token", canned.RequestText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remote_CancellingACall_ReachesTheCommandOnTheInstance()
    {
        var gate = new GateCommand();
        await using HostRig server = await HostRig.StartAsync(register: c => c.Commands.Register(gate.Definition));
        DiscoveryRecord record = Assert.Single(Discovery.Live(server.State));
        using var client = new RemoteCommandClient(record, CommandOrigin.Cli);
        using var cancel = new CancellationTokenSource();

        Task<JsonObject> call = client.InvokeAsync("test.wait", null, cancel.Token);
        await gate.Started.WaitAsync(TimeSpan.FromSeconds(20));
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        await Wait.UntilAsync(() => gate.Cancelled == 1, "the instance to see the cancelled request");
    }

    [Fact]
    public async Task Remote_AnInstanceThatIsGone_IsAnUnavailableResult_AndListThrowsUnavailable()
    {
        DiscoveryRecord record;
        await using (HostRig server = await HostRig.StartAsync())
        {
            record = Assert.Single(Discovery.Live(server.State));
        }

        using var client = new RemoteCommandClient(record, CommandOrigin.Cli);

        JsonObject result = await client.InvokeAsync("status", null, CancellationToken.None);
        CommandException list = await Assert.ThrowsAsync<CommandException>(() => client.ListAsync(CancellationToken.None));

        Assert.Equal(CommandException.Unavailable, result.Str("error.code"));
        Assert.Contains("Cannot reach", result.Str("error.message"), StringComparison.Ordinal);
        Assert.Equal(CommandException.Unavailable, list.Code);
    }
}
