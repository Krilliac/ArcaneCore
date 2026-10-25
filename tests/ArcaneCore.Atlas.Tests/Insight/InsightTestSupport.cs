using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Host;
using ArcaneCore.Atlas.Insight;
using ArcaneCore.Atlas.Live;
using ArcaneCore.Atlas.Tests.Live;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Insight;

/// <summary>
/// The whole stack the insight commands run in: the real host module (command log events and the 20,000-node limit), the real live module on
/// a real named pipe served by a fake daemon, and the insight module under test.
/// </summary>
internal sealed class InsightRig : IAsyncDisposable
{
    private readonly string _stateRoot;
    private readonly List<(string Topic, JsonNode? Data)> _events = [];
    private readonly IDisposable _subscription;
    private CancellationTokenSource? _responderStop;
    private Task? _responder;

    private InsightRig(AtlasContext context, FakeFeedServer server, string pipeName, LiveOptions options, string stateRoot, InsightClock clock)
    {
        Context = context;
        Server = server;
        PipeName = pipeName;
        Options = options;
        _stateRoot = stateRoot;
        Clock = clock;
        _subscription = context.Events.Subscribe((topic, data) =>
        {
            lock (_events)
            {
                _events.Add((topic, data?.DeepClone()));
            }
        });
    }

    public AtlasContext Context { get; }

    public FakeFeedServer Server { get; private set; }

    public string PipeName { get; }

    public LiveOptions Options { get; }

    public InsightClock Clock { get; }

    public InsightHub Hub => Context.Services.Require<InsightHub>();

    public LiveFeed Feed => Context.Services.Require<LiveFeed>();

    /// <summary>Composes the stack and, unless told not to, connects to the fake daemon with a hello that offers feed revision <paramref name="rev"/>.</summary>
    public static async Task<InsightRig> StartAsync(int rev = 1, SpellNames? spells = null, bool connect = true)
    {
        string pipe = FakeFeedServer.NewName();
        var options = new LiveOptions
        {
            PipeNameFor = _ => pipe,
            Discovery = new FixedDiscovery(4242),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            HelloTimeout = TimeSpan.FromSeconds(10),
            QueryTimeout = TimeSpan.FromSeconds(10),
        };
        string root = Path.Combine(Path.GetTempPath(), "atlas-insight-" + Guid.NewGuid().ToString("N")[..12]);
        var clock = new InsightClock();
        AtlasContext context = HostComposer.Compose(
            new AtlasPaths(null, null),
            NullLoggerFactory.Instance,
            headless: true,
            [new HostModule(new AtlasState(root)), new LiveModule(options), new InsightModule(clock.Read, spells)]);
        var rig = new InsightRig(context, new FakeFeedServer(pipe), pipe, options, root, clock);
        if (connect)
        {
            await rig.ConnectAsync(rev);
        }

        return rig;
    }

    public Task ConnectAsync(int rev)
    {
        JsonObject hello = Wire.Hello(pid: 4242);
        if (rev > 1)
        {
            hello["rev"] = rev;
        }

        return ConnectWithHelloAsync(hello);
    }

    /// <summary>Connects (pid 4242) and answers the handshake with exactly <paramref name="hello"/>.</summary>
    public async Task ConnectWithHelloAsync(JsonObject hello)
    {
        Task accept = Server.AcceptAsync();
        Task<CommandResult> connecting = RunAsync("live.connect", new JsonObject { ["pid"] = 4242 });
        await accept;
        await Server.AnswerHelloAsync(hello);
        CommandResult result = await connecting.WaitAsync(FakeFeedServer.Patience);
        Assert.True(result.Ok, result.ErrorMessage);
    }

    /// <summary>Drops the daemon and connects to a new one on the same pipe.</summary>
    public async Task ReconnectAsync(int rev)
    {
        await Server.DisposeAsync();
        Server = new FakeFeedServer(PipeName);
        await ConnectAsync(rev);
    }

    public Task<CommandResult> RunAsync(string name, JsonObject? args = null, CommandOrigin origin = CommandOrigin.Cli)
        => RunCoreAsync(name, args, origin);

    private async Task<CommandResult> RunCoreAsync(string name, JsonObject? args, CommandOrigin origin)
    {
        CommandResult result = await Context.Commands.InvokeAsync(name, args, Context.Services, origin, headless: true, CancellationToken.None);
        return result.Data is null ? result : result with { Data = JsonNode.Parse(result.Data.ToJsonString()) };
    }

    /// <summary>Runs <paramref name="name"/>, reads the query it sends, answers it with <paramref name="reply"/> and returns the request and the result.</summary>
    public async Task<(JsonObject Request, CommandResult Result)> AskAsync(string name, JsonObject? args, Func<JsonObject, JsonObject> reply)
    {
        Task<CommandResult> run = RunAsync(name, args);
        JsonObject request = await Server.ReadAsync();
        await Server.WriteAsync(reply(request));
        return (request, await run.WaitAsync(FakeFeedServer.Patience));
    }

    /// <summary>Answers every query the client sends from a background loop until the rig is disposed.</summary>
    public void Respond(Func<JsonObject, JsonObject> reply)
    {
        _responderStop = new CancellationTokenSource();
        CancellationToken stop = _responderStop.Token;
        _responder = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    string? line = await Server.ReadLineAsync();
                    if (line is null)
                    {
                        return;
                    }

                    JsonObject request = JsonNode.Parse(line)!.AsObject();
                    if (request["op"]?.GetValue<string>() == "query")
                    {
                        await Server.WriteAsync(reply(request));
                    }
                }
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
            {
            }
        });
    }

    /// <summary>Sends a frame and waits until the client has published it.</summary>
    public async Task SendFrameAsync(long seq, uint map, params JsonObject[] entities)
    {
        int before = EventCount(EventHub.Topics.LiveFrame);
        await Server.WriteAsync(Wire.Frame(map, 0, seq, entities));
        await Wait.UntilAsync(() => EventCount(EventHub.Topics.LiveFrame) > before, $"frame {seq} published");
        await Wait.UntilAsync(() => Hub.Tail.NewestSeq == seq, $"frame {seq} reached the insight recorder");
    }

    /// <summary>Sends a whole frame line as the world wrote it and waits until the client has published it.</summary>
    public async Task SendFrameLineAsync(long seq, JsonObject frame)
    {
        int before = EventCount(EventHub.Topics.LiveFrame);
        await Server.WriteAsync(frame);
        await Wait.UntilAsync(() => EventCount(EventHub.Topics.LiveFrame) > before, $"frame {seq} published");
        await Wait.UntilAsync(() => Hub.Tail.NewestSeq == seq, $"frame {seq} reached the insight recorder");
    }

    public int EventCount(string topic)
    {
        lock (_events)
        {
            return _events.Count(e => e.Topic == topic);
        }
    }

    public IReadOnlyList<JsonNode?> Events(string topic)
    {
        lock (_events)
        {
            return [.. _events.Where(e => e.Topic == topic).Select(e => e.Data is null ? null : JsonNode.Parse(e.Data.ToJsonString()))];
        }
    }

    public static JsonObject Entity(string guid, string kind, double x, double y, int hp = 100, string mt = "idle", bool? cb = null, uint? d = null)
    {
        JsonObject entity = Wire.Entity(guid, kind, x, y, 5);
        entity["hp"] = hp;
        entity["mt"] = mt;
        if (cb is { } combat)
        {
            entity["cb"] = combat;
        }

        if (d is { } display)
        {
            entity["d"] = display;
        }

        return entity;
    }

    /// <summary>JSON nodes as the host counts them (every object, array and value).</summary>
    public static int NodeCount(JsonNode? node)
    {
        int count = 1;
        switch (node)
        {
            case JsonArray array:
                count += array.Sum(NodeCount);
                break;
            case JsonObject obj:
                count += obj.Sum(p => NodeCount(p.Value));
                break;
        }

        return count;
    }

    public async ValueTask DisposeAsync()
    {
        _responderStop?.Cancel();
        _subscription.Dispose();
        Context.Dispose();
        await Server.DisposeAsync();
        if (_responder is not null)
        {
            await Task.WhenAny(_responder, Task.Delay(TimeSpan.FromSeconds(5)));
        }

        try
        {
            if (Directory.Exists(_stateRoot))
            {
                Directory.Delete(_stateRoot, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>The millisecond clock the insight module reads, set by the test.</summary>
internal sealed class InsightClock
{
    private long _now = 1_000_000;

    public long Value
    {
        get => Interlocked.Read(ref _now);
        set => Interlocked.Exchange(ref _now, value);
    }

    public long Read() => Value;
}
