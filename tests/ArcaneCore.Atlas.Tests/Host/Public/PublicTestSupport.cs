using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Host.Public;
using ArcaneCore.Atlas.Live;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Atlas.Tests.Host.Public;

/// <summary>A clock the test moves by hand. Timestamps tick at 10 000 000 per second like <see cref="TimeSpan.TicksPerSecond"/>.</summary>
internal sealed class ManualTime : TimeProvider
{
    private long _ticks;

    public ManualTime(DateTimeOffset? start = null)
    {
        Start = start ?? new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    }

    public DateTimeOffset Start { get; }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public override DateTimeOffset GetUtcNow() => Start + TimeSpan.FromTicks(Interlocked.Read(ref _ticks));

    public void Advance(TimeSpan span) => Interlocked.Add(ref _ticks, span.Ticks);
}

/// <summary>One captured log line.</summary>
internal sealed record LogLine(string Category, LogLevel Level, string Message);

/// <summary>A logger factory that keeps every line (the listener lines and refusals are asserted from it).</summary>
internal sealed class CapturingLoggers : ILoggerFactory
{
    private readonly ConcurrentQueue<LogLine> _lines = new();

    public IReadOnlyList<LogLine> Lines => [.. _lines];

    public IReadOnlyList<string> Messages(LogLevel? level = null) => [.. _lines.Where(l => level is null || l.Level == level).Select(l => l.Message)];

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(CapturingLoggers owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => owner._lines.Enqueue(new LogLine(category, logLevel, formatter(state, exception)));
    }
}

/// <summary>Builders for the entities and frames the projector tests use.</summary>
internal static class Frames
{
    public static LiveEntity Entity(ulong id, LiveKind kind, string? name, double x, double y, double z = 10, string? motion = null, ulong? target = null, uint entry = 0, double? hp = 100, WorldPoint? home = null)
        => new(id, kind, entry, name, 0, 0, new WorldPoint(x, y, z), 1.5, hp, motion, target, home, null, null);

    public static LiveFrame Frame(long seq, params LiveEntity[] entities)
        => new(seq, seq * 500, 0, 0, [], null, entities, 0);

    public static JsonObject LinksAnswer(IEnumerable<ulong> linked, IEnumerable<uint>? summonEntries = null, IEnumerable<uint>? ownedObjectEntries = null)
        => new()
        {
            ["op"] = "result",
            ["ok"] = true,
            ["kind"] = "links",
            ["linked"] = new JsonArray([.. linked.Select(g => (JsonNode?)JsonValue.Create(g.ToString("X", System.Globalization.CultureInfo.InvariantCulture)))]),
            ["summonEntries"] = new JsonArray([.. (summonEntries ?? []).Select(e => (JsonNode?)JsonValue.Create(e))]),
            ["ownedObjectEntries"] = new JsonArray([.. (ownedObjectEntries ?? []).Select(e => (JsonNode?)JsonValue.Create(e))]),
        };
}

/// <summary>Free ports for listeners that must be told a fixed port.</summary>
internal static class FreePort
{
    public static int Next()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}

/// <summary>An in-memory world link for server and hub tests: scripted logins, recorded tickets and demand.</summary>
internal sealed class FakeWorld : IPublicWorld
{
    private readonly ConcurrentQueue<string> _ended = new();

    public bool Connected { get; set; } = true;

    /// <summary>Accounts that may log in: name to (password, security).</summary>
    public Dictionary<string, (string Password, int Security)> Accounts { get; } = new(StringComparer.OrdinalIgnoreCase);

    public WorldLoginKind OtherwiseKind { get; set; } = WorldLoginKind.Failed;

    /// <summary>When set, <see cref="LoginAsync"/> throws it (a world link that fails in an unexpected way).</summary>
    public Exception? LoginThrows { get; set; }

    public List<(string User, string Ip, int Floor)> Logins { get; } = [];

    public IReadOnlyList<string> EndedTickets => [.. _ended];

    public List<uint> Demanded { get; } = [];

    public int ActiveDemand { get; private set; }

    public JsonObject? CharactersAnswer { get; set; }

    public JsonObject? InfoAnswer { get; set; }

    public List<ulong> InfoAsked { get; } = [];

    public Task<WorldLoginResult> LoginAsync(string user, string password, string? code, string clientIp, int floor, CancellationToken cancellationToken)
    {
        lock (Logins)
        {
            Logins.Add((user, clientIp, floor));
        }

        if (LoginThrows is not null)
        {
            throw LoginThrows;
        }

        if (Accounts.TryGetValue(user, out (string Password, int Security) account) && account.Password == password && account.Security >= floor)
        {
            return Task.FromResult(new WorldLoginResult(WorldLoginKind.Ok, "ticket-" + Guid.NewGuid().ToString("N"), user.ToUpperInvariant(), account.Security));
        }

        return Task.FromResult(new WorldLoginResult(OtherwiseKind));
    }

    public Task EndTicketAsync(string ticket, bool kick, CancellationToken cancellationToken)
    {
        _ended.Enqueue(kick ? "kick:" + ticket : ticket);
        return Task.CompletedTask;
    }

    public Task<JsonObject?> CharactersAsync(string ticket, CancellationToken cancellationToken) => Task.FromResult(CharactersAnswer);

    public Task<JsonObject?> InfoPublicAsync(ulong guid, bool showPlayers, bool showNames, CancellationToken cancellationToken)
    {
        lock (InfoAsked)
        {
            InfoAsked.Add(guid);
        }

        return Task.FromResult(InfoAnswer);
    }

    public ValueTask<IDisposable?> DemandAsync(uint map, CancellationToken cancellationToken)
    {
        lock (Demanded)
        {
            Demanded.Add(map);
            ActiveDemand++;
        }

        return ValueTask.FromResult<IDisposable?>(new Release(this));
    }

    private sealed class Release(FakeWorld world) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                lock (world.Demanded)
                {
                    world.ActiveDemand--;
                }
            }
        }
    }
}

/// <summary>
/// A fake world feed for the pool: it records every message in order, answers <c>role</c>, <c>links</c>, <c>login</c> and the other request ops, and lets
/// the test push frames or close the connection.
/// </summary>
internal sealed class FakeFeed : IPublicFeedConnector
{
    public bool Tools { get; set; }

    public bool LinksSupported { get; set; } = true;

    public IEnumerable<ulong> Linked { get; set; } = [];

    public IEnumerable<uint> SummonEntries { get; set; } = [];

    public JsonObject? LoginAnswer { get; set; }

    public JsonObject? RoleAnswer { get; set; }

    /// <summary>What the world's <c>caps</c> says about its action channel (<c>actions</c>); the default is a world with the channel off.</summary>
    public bool CapsActions { get; set; }

    /// <summary>What the world's <c>caps</c> says about the live-server marker (<c>liveServer</c>).</summary>
    public bool CapsLiveServer { get; set; }

    /// <summary>Replaces the whole <c>caps</c> answer (a broken or old world).</summary>
    public JsonObject? CapsAnswer { get; set; }

    public List<FakeConnection> Opened { get; } = [];

    public int OpenFailures { get; set; }

    public Task<IPublicFeedConnection> OpenAsync(IPublicFeedSink sink, CancellationToken cancellationToken)
    {
        if (OpenFailures > 0)
        {
            OpenFailures--;
            throw new ArcaneCore.Atlas.Core.Commands.CommandException(ArcaneCore.Atlas.Core.Commands.CommandException.Unavailable, "nothing answered");
        }

        var connection = new FakeConnection(this, sink, Opened.Count + 1);
        Opened.Add(connection);
        return Task.FromResult<IPublicFeedConnection>(connection);
    }
}

internal sealed class FakeConnection(FakeFeed feed, IPublicFeedSink sink, long id) : IPublicFeedConnection
{
    private readonly List<JsonObject> _sent = [];
    private bool _open = true;

    public long Id { get; } = id;

    public HelloInfo Hello { get; } = new(1, "abc", 4242, [], 0, null, feed.Tools);

    public bool IsOpen => _open;

    public long ServerErrors { get; set; }

    /// <summary>Everything sent on the connection, requests and one-way messages, in order.</summary>
    public IReadOnlyList<JsonObject> Sent
    {
        get
        {
            lock (_sent)
            {
                return [.. _sent];
            }
        }
    }

    public IEnumerable<string> Ops => Sent.Select(m => m["op"]!.GetValue<string>());

    public Task<JsonObject> RequestAsync(JsonObject message, TimeSpan timeout, CancellationToken cancellationToken)
    {
        lock (_sent)
        {
            _sent.Add((JsonObject)message.DeepClone());
        }

        string op = message["op"]!.GetValue<string>();
        JsonObject answer = op switch
        {
            "role" => feed.RoleAnswer ?? new JsonObject { ["op"] = "result", ["ok"] = true, ["kind"] = "role", ["role"] = "web" },
            "links" when feed.LinksSupported => Frames.LinksAnswer(feed.Linked, feed.SummonEntries),
            "links" => throw new ArcaneCore.Atlas.Core.Commands.CommandException(ArcaneCore.Atlas.Core.Commands.CommandException.Failed, "unknown-op"),
            "caps" => feed.CapsAnswer ?? new JsonObject { ["op"] = "result", ["ok"] = true, ["kind"] = "caps", ["actions"] = feed.CapsActions, ["login"] = true, ["liveServer"] = feed.CapsLiveServer, ["role"] = "web", ["templates"] = new JsonArray() },
            "login" => feed.LoginAnswer ?? new JsonObject { ["op"] = "result", ["ok"] = false, ["kind"] = "login", ["code"] = "login-failed" },
            _ => new JsonObject { ["op"] = "result", ["ok"] = true, ["kind"] = op },
        };
        return Task.FromResult(answer);
    }

    public Task SendAsync(JsonObject message, CancellationToken cancellationToken)
    {
        lock (_sent)
        {
            _sent.Add((JsonObject)message.DeepClone());
        }

        return Task.CompletedTask;
    }

    public void PushFrame(LiveFrame frame) => sink.Frame(this, new ParsedFrame(frame, new JsonObject(), 0, null));

    public void Close(string reason)
    {
        _open = false;
        sink.Closed(this, reason);
    }

    public void Dispose() => _open = false;
}
