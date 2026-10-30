using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Ops.MapFeed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Net;

/// <summary>
/// Receives a session's packets for the map feed's packet tap (docs/areas/map-feed.md, "Packet tools"). <see cref="Offer"/> is called on
/// the session's reader task, the world thread and any thread that sends, sometimes under the session's send lock: it must copy what it
/// keeps and never block or throw.
/// </summary>
internal interface ISessionPacketTap
{
    /// <summary>One packet: <paramref name="inbound"/> from the client (or injected as if it were), else to it.</summary>
    void Offer(bool inbound, WorldOpcode opcode, ReadOnlySpan<byte> payload, bool injected);

    /// <summary>The session closed; nothing more will be offered.</summary>
    void SessionClosed();
}

/// <summary>What <see cref="WorldSession.InjectAsync"/> did with a packet.</summary>
internal enum InjectVerdict : byte
{
    /// <summary>An in-world opcode: queued on the session's world queue; the world thread handles it in the player's next map update.</summary>
    Queued,

    /// <summary>A session-task opcode: its handler ran to completion, serialized with the session's own reader.</summary>
    Handled,

    /// <summary>The dispatcher refused the packet the way it refuses a client's (malformed, a flood, a full queue), or its session handler faulted the way that ends a client's session, and the session was disconnected.</summary>
    Disconnected,

    /// <summary>The dispatcher dropped the packet over the connection's packet budget, as it drops a client's (Net:Protection:World*); the session stays up.</summary>
    Dropped,

    /// <summary>The server has no handler for the opcode (the dispatcher would count and drop it).</summary>
    NoHandler,

    /// <summary>The handler does not accept the opcode in the session's state (the dispatcher would drop it).</summary>
    WrongState,

    /// <summary>A playerbot session and a session-task opcode (a bot has no session task), or a session built without the packet tools' gate.</summary>
    NotInjectable,

    /// <summary>The session's dispatcher stayed busy for the whole wait.</summary>
    Busy,

    /// <summary>The session is closed or closing.</summary>
    Closed,
}

/// <summary>
/// The packet tools' entry points into a session (docs/areas/map-feed.md, "Packet tools"): injection through the session's own dispatcher,
/// sends through its own send path, and taps on its packet capture point. All of it is inert unless <c>World:Control:PacketTools:Enabled</c>
/// was set when the session was built: only then does the session serialize its dispatcher with a gate an injection can take.
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>
    /// Set on the thread that hands a tool's packet to <see cref="CapturePacket"/> or <see cref="Send(WorldOpcode, ReadOnlySpan{byte})"/>,
    /// so a tap can tell a tool's packet from the client's or the server's own. Only ever true for the length of that synchronous call.
    /// </summary>
    [ThreadStatic]
    private static bool t_toolPacket;

    /// <summary>Set by <see cref="DispatchAsync"/> when it drops a packet over the packet budget; read by an injection under the dispatch gate.</summary>
    private bool _budgetDropped;

    /// <summary>
    /// Serializes <see cref="DispatchAsync"/> between the session's own reader and an injection, so the dispatcher keeps its single-producer
    /// rules (the world-queue bound, the packet limiter, one session handler at a time). Null, and never taken, unless the packet tools were
    /// enabled when the session was built.
    /// </summary>
    private readonly SemaphoreSlim? _dispatchGate;

    private ISessionPacketTap[] _taps = [];

    /// <summary>Whether a packet tap is attached (lean bots then build and keep every packet, so the tap sees what a client would).</summary>
    private bool HasTaps => Volatile.Read(ref _taps).Length != 0;

    /// <summary>
    /// The dispatcher gate when the packet tools are actually permitted (read once, when the session is built): the same decision that
    /// switches the tools on (<see cref="PacketToolsPermit"/>: options, map feed serving, environment), not merely the Enabled option.
    /// </summary>
    private static SemaphoreSlim? CreateDispatchGate(IServiceProvider services)
        => services.GetService<PacketToolsPermit>()?.GatesSessions == true ? new SemaphoreSlim(1, 1) : null;

    /// <summary>
    /// <see cref="DispatchAsync"/> for a packet the client sent (socket or gateway reader), behind the dispatcher gate when there is one.
    /// </summary>
    private async ValueTask<bool> DispatchFromClientAsync(WorldOpcode opcode, byte[] payload, CancellationToken token)
    {
        try
        {
            if (_dispatchGate is not { } gate)
            {
                return await DispatchAsync(opcode, payload).ConfigureAwait(false);
            }

            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                return await DispatchAsync(opcode, payload).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }
        finally
        {
            Interlocked.Increment(ref _inboundDispatched);
        }
    }

    private long _inboundDispatched;

    /// <summary>
    /// Client packets this session has finished dispatching (queued for the world thread, answered by the read loop or dropped), whatever the
    /// outcome. Lets a test host tell that everything a client sent has arrived before it steps the world. Thread-safe.
    /// </summary>
    internal long InboundDispatched => Interlocked.Read(ref _inboundDispatched);

    /// <summary>Packets queued for the world thread and not yet handled (the inbound world queue depth). Thread-safe.</summary>
    internal int InboundWorldQueueDepth => Volatile.Read(ref _queuedPackets);

    /// <summary>
    /// Hand <paramref name="payload"/> to the session's dispatcher as if its client had just sent <paramref name="opcode"/>: the same
    /// <see cref="DispatchAsync"/> the reader calls, behind the same gate, so an in-world opcode lands on the session's world queue and is
    /// handled on the world thread in the player's map update (with every check a client's packet gets), and a session-task opcode runs
    /// its handler here, never alongside the reader. The packet is offered to the taps (marked injected) and to the packet capture first,
    /// exactly as the reader does. Any thread but the world thread (a session handler may wait on it).
    /// </summary>
    internal async Task<InjectVerdict> InjectAsync(WorldOpcode opcode, byte[] payload, TimeSpan wait, CancellationToken token)
    {
        if (_dispatchGate is not { } gate)
        {
            return InjectVerdict.NotInjectable;
        }

        if (!_opcodes.TryGet(opcode, out OpcodeHandler handler))
        {
            return InjectVerdict.NoHandler;
        }

        if (handler.World is null && _managed && !handler.Stripped)
        {
            return InjectVerdict.NotInjectable;
        }

        if (!await gate.WaitAsync(wait, token).ConfigureAwait(false))
        {
            return InjectVerdict.Busy;
        }

        try
        {
            if (_state == SessionState.Closed || _kick.IsCancellationRequested)
            {
                return InjectVerdict.Closed;
            }

            // The dispatcher drops these without a word; a tool is told instead.
            bool accepted = handler.World is not null ? _state is SessionState.LoggingIn or SessionState.InWorld : handler.AllowsState(_state);
            if (!accepted)
            {
                return InjectVerdict.WrongState;
            }

            t_toolPacket = true;
            try
            {
                CapturePacket(true, opcode, payload);
            }
            finally
            {
                t_toolPacket = false;
            }

            bool dispatched;
            _budgetDropped = false;
            _injectDispatch = true; // report only: the packet heuristics attribute findings from this dispatch to the tool, not the target's account
            try
            {
                dispatched = await DispatchAsync(opcode, payload).ConfigureAwait(false);
            }
            catch (InvariantViolationException)
            {
                // The reader's loop ends the session on a broken invariant before it propagates; an injection does too.
                Kick("an injected packet broke an invariant in its session handler");
                throw;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (_kick.IsCancellationRequested)
            {
                // The session's own kick cancelled the handler: the session is already ending, which is not a fault and needs no second kick.
                return InjectVerdict.Closed;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A session handler fault the dispatcher does not contain (a cancellation that is not the caller's included) leaves the
                // reader's loop and ends the session; an injection ends it the same way instead of reporting a failed query with the
                // session still up.
                _logger.LogFault("session", ex, "[{Endpoint}] an injected {Opcode} faulted its handler; disconnecting", RemoteEndpoint, WorldOpcodeNames.GetName(opcode));
                Kick("an injected packet faulted its session handler");
                return InjectVerdict.Disconnected;
            }
            finally
            {
                _injectDispatch = false;
            }

            if (!dispatched)
            {
                // The reader would end the connection here; an injection ends it the same way.
                Kick("an injected packet was refused by the dispatcher");
                return InjectVerdict.Disconnected;
            }

            if (_budgetDropped)
            {
                return InjectVerdict.Dropped;
            }

            if (handler.World is null)
            {
                return InjectVerdict.Handled;
            }

            // Close discards the world queue without taking the gate: a packet enqueued after that discard would sit on a closed session
            // for good. Close sets the state before it discards, so a closed session seen here is discarded again.
            if (_state == SessionState.Closed)
            {
                DiscardQueuedPackets();
            }

            return InjectVerdict.Queued;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Whether <paramref name="opcode"/>'s handler runs on the world thread (from the session's world queue). Any thread.</summary>
    internal bool HandlesOnWorldThread(WorldOpcode opcode) => _opcodes.TryGet(opcode, out OpcodeHandler handler) && handler.World is not null;

    /// <summary>
    /// Queue a tool's server packet to this session's client through <see cref="Send(WorldOpcode, ReadOnlySpan{byte})"/> (header cipher,
    /// outbound bound, observers, capture and taps, all as for the server's own packets). False when the session is closed. Any thread.
    /// </summary>
    internal bool SendFromTools(WorldOpcode opcode, ReadOnlySpan<byte> payload)
    {
        if (_state == SessionState.Closed || _kick.IsCancellationRequested)
        {
            return false;
        }

        t_toolPacket = true;
        try
        {
            Send(opcode, payload);
        }
        finally
        {
            t_toolPacket = false;
        }

        return true;
    }

    /// <summary>Attach a tap; false when the session is already closed. Any thread.</summary>
    internal bool AttachTap(ISessionPacketTap tap)
    {
        lock (_sendLock)
        {
            if (_state == SessionState.Closed)
            {
                return false;
            }

            Volatile.Write(ref _taps, [.. _taps, tap]);
            return true;
        }
    }

    /// <summary>Detach a tap (nothing happens when it is not attached). Any thread.</summary>
    internal void DetachTap(ISessionPacketTap tap)
    {
        lock (_sendLock)
        {
            Volatile.Write(ref _taps, [.. _taps.Where(t => !ReferenceEquals(t, tap))]);
        }
    }

    /// <summary>Hand one packet to every tap (never blocks: a tap copies or drops).</summary>
    private void OfferToTaps(ISessionPacketTap[] taps, bool fromClient, WorldOpcode opcode, ReadOnlySpan<byte> payload)
    {
        bool injected = t_toolPacket;
        foreach (ISessionPacketTap tap in taps)
        {
            tap.Offer(fromClient, opcode, payload, injected);
        }
    }

    /// <summary>The session closed: detach every tap and tell it.</summary>
    private void EndTaps()
    {
        ISessionPacketTap[] taps;
        lock (_sendLock)
        {
            taps = _taps;
            Volatile.Write(ref _taps, []);
        }

        foreach (ISessionPacketTap tap in taps)
        {
            tap.SessionClosed();
        }
    }
}
