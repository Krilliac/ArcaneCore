using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Live;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Live;

/// <summary>A fact that needs Windows named pipes with access lists; skipped, visibly, elsewhere.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "needs Windows named pipes (access lists, \\\\.\\pipe\\ enumeration).";
        }
    }
}

/// <summary>A fact for platforms without \.pipe enumeration; skipped, visibly, on Windows.</summary>
public sealed class NonWindowsFactAttribute : FactAttribute
{
    public NonWindowsFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "covers the non-Windows refusal; this host is Windows.";
        }
    }
}

/// <summary>JSON the way the map feed writes it (design section 9), built in code and sent as text.</summary>
internal static class Wire
{
    public static JsonObject Hello(int protocol = 1, int pid = 4242, string sha = "feedface", params (uint Map, uint Instance, int Players, int Creatures)[] maps)
        => new()
        {
            ["op"] = "hello",
            ["protocol"] = protocol,
            ["sha"] = sha,
            ["pid"] = pid,
            ["maps"] = new JsonArray([.. maps.Select(m => (JsonNode?)new JsonObject { ["map"] = m.Map, ["instance"] = m.Instance, ["players"] = m.Players, ["creatures"] = m.Creatures })]),
        };

    public static JsonObject Entity(string guid, string kind, double x, double y, double z, string? name = null, double o = 0.5)
        => new()
        {
            ["g"] = guid,
            ["k"] = kind,
            ["e"] = 1234,
            ["n"] = name ?? ("unit " + guid),
            ["x"] = x,
            ["y"] = y,
            ["z"] = z,
            ["o"] = o,
            ["hp"] = 100,
            ["mt"] = "idle",
        };

    public static JsonObject Frame(uint map, uint instance, long seq, params JsonObject[] entities)
        => new()
        {
            ["op"] = "frame",
            ["seq"] = seq,
            ["clockMs"] = seq * 250,
            ["map"] = map,
            ["instance"] = instance,
            ["grids"] = new JsonArray(new JsonArray(32, 33), new JsonArray(32, 32)),
            ["counters"] = new JsonObject { ["deferredPathSearches"] = 7, ["noPathPaths"] = 1 },
            ["entities"] = new JsonArray([.. entities.Select(e => (JsonNode?)e)]),
        };

    public static JsonObject Result(long id, params (string Key, JsonNode? Value)[] fields)
    {
        var json = new JsonObject { ["op"] = "result", ["id"] = id, ["ok"] = true };
        foreach ((string key, JsonNode? value) in fields)
        {
            json[key] = value;
        }

        return json;
    }

    public static JsonArray Point(double x, double y, double z) => new(x, y, z);
}

/// <summary>An in-process stand-in for the world daemon's map feed: a real named pipe speaking design section 9, driven line by line by the test.</summary>
internal sealed class FakeFeedServer : IAsyncDisposable
{
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Pipe buffer size in each direction. With the default of 0 a write completes only when the other side reads, so a test that awaits the
    /// client's command before reading its line would deadlock; the real daemon always has a reader pending, so a buffer models it.
    /// </summary>
    public const int BufferBytes = 64 * 1024;

    private readonly NamedPipeServerStream _pipe;
    private readonly StreamReader _reader;

    public FakeFeedServer(string? pipeName = null)
        : this(pipeName ?? NewName(), null)
    {
    }

    public FakeFeedServer(string pipeName, NamedPipeServerStream? pipe)
    {
        PipeName = pipeName;
        _pipe = pipe ?? new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, BufferBytes, BufferBytes);
        _reader = new StreamReader(_pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
    }

    public string PipeName { get; }

    public static string NewName() => "atlas-l2-" + Guid.NewGuid().ToString("N")[..16];

    public async Task AcceptAsync() => await _pipe.WaitForConnectionAsync().WaitAsync(Patience);

    /// <summary>The next line the client sent, or null at its end of stream.</summary>
    public async Task<string?> ReadLineAsync()
    {
        using var timeout = new CancellationTokenSource(Patience);
        return await _reader.ReadLineAsync(timeout.Token);
    }

    public async Task<JsonObject> ReadAsync()
    {
        string? line = await ReadLineAsync();
        Assert.NotNull(line);
        return JsonNode.Parse(line)!.AsObject();
    }

    public async Task WriteLineAsync(string line) => await WriteBytesAsync(Encoding.UTF8.GetBytes(line + "\n"));

    public Task WriteAsync(JsonObject message) => WriteLineAsync(message.ToJsonString());

    public async Task WriteBytesAsync(ReadOnlyMemory<byte> bytes)
    {
        using var timeout = new CancellationTokenSource(Patience);
        await _pipe.WriteAsync(bytes, timeout.Token);
    }

    /// <summary>Reads the client's hello and answers it.</summary>
    public async Task<JsonObject> AnswerHelloAsync(JsonObject? reply = null)
    {
        JsonObject hello = await ReadAsync();
        await WriteAsync(reply ?? Wire.Hello());
        return hello;
    }

    /// <summary>Closes the server end, as a daemon that exits does.</summary>
    public void Drop()
    {
        try
        {
            _pipe.Dispose();
        }
        catch (IOException)
        {
            // Already broken by the client.
        }
    }

    public ValueTask DisposeAsync()
    {
        Drop();
        _reader.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Collects what is published to an <see cref="EventHub"/> and hands it out in order.</summary>
internal sealed class EventLog : IDisposable
{
    private readonly Channel<(string Topic, JsonNode? Payload)> _events = Channel.CreateUnbounded<(string, JsonNode?)>();
    private readonly IDisposable _subscription;

    public EventLog(EventHub hub)
    {
        _subscription = hub.Subscribe((topic, payload) => _events.Writer.TryWrite((topic, payload)));
    }

    /// <summary>The next event on <paramref name="topic"/> (earlier events of other topics are skipped).</summary>
    public async Task<JsonNode?> NextAsync(string topic)
    {
        using var timeout = new CancellationTokenSource(FakeFeedServer.Patience);
        while (await _events.Reader.WaitToReadAsync(timeout.Token))
        {
            while (_events.Reader.TryRead(out (string Topic, JsonNode? Payload) item))
            {
                if (item.Topic == topic)
                {
                    return item.Payload;
                }
            }
        }

        throw new InvalidOperationException($"no '{topic}' event arrived.");
    }

    /// <summary>Events of <paramref name="topic"/> already published, without waiting.</summary>
    public List<JsonNode?> Drain(string topic)
    {
        var list = new List<JsonNode?>();
        while (_events.Reader.TryRead(out (string Topic, JsonNode? Payload) item))
        {
            if (item.Topic == topic)
            {
                list.Add(item.Payload);
            }
        }

        return list;
    }

    public void Dispose() => _subscription.Dispose();
}

/// <summary>Fixed discovery for tests.</summary>
internal sealed class FixedDiscovery(params int[] pids) : IFeedDiscovery
{
    public IReadOnlyList<int> FindFeeds() => pids;

    public bool? PipeExists(string pipeName) => null;
}

/// <summary>A live client wired to a fresh hub and view, talking to a fake feed over a real named pipe.</summary>
internal sealed class LiveRig : IAsyncDisposable
{
    private readonly List<FakeFeedServer> _servers = [];
    private CommandRegistry? _registry;

    public LiveRig(Func<LiveOptions, LiveOptions>? tune = null, IFeedDiscovery? discovery = null)
    {
        PipeName = FakeFeedServer.NewName();
        var options = new LiveOptions
        {
            PipeNameFor = _ => PipeName,
            Discovery = discovery ?? new FixedDiscovery(4242),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            HelloTimeout = TimeSpan.FromSeconds(10),
            QueryTimeout = TimeSpan.FromSeconds(10),
        };
        Options = tune is null ? options : tune(options);
        Feed = new LiveFeed(Options, Events, View, NullLogger.Instance);
        Log = new EventLog(Events);
    }

    public string PipeName { get; }

    public LiveOptions Options { get; }

    public EventHub Events { get; } = new();

    public ViewStore View { get; } = new(ViewState.Default);

    public LiveFeed Feed { get; }

    public EventLog Log { get; }

    /// <summary>A fake daemon serving <paramref name="pipeName"/> (this rig's pipe by default), closed when the rig is disposed.</summary>
    public FakeFeedServer NewServer(string? pipeName = null)
    {
        var server = new FakeFeedServer(pipeName ?? PipeName);
        _servers.Add(server);
        return server;
    }

    /// <summary>Starts connecting and returns once the pipe is accepted; the caller finishes the hello exchange.</summary>
    public async Task<(FakeFeedServer Server, Task<JsonObject> Connect)> BeginConnectAsync(int? pid = 4242, FakeFeedServer? server = null)
    {
        server ??= NewServer();
        Task accept = server.AcceptAsync();
        Task<JsonObject> connect = Feed.ConnectAsync(pid, CancellationToken.None);
        await accept;
        return (server, connect);
    }

    /// <summary>Starts a fake daemon on this rig's pipe name, connects the client to it and finishes the hello exchange.</summary>
    public async Task<FakeFeedServer> ConnectAsync(int pid = 4242, JsonObject? hello = null, FakeFeedServer? server = null)
    {
        (FakeFeedServer daemon, Task<JsonObject> connect) = await BeginConnectAsync(pid, server);
        await daemon.AnswerHelloAsync(hello ?? Wire.Hello(pid: pid));
        await connect.WaitAsync(FakeFeedServer.Patience);
        return daemon;
    }

    /// <summary>
    /// Runs a command through a registry that holds only the live commands. The result's data is passed through its JSON text, as every real surface
    /// (HTTP, CLI, MCP) delivers it, so a test never depends on how a handler happened to build a number.
    /// </summary>
    public async Task<CommandResult> RunAsync(string name, JsonObject? args = null)
    {
        CommandRegistry registry = Registry();
        CommandResult result = await registry.InvokeAsync(name, args, new NoServices(), CommandOrigin.Cli, headless: true, CancellationToken.None);
        return result.Data is null ? result : result with { Data = JsonNode.Parse(result.Data.ToJsonString()) };
    }

    /// <summary>A registry holding exactly the live commands, bound to this rig's feed.</summary>
    public CommandRegistry Registry()
    {
        if (_registry is null)
        {
            var registry = new CommandRegistry();
            LiveCommands.Register(registry, Feed);
            _registry = registry;
        }

        return _registry;
    }

    public async ValueTask DisposeAsync()
    {
        Feed.Dispose();
        Log.Dispose();
        foreach (FakeFeedServer server in _servers)
        {
            await server.DisposeAsync();
        }
    }

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}

/// <summary>Condition waits for tests: poll with a generous bound instead of sleeping a guessed time.</summary>
internal static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, string what)
    {
        using var timeout = new CancellationTokenSource(FakeFeedServer.Patience);
        while (!condition())
        {
            try
            {
                await Task.Delay(5, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"timed out waiting for: {what}");
            }
        }
    }
}
