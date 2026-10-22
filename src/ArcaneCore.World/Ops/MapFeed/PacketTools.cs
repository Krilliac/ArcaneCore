using ArcaneCore.Kernel.Diagnostics;
using System.Diagnostics.Metrics;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Ops.ServerConsole;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Ops.ServerConsole;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>An online session the tools act on, found by its character's name or guid on the world thread.</summary>
internal sealed record ToolTarget(WorldSession Session, string Name, ulong Guid, bool Bot);

/// <summary>The answer of a target search on the world thread (a box, so "nobody" is a value, not a missing answer).</summary>
internal sealed class ToolLookup(ToolTarget? target)
{
    public ToolTarget? Target { get; } = target;
}

/// <summary>
/// The map feed's packet tools (docs/areas/map-feed.md, "Packet tools"): the parts that do not belong to one connection. Built by
/// <see cref="WorldMapFeed"/> only when <see cref="WorldPacketToolsOptions"/> permits it in the host's environment.
/// <para>
/// Every console line, injection and send is logged (category <see cref="WorldMapFeed.LogCategory"/>, Information) with the connected
/// client, the target, the opcode and the payload length, never the payload; injections and sends are also counted
/// (<see cref="PacketToolsMeters"/>). Refusals are logged at Warning or below. Any thread, except where a member says world thread.
/// </para>
/// </summary>
internal sealed class PacketTools : IAsyncDisposable
{
    /// <summary>How long a finished connection's console scope is kept: a command that replies later (a reload) still resolves services through it.</summary>
    internal static readonly TimeSpan ScopeGrace = TimeSpan.FromMinutes(2);

    private readonly IServiceProvider _services;
    private readonly SessionRegistry _registry;
    private readonly WorldRuntime _world;
    private readonly OpcodeTable _opcodes;
    private readonly WorldSessionOptions _sessionOptions;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ToolConsole, byte> _retired = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _retireGate = new();
    private bool _disposed;

    /// <summary>The services are resolved here, so a host missing one fails when the tools are switched on, not at the first console line.</summary>
    public PacketTools(IServiceProvider services, WorldRuntime world, ILogger logger)
    {
        _services = services;
        _registry = services.GetRequiredService<SessionRegistry>();
        _world = world;
        _opcodes = services.GetRequiredService<OpcodeTable>();
        _sessionOptions = services.GetRequiredService<IOptions<WorldSessionOptions>>().Value;
        _loggerFactory = services.GetRequiredService<ILoggerFactory>();
        _logger = logger;
    }

    /// <summary>
    /// The online character named <paramref name="name"/> (case-insensitive) or with player guid <paramref name="guid"/>, real client or
    /// playerbot. World thread only: a session's player is written there.
    /// </summary>
    public ToolLookup Find(string? name, ulong guid)
    {
        foreach (WorldSession session in _registry.Sessions)
        {
            if (session.Player is not { } player || session.State == SessionState.Closed)
            {
                continue;
            }

            bool match = name is not null
                ? string.Equals(player.Name, name, StringComparison.OrdinalIgnoreCase)
                : player.Guid.Value == guid;
            if (match)
            {
                return new ToolLookup(new ToolTarget(session, player.Name, player.Guid.Value, session.IsPlayerbot));
            }
        }

        return new ToolLookup(null);
    }

    /// <summary>Why an injection of <paramref name="opcode"/> with <paramref name="length"/> payload bytes is refused before any session is touched, or null.</summary>
    public static FeedError? CheckInject(uint opcode, int length)
    {
        if (!PacketToolOpcodes.IsKnown(opcode))
        {
            return new FeedError(MapFeedProtocol.UnknownOpcode, $"0x{opcode:X} is not an opcode of the build-5875 table");
        }

        string name = PacketToolOpcodes.Name(opcode);
        if (!PacketToolOpcodes.IsClientToServer(opcode))
        {
            return new FeedError(MapFeedProtocol.WrongDirection, $"{name} is a server opcode; inject takes client opcodes (CMSG_, MSG_)");
        }

        if (PacketToolOpcodes.RefusedInject.Contains((WorldOpcode)opcode))
        {
            return new FeedError(MapFeedProtocol.LifecycleOpcode, $"{name} is an authentication or session-lifecycle opcode; the tools never inject it");
        }

        return length > PacketToolOpcodes.MaxInjectPayload
            ? new FeedError(MapFeedProtocol.Oversize, $"a {length}-byte payload is larger than any client packet ({PacketToolOpcodes.MaxInjectPayload} bytes at most)")
            : null;
    }

    /// <summary>Why a send of <paramref name="opcode"/> with <paramref name="length"/> payload bytes is refused before any session is touched, or null.</summary>
    public static FeedError? CheckSend(uint opcode, int length)
    {
        if (!PacketToolOpcodes.IsKnown(opcode))
        {
            return new FeedError(MapFeedProtocol.UnknownOpcode, $"0x{opcode:X} is not an opcode of the build-5875 table");
        }

        string name = PacketToolOpcodes.Name(opcode);
        if (!PacketToolOpcodes.IsServerToClient(opcode))
        {
            return new FeedError(MapFeedProtocol.WrongDirection, $"{name} is a client opcode; send takes server opcodes (SMSG_, MSG_)");
        }

        if (PacketToolOpcodes.RefusedSend.Contains((WorldOpcode)opcode))
        {
            return new FeedError(MapFeedProtocol.LifecycleOpcode, $"{name} is an authentication or session-lifecycle opcode; the tools never send it");
        }

        return length > PacketToolOpcodes.MaxSendPayload
            ? new FeedError(MapFeedProtocol.Oversize, $"a {length}-byte payload is larger than the tools send ({PacketToolOpcodes.MaxSendPayload} bytes at most)")
            : null;
    }

    /// <summary>A console for one connection: its own DI scope and console session, replies collected by a <see cref="ToolConsoleSink"/>.</summary>
    public ToolConsole CreateConsole(Action<string> lateLine)
    {
        AsyncServiceScope scope = _services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        var sink = new ToolConsoleSink(lateLine);
        WorldConsoleExecutor executor = WorldConsoleExecutor.Create(
            scope.ServiceProvider,
            _opcodes,
            _world,
            _registry,
            _sessionOptions,
            _loggerFactory.CreateLogger<WorldSession>(),
            sink);
        return new ToolConsole(scope, executor, sink);
    }

    /// <summary>
    /// Keep a finished connection's console until its last command settled (waited for at most <see cref="ScopeGrace"/>) and then for
    /// <see cref="ScopeGrace"/> more, then dispose it (or at stop, whichever is first).
    /// </summary>
    public void Retire(ToolConsole console)
    {
        bool disposed;
        lock (_retireGate)
        {
            // Under the gate with DisposeAsync's flag set: a console added here is either swept by DisposeAsync (it sets the flag after
            // this add, then sweeps) or seen as disposed below and disposed at once, never left behind.
            disposed = _disposed;
            if (!disposed)
            {
                _retired[console] = 0;
            }
        }

        if (disposed)
        {
            _ = DisposeObservedAsync(console);
            return;
        }

        _ = RetireAsync(console);
    }

    /// <summary>Dispose a console, logging a fault instead of losing it (Retire's and the sweep's disposals have no caller to throw to).</summary>
    private async Task DisposeObservedAsync(ToolConsole console)
    {
        try
        {
            await console.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("ops.packet-tools", ex);
            _logger.LogWarning(ex, "Map feed: a console failed to dispose");
        }
    }

    private async Task RetireAsync(ToolConsole console)
    {
        try
        {
            // A command that holds itself open (.botctl wait) still uses the scope; a command that replies later (a reload) gets the grace.
            await console.LastSettled.WaitAsync(ScopeGrace, _stopping.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await Task.Delay(ScopeGrace, _stopping.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        catch (ObjectDisposedException)
        {
            // The tools were disposed: DisposeAsync disposes every retired console.
            return;
        }

        if (_retired.TryRemove(console, out byte _))
        {
            await DisposeObservedAsync(console).ConfigureAwait(false);
        }
    }

    /// <summary>Dispose every retired console (the feed is stopping); a console that faults to dispose is logged and does not stop the sweep.</summary>
    public async ValueTask DisposeAsync()
    {
        lock (_retireGate)
        {
            _disposed = true;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        foreach (ToolConsole console in _retired.Keys)
        {
            if (_retired.TryRemove(console, out byte _))
            {
                await DisposeObservedAsync(console).ConfigureAwait(false);
            }
        }

        _stopping.Dispose();
    }

    /// <summary>Log a console line (redacted, like the console's and the control pipe's own log line).</summary>
    public void LogConsole(string client, string line)
        => _logger.LogInformation("Map feed console command: {Line} ({Client})", ConsoleCommandRedactor.Redact(line), client);

    /// <summary>Log an injection request before the session takes it (so a fault or a stall still leaves the audit line); <see cref="LogInjected"/> or a refusal line follows.</summary>
    public void LogInjecting(string client, ToolTarget target, uint opcode, int length)
        => _logger.LogInformation(
            "Map feed inject requested: {Opcode} ({Length} bytes) as '{Target}' (guid {Guid:X16}{Bot}) from {Client}",
            PacketToolOpcodes.Name(opcode), length, target.Name, target.Guid, target.Bot ? ", bot" : string.Empty, client);

    /// <summary>Log and count an injection that reached the session's dispatcher, with its verdict.</summary>
    public void LogInjected(string client, ToolTarget target, uint opcode, int length, InjectVerdict verdict)
    {
        _logger.LogInformation(
            "Map feed inject: {Opcode} ({Length} bytes) as '{Target}' (guid {Guid:X16}{Bot}) from {Client}: {Verdict}",
            PacketToolOpcodes.Name(opcode), length, target.Name, target.Guid, target.Bot ? ", bot" : string.Empty, client, verdict);
        PacketToolsMeters.Count(PacketToolsMeters.Inject, verdict is InjectVerdict.Queued or InjectVerdict.Handled or InjectVerdict.Dropped or InjectVerdict.Disconnected);
    }

    /// <summary>Log and count a send that was queued to the session's client.</summary>
    public void LogSent(string client, ToolTarget target, uint opcode, int length)
    {
        _logger.LogInformation(
            "Map feed send: {Opcode} ({Length} bytes) to '{Target}' (guid {Guid:X16}{Bot}) from {Client}",
            PacketToolOpcodes.Name(opcode), length, target.Name, target.Guid, target.Bot ? ", bot" : string.Empty, client);
        PacketToolsMeters.Count(PacketToolsMeters.Send, accepted: true);
    }

    /// <summary>Log and count a refused injection or send (Warning: a tool's mistake, never a server fault).</summary>
    public void LogRefused(string client, string op, uint opcode, int length, string code, string message)
    {
        _logger.LogWarning(
            "Map feed {Op} refused: {Opcode} ({Length} bytes) from {Client}: {Code}: {Message}",
            op, PacketToolOpcodes.Name(opcode), length, client, code, message);
        PacketToolsMeters.Count(op, accepted: false);
    }

    /// <summary>Log a tap's start.</summary>
    public void LogTap(string client, ToolTarget target, long tapId, TapDirections directions, int maxPackets, int seconds)
        => _logger.LogInformation(
            "Map feed tap {Tap}: watching '{Target}' (guid {Guid:X16}{Bot}, {Directions}, at most {MaxPackets} packets or {Seconds} s) for {Client}",
            tapId, target.Name, target.Guid, target.Bot ? ", bot" : string.Empty, directions, maxPackets, seconds, client);
}

/// <summary>
/// Whether the packet tools are on in this host. Sessions read it when they are built, so only they pay for the dispatcher gate.
/// <see cref="Configured"/> is decided from the options and the host environment when the permit is built, before any session exists:
/// the world host restores playerbots in its own start, which runs before the map feed's, so a permit that only the feed's start set would
/// leave every restored bot without a gate (not injectable) for the life of the process. <see cref="Granted"/> is the feed's own decision
/// once it serves (also the only one in hosts without an <see cref="IHostEnvironment"/>, such as the test worlds).
/// </summary>
internal sealed class PacketToolsPermit
{
    private volatile bool _granted;

    public PacketToolsPermit()
    {
    }

    [ActivatorUtilitiesConstructor]
    public PacketToolsPermit(IOptions<WorldPacketToolsOptions> tools, IOptions<WorldMapFeedOptions> feed, IHostEnvironment? environment = null)
        : this(tools, feed, environment, WorldPacketToolsOptions.IsLiveServer())
    {
    }

    /// <summary>As the public constructor, with the live-server decision given (<see cref="WorldPacketToolsOptions.IsLiveServer"/> in production). Not public: DI must not see it.</summary>
    internal PacketToolsPermit(IOptions<WorldPacketToolsOptions> tools, IOptions<WorldMapFeedOptions> feed, IHostEnvironment? environment, bool liveServer)
    {
        Configured = environment is not null && feed.Value.Enabled && tools.Value.PermitsEnvironment(environment.EnvironmentName, liveServer);
    }

    /// <summary>The options and the host environment permit the tools (the feed may still fail to serve; then nothing can reach a gate).</summary>
    public bool Configured { get; }

    /// <summary>The map feed is serving with the tools on (set by <see cref="WorldMapFeed"/>).</summary>
    public bool Granted
    {
        get => _granted;
        set => _granted = value;
    }

    /// <summary>Whether a session built now gets the dispatcher gate.</summary>
    public bool GatesSessions => Configured || _granted;
}

/// <summary>The packet tools' instruments (docs/ops/metrics.md): <c>arcanecore.tools.packets{op,result}</c>.</summary>
internal static class PacketToolsMeters
{
    public const string Inject = "inject";

    public const string Send = "send";

    private static readonly Meter Meter = new("ArcaneCore.Tools", "1.0");

    /// <summary>Injections and sends by the packet tools: <c>op</c> inject or send, <c>result</c> accepted or refused.</summary>
    public static Counter<long> Packets { get; } = Meter.CreateCounter<long>("arcanecore.tools.packets", "{packet}", "Packets injected or sent by the map feed's packet tools.");

    public static void Count(string op, bool accepted)
        => Packets.Add(1, new KeyValuePair<string, object?>("op", op), new KeyValuePair<string, object?>("result", accepted ? "accepted" : "refused"));
}

/// <summary>
/// One connection's console: the scope its console session lives in, the executor and the reply sink. Disposed by the connection when it
/// ends, after any command still holding itself open has settled (bounded, <see cref="PacketTools.Retire"/>).
/// </summary>
internal sealed class ToolConsole(AsyncServiceScope scope, WorldConsoleExecutor executor, ToolConsoleSink sink) : IAsyncDisposable
{
    public WorldConsoleExecutor Executor { get; } = executor;

    public ToolConsoleSink Sink { get; } = sink;

    /// <summary>The settle task of the last command (a <c>.botctl wait</c> holds itself open); <see cref="PacketTools.Retire"/> waits for it, bounded, before disposing.</summary>
    public volatile Task LastSettled = Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await scope.DisposeAsync().ConfigureAwait(false);
        Sink.Dispose();
    }
}

/// <summary>
/// The console's reply sink for a tool: while a command runs, its lines are collected for the <c>result</c>; afterwards, a line a command
/// prints later (a reload's result) goes to the connection as a <c>consoleLine</c>. The console prefix is dropped. Thread-safe; never blocks.
/// </summary>
internal sealed class ToolConsoleSink(Action<string> lateLine) : TextWriter
{
    private readonly System.Text.StringBuilder _partial = new();
    private readonly Lock _gate = new();
    private readonly AsyncLocal<long> _runTag = new();
    private List<string>? _collecting;
    private long _collectingId;
    private long _lastId;

    /// <inheritdoc />
    public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

    /// <summary>Start collecting the lines of a command; the request's tag, for <see cref="Enter"/>.</summary>
    public long Begin()
    {
        lock (_gate)
        {
            _collecting = [];
            _collectingId = ++_lastId;
            return _collectingId;
        }
    }

    /// <summary>
    /// The command of request <paramref name="id"/> starts running (on the world thread): its output, and the output of whatever it
    /// starts, is tagged with the request, so a command that runs after its request ended can never fill a later request's result.
    /// False when the request already ended (the command must not run). Pair with <see cref="Exit"/>.
    /// </summary>
    public bool Enter(long id)
    {
        lock (_gate)
        {
            if (_collecting is null || _collectingId != id)
            {
                return false;
            }

            _runTag.Value = id;
            return true;
        }
    }

    /// <summary>The synchronous part of the command ended.</summary>
    public void Exit() => _runTag.Value = 0;

    /// <summary>Stop collecting; the lines collected since <see cref="Begin"/>. Later lines go to the late-line callback.</summary>
    public IReadOnlyList<string> End()
    {
        lock (_gate)
        {
            List<string> lines = _collecting ?? [];
            _collecting = null;
            _collectingId = 0;
            return lines;
        }
    }

    /// <inheritdoc />
    public override void Write(char value)
    {
        string? line = null;
        lock (_gate)
        {
            if (value == '\n')
            {
                line = _partial.ToString().TrimEnd('\r');
                _partial.Clear();
            }
            else
            {
                _partial.Append(value);
            }
        }

        if (line is not null)
        {
            Add(line);
        }
    }

    /// <inheritdoc />
    public override void WriteLine(string? value)
    {
        string line;
        lock (_gate)
        {
            line = _partial.Length == 0 ? value ?? string.Empty : _partial.Append(value).ToString();
            _partial.Clear();
        }

        Add(line);
    }

    private void Add(string line)
    {
        string text = line.StartsWith(WorldConsoleExecutor.Prefix, StringComparison.Ordinal) ? line[WorldConsoleExecutor.Prefix.Length..] : line;
        lock (_gate)
        {
            long tag = _runTag.Value;
            if (_collecting is { } lines && (tag == 0 || tag == _collectingId))
            {
                lines.Add(text);
                return;
            }
        }

        lateLine(text);
    }
}
