using System.Diagnostics.Metrics;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Ops.ServerConsole;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Ops.ServerConsole;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>Keep a finished connection's console for <see cref="ScopeGrace"/>, then dispose it (or at stop, whichever is first).</summary>
    public void Retire(ToolConsole console)
    {
        _retired[console] = 0;
        _ = Task.Delay(ScopeGrace, _stopping.Token).ContinueWith(
            _ =>
            {
                if (_retired.TryRemove(console, out byte _))
                {
                    _ = console.DisposeAsync().AsTask();
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>Dispose every retired console (the feed is stopping).</summary>
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        foreach (ToolConsole console in _retired.Keys)
        {
            if (_retired.TryRemove(console, out byte _))
            {
                await console.DisposeAsync().ConfigureAwait(false);
            }
        }

        _stopping.Dispose();
    }

    /// <summary>Log a console line (redacted, like the console's and the control pipe's own log line).</summary>
    public void LogConsole(string client, string line)
        => _logger.LogInformation("Map feed console command: {Line} ({Client})", ConsoleCommandRedactor.Redact(line), client);

    /// <summary>Log and count an injection that reached the session's dispatcher.</summary>
    public void LogInjected(string client, ToolTarget target, uint opcode, int length, InjectVerdict verdict)
    {
        _logger.LogInformation(
            "Map feed inject: {Opcode} ({Length} bytes) as '{Target}' (guid {Guid:X16}{Bot}) from {Client}: {Verdict}",
            PacketToolOpcodes.Name(opcode), length, target.Name, target.Guid, target.Bot ? ", bot" : string.Empty, client, verdict);
        PacketToolsMeters.Count(PacketToolsMeters.Inject, verdict is InjectVerdict.Queued or InjectVerdict.Handled or InjectVerdict.Disconnected);
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
/// ends, after any command still holding itself open has settled (bounded).
/// </summary>
internal sealed class ToolConsole(AsyncServiceScope scope, WorldConsoleExecutor executor, ToolConsoleSink sink) : IAsyncDisposable
{
    public WorldConsoleExecutor Executor { get; } = executor;

    public ToolConsoleSink Sink { get; } = sink;

    /// <summary>The settle task of the last command (a <c>.botctl wait</c> holds itself open).</summary>
    public Task LastSettled { get; set; } = Task.CompletedTask;

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
    private List<string>? _collecting;

    /// <inheritdoc />
    public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

    /// <summary>Start collecting the lines of a command.</summary>
    public void Begin()
    {
        lock (_gate)
        {
            _collecting = [];
        }
    }

    /// <summary>Stop collecting; the lines collected since <see cref="Begin"/>. Later lines go to the late-line callback.</summary>
    public IReadOnlyList<string> End()
    {
        lock (_gate)
        {
            List<string> lines = _collecting ?? [];
            _collecting = null;
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
            if (_collecting is { } lines)
            {
                lines.Add(text);
                return;
            }
        }

        lateLine(text);
    }
}
