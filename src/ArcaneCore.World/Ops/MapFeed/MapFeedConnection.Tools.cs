using ArcaneCore.Kernel.Diagnostics;
using System.Threading.Channels;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Net;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>
/// The packet tools of one connection (docs/areas/map-feed.md, "Packet tools"): <c>console</c>, <c>inject</c>, <c>send</c>, <c>tap</c> and
/// <c>untap</c>. Every request is answered by a <c>result</c> of its id; a refusal (<c>ok:false</c> with a code) is an answer, not a strike.
/// The reader handles one request at a time; a tap's packets are written by its own pump task, so a busy tap never holds the reader.
/// </summary>
internal sealed partial class MapFeedConnection
{
    /// <summary>How long a console command that holds itself open (<c>.botctl wait</c>) is waited for before its result goes out.</summary>
    internal static readonly TimeSpan ConsoleSettleWait = TimeSpan.FromMinutes(2);

    /// <summary>How long an injection waits for the session's dispatcher (the session's reader may be inside a slow handler).</summary>
    internal static readonly TimeSpan InjectWait = TimeSpan.FromSeconds(5);

    private ToolConsole? _console;
    private long _consoleId;
    private FeedPacketTap? _tap;
    private Task _tapPump = Task.CompletedTask;

    private async Task<Outcome> HandleToolAsync(FeedRequest request)
    {
        if (_feed.Tools is not { } tools)
        {
            return await RefuseAsync(request, new FeedError(
                MapFeedProtocol.ToolsOff, "the packet tools are off on this daemon (World:Control:PacketTools; the hello has no \"tools\":true)")).ConfigureAwait(false);
        }

        switch (request.Op)
        {
            case FeedOp.Console:
                await ConsoleAsync(tools, request).ConfigureAwait(false);
                break;
            case FeedOp.Inject:
                await InjectAsync(tools, request).ConfigureAwait(false);
                break;
            case FeedOp.Send:
                await SendAsync(tools, request).ConfigureAwait(false);
                break;
            case FeedOp.Tap:
                await TapAsync(tools, request).ConfigureAwait(false);
                break;
            default:
                await UntapAsync(request).ConfigureAwait(false);
                break;
        }

        return Outcome.Served;
    }

    private async Task ConsoleAsync(PacketTools tools, FeedRequest request)
    {
        tools.LogConsole(Client, request.Line!);
        _console ??= tools.CreateConsole(LateConsoleLine);
        ToolConsole console = _console;
        Volatile.Write(ref _consoleId, request.Id);
        long requestTag = console.Sink.Begin();
        (ConsoleRun? run, FeedError? failure) = await InvokeWorldAsync(() =>
        {
            if (!console.Sink.Enter(requestTag))
            {
                // The request gave up waiting for the world thread (its refusal is out): the command must not run, nor print into a later request.
                return new ConsoleRun(null);
            }

            CommandContext? context = null;
            try
            {
                context = console.Executor.Run(request.Line!);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Like the console window: a command that throws fails that command, not the connection.
                FaultLedger.Shared.Record("ops.map-feed", ex);
                _logger.LogError(ex, "map feed console command could not be run ({Client})", Client);
                console.Executor.WriteLine("The command failed; see the server log.");
            }
            finally
            {
                console.Sink.Exit();
            }

            return new ConsoleRun(context);
        }).ConfigureAwait(false);

        if (run is null)
        {
            console.Sink.End();
            await QueueAsync(PacketToolsJson.Refused(request.Id, "console", failure!.Value.Code, failure.Value.Message)).ConfigureAwait(false);
            return;
        }

        Task settled = run.Context?.WhenSettled ?? Task.CompletedTask;
        console.LastSettled = settled;
        if (!settled.IsCompleted)
        {
            try
            {
                await settled.WaitAsync(ConsoleSettleWait, _lifetime.Token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Its later lines follow as consoleLine.
            }
        }

        IReadOnlyList<string> lines = console.Sink.End();
        await QueueAsync(PacketToolsJson.Console(request.Id, lines, settled.IsCompleted, run.Context?.Failure)).ConfigureAwait(false);
    }

    /// <summary>A console line printed after its command's result (a reload's outcome): sent if the queue has room, else lost.</summary>
    private void LateConsoleLine(string line)
        => _outbound.Writer.TryWrite(new Outbound(OutboundKind.Line, null, PacketToolsJson.ConsoleLine(Volatile.Read(ref _consoleId), line), 0, 0));

    private async Task InjectAsync(PacketTools tools, FeedRequest request)
    {
        byte[] payload = request.Payload ?? [];
        if (PacketTools.CheckInject(request.Opcode, payload.Length) is { } refused)
        {
            await RefuseToolAsync(tools, request, "inject", refused).ConfigureAwait(false);
            return;
        }

        if (await FindAsync(tools, request).ConfigureAwait(false) is not { } target)
        {
            return;
        }

        bool worldHandler = target.Session.HandlesOnWorldThread((WorldOpcode)request.Opcode);
        tools.LogInjecting(Client, target, request.Opcode, payload.Length);
        // InjectAsync answers a handler fault itself (it ends the session, as the reader would); what it throws is not the handler's.
        InjectVerdict verdict = await target.Session.InjectAsync((WorldOpcode)request.Opcode, payload, InjectWait, _lifetime.Token).ConfigureAwait(false);

        FeedError? refusal = verdict switch
        {
            InjectVerdict.NoHandler => new FeedError(MapFeedProtocol.NoHandler, $"the server has no handler for {PacketToolOpcodes.Name(request.Opcode)}"),
            InjectVerdict.WrongState => new FeedError(MapFeedProtocol.WrongState, $"{PacketToolOpcodes.Name(request.Opcode)} is not handled in the session's state"),
            InjectVerdict.NotInjectable => new FeedError(
                MapFeedProtocol.NotInjectable,
                target.Bot ? $"{PacketToolOpcodes.Name(request.Opcode)} runs on a session task, which a playerbot does not have" : "the session was built before the packet tools were on"),
            InjectVerdict.Busy => new FeedError(MapFeedProtocol.Busy, $"the session's dispatcher stayed busy for {(int)InjectWait.TotalSeconds} s"),
            InjectVerdict.Closed => new FeedError(MapFeedProtocol.Offline, $"'{target.Name}' logged out"),
            _ => null,
        };
        if (refusal is { } error)
        {
            await RefuseToolAsync(tools, request, "inject", error).ConfigureAwait(false);
            return;
        }

        tools.LogInjected(Client, target, request.Opcode, payload.Length, verdict);
        await QueueAsync(PacketToolsJson.Injected(request.Id, target, request.Opcode, payload.Length, verdict, worldHandler)).ConfigureAwait(false);
    }

    private async Task SendAsync(PacketTools tools, FeedRequest request)
    {
        byte[] payload = request.Payload ?? [];
        if (PacketTools.CheckSend(request.Opcode, payload.Length) is { } refused)
        {
            await RefuseToolAsync(tools, request, "send", refused).ConfigureAwait(false);
            return;
        }

        if (await FindAsync(tools, request).ConfigureAwait(false) is not { } target)
        {
            return;
        }

        if (!target.Session.SendFromTools((WorldOpcode)request.Opcode, payload))
        {
            await RefuseToolAsync(tools, request, "send", new FeedError(MapFeedProtocol.Offline, $"'{target.Name}' logged out")).ConfigureAwait(false);
            return;
        }

        tools.LogSent(Client, target, request.Opcode, payload.Length);
        await QueueAsync(PacketToolsJson.Sent(request.Id, target, request.Opcode, payload.Length)).ConfigureAwait(false);
    }

    private async Task TapAsync(PacketTools tools, FeedRequest request)
    {
        if (_tap is not null && !_tapPump.IsCompleted)
        {
            await QueueToolRefusalAsync(request, "tap", new FeedError(MapFeedProtocol.TapBusy, $"this connection already taps (tap {_tap.Id}); untap it first")).ConfigureAwait(false);
            return;
        }

        if (await FindAsync(tools, request).ConfigureAwait(false) is not { } target)
        {
            return;
        }

        var tap = new FeedPacketTap(request.Id, target, request.Directions, request.TapOpcodes, request.MaxPackets, request.MaxBytes);
        if (!target.Session.AttachTap(tap))
        {
            await QueueToolRefusalAsync(request, "tap", new FeedError(MapFeedProtocol.Offline, $"'{target.Name}' logged out")).ConfigureAwait(false);
            return;
        }

        _tap = tap;
        tools.LogTap(Client, target, tap.Id, request.Directions, request.MaxPackets, request.Seconds);
        try
        {
            await QueueAsync(PacketToolsJson.TapStarted(request.Id, tap, request.MaxPackets, request.Seconds)).ConfigureAwait(false);
        }
        catch
        {
            // No pump will run to detach it: do it here.
            tap.End("closed");
            target.Session.DetachTap(tap);
            _tap = null;
            throw;
        }

        TimeSpan life = TimeSpan.FromSeconds(request.Seconds);
        _tapPump = Task.Run(() => PumpTapAsync(tap, life));
    }

    private async Task UntapAsync(FeedRequest request)
    {
        if (_tap is not { } tap || _tapPump.IsCompleted)
        {
            await QueueToolRefusalAsync(request, "untap", new FeedError(MapFeedProtocol.NoTap, "this connection has no running tap")).ConfigureAwait(false);
            return;
        }

        // The result goes out first; the pump's tapEnd follows it.
        await QueueAsync(PacketToolsJson.Untapped(request.Id, tap.Id)).ConfigureAwait(false);
        tap.End("stopped");
        await _tapPump.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    /// <summary>
    /// Write a tap's packets until it ends (its count, its time, an untap, the session closing or the connection ending), then its
    /// <c>tapEnd</c>. The tap is detached from the session however this ends.
    /// </summary>
    private async Task PumpTapAsync(FeedPacketTap tap, TimeSpan life)
    {
        long sent = 0;
        try
        {
            using (CancellationTokenSource timer = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token))
            {
                timer.CancelAfter(life);
                try
                {
                    await foreach (TapPacket packet in tap.Reader.ReadAllAsync(timer.Token).ConfigureAwait(false))
                    {
                        await QueueAsync(PacketToolsJson.Packet(tap.Id, packet)).ConfigureAwait(false);
                        sent++;
                    }
                }
                catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
                {
                    tap.End("time");
                }
            }

            // What was queued before the end still goes out.
            while (tap.Reader.TryRead(out TapPacket packet))
            {
                await QueueAsync(PacketToolsJson.Packet(tap.Id, packet)).ConfigureAwait(false);
                sent++;
            }

            await QueueAsync(PacketToolsJson.TapEnd(tap.Id, tap.Reason ?? "closed", sent, tap.Dropped)).ConfigureAwait(false);
            _logger.LogInformation(
                "map feed tap {Tap} ended ({Reason}): {Sent} packets sent, {Dropped} dropped ({Client})", tap.Id, tap.Reason, sent, tap.Dropped, Client);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException)
        {
            // The connection ended: nobody is left to tell.
        }
        finally
        {
            tap.End("closed");
            tap.Target.Session.DetachTap(tap);
        }
    }

    /// <summary>The online target of an inject, send or tap, found on the world thread; null after the refusal was queued.</summary>
    private async Task<ToolTarget?> FindAsync(PacketTools tools, FeedRequest request)
    {
        string kind = request.Op switch
        {
            FeedOp.Inject => "inject",
            FeedOp.Send => "send",
            _ => "tap",
        };
        string? name = request.TargetName;
        ulong guid = request.TargetGuid;
        (ToolLookup? lookup, FeedError? failure) = await InvokeWorldAsync(() => tools.Find(name, guid)).ConfigureAwait(false);
        if (lookup is null)
        {
            await RefuseToolAsync(tools, request, kind, failure!.Value).ConfigureAwait(false);
            return null;
        }

        if (lookup.Target is null)
        {
            string who = name is not null ? $"'{name}'" : $"guid {guid:X16}";
            await RefuseToolAsync(tools, request, kind, new FeedError(MapFeedProtocol.Offline, $"no online character is {who}")).ConfigureAwait(false);
            return null;
        }

        return lookup.Target;
    }

    /// <summary>Answer a refused tool request; an injection or send is also logged and counted as refused.</summary>
    private Task RefuseToolAsync(PacketTools tools, FeedRequest request, string kind, FeedError error)
    {
        if (request.Op is FeedOp.Inject or FeedOp.Send)
        {
            tools.LogRefused(Client, kind, request.Opcode, request.Payload?.Length ?? 0, error.Code, error.Message);
        }
        else
        {
            _logger.LogWarning("map feed {Kind} {Id} refused ({Client}): {Code} {Message}", kind, request.Id, Client, error.Code, error.Message);
        }

        return QueueAsync(PacketToolsJson.Refused(request.Id, kind, error.Code, error.Message)).AsTask();
    }

    private Task QueueToolRefusalAsync(FeedRequest request, string kind, FeedError error)
    {
        _logger.LogWarning("map feed {Kind} {Id} refused ({Client}): {Code} {Message}", kind, request.Id, Client, error.Code, error.Message);
        return QueueAsync(PacketToolsJson.Refused(request.Id, kind, error.Code, error.Message)).AsTask();
    }

    /// <summary>The connection is ending: stop its tap and hand its console's scope to the tools for disposal.</summary>
    private async Task EndToolsAsync()
    {
        if (_tap is { } tap)
        {
            tap.End("closed");
            await _tapPump.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        if (_console is { } console)
        {
            _feed.Tools?.Retire(console);
            _console = null;
        }
    }

    private static string KindOf(FeedOp op) => op switch
    {
        FeedOp.Console => "console",
        FeedOp.Inject => "inject",
        FeedOp.Send => "send",
        FeedOp.Tap => "tap",
        _ => "untap",
    };

    /// <summary>A console run's context, boxed for <see cref="InvokeWorldAsync{T}"/> (null: an empty line).</summary>
    private sealed class ConsoleRun(CommandContext? context)
    {
        public CommandContext? Context { get; } = context;
    }
}
