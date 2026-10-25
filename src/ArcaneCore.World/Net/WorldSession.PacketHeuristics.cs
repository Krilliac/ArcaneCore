using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.World.AntiCheat.PacketHeuristics;
using ArcaneCore.World.Handlers;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Net;

/// <summary>
/// The session's hooks into the packet heuristics (docs/areas/packet-heuristics.md). Report only: every hook is a call that returns nothing, never
/// throws and never changes what the dispatcher does next, so a packet that dispatched before still dispatches and one that was dropped is still
/// dropped. Resolved once from the connection's services; with the feature absent or off each hook is a null check and one volatile read, and no
/// per-session state exists. The GM-mode snapshot field always exists; what the world-packet hook adds is writing it while the heuristics are off
/// (one flag read, a write only on change). It is cleared when the player leaves the world.
/// </summary>
public sealed partial class WorldSession
{
    private PacketHeuristicsFeature? _heuristics;
    private PacketHeuristicsSession? _heuristicsState;
    private long _leftWorldTick = long.MinValue;
    private volatile bool _heuristicsGm;
    private volatile bool _injectDispatch;
    private volatile bool _injectWorld;

    /// <summary>Where a dropped packet may be a packet in flight: after the player left the world (a timed window), or across a far transfer (the teleport stage, however long the loading screen takes).</summary>
    private enum WrongStateGrace : byte
    {
        None = 0,
        LeftWorld = 1,
        Transfer = 2,
    }

    /// <summary>The player was in GM mode when the world thread last dispatched one of this session's world packets, regardless of whether the heuristics are on (read by the network path to classify the session as staff).</summary>
    internal bool HeuristicsGmMode => _heuristicsGm;

    /// <summary>
    /// Whether this session counts as staff for the heuristics: not a bot and not a packet-tool injection, and either managed, a moderator or above, or in GM mode.
    /// The one predicate <see cref="PacketHeuristicsFeature"/> admission and the dev-opcode audit both use, so they never disagree.
    /// </summary>
    internal bool IsHeuristicsStaff => !IsPlayerbot && !HeuristicsToolPacket && (IsManaged || Security >= Kernel.Accounts.AccountSecurity.Moderator || HeuristicsGmMode);

    /// <summary>
    /// Whether the packet being handled right now was injected by a packet tool (<see cref="InjectAsync"/>). On the world thread that is the world
    /// packet being processed; elsewhere it is the injection holding the dispatch gate. Report only: it picks the traffic kind and subject of a finding.
    /// </summary>
    internal bool HeuristicsToolPacket => World.IsWorldThread ? _injectWorld : _injectDispatch;

    private static bool Within(long startTick, long windowMs) => startTick != long.MinValue && Environment.TickCount64 - startTick < windowMs;

    /// <summary>The player is crossing a far teleport (map null from SMSG_TRANSFER_PENDING until the tick after the world-port acknowledgement).</summary>
    private bool InFarTransfer(PacketHeuristicsFeature heuristics) => Player is { } player && heuristics.InFarTransfer(player);

    private void InitPacketHeuristics(IServiceProvider services) => _heuristics = services.GetService<PacketHeuristicsFeature>();

    /// <summary>The per-session heuristics state, or null while the heuristics have not inspected this session (off, exempt, or no packet yet).</summary>
    internal PacketHeuristicsSession? HeuristicsState => Volatile.Read(ref _heuristicsState);

    /// <summary>The per-session heuristics state, created on first use (the first packet the heuristics actually inspect).</summary>
    internal PacketHeuristicsSession EnsureHeuristicsState()
        => Volatile.Read(ref _heuristicsState) ?? Interlocked.CompareExchange(ref _heuristicsState, new PacketHeuristicsSession(), null) ?? _heuristicsState!;

    /// <summary>The heuristics seam for handlers that ignore a packet (the movement acknowledgement handlers), or null when none is registered.</summary>
    internal PacketHeuristicsFeature? PacketHeuristics => _heuristics;

    /// <summary>Handlers call this where an acknowledgement is ignored because it matches nothing the server sent. A bot's own acknowledgement counts like a client's.</summary>
    internal void NoteOrphanAck(WorldOpcode opcode, string reason)
    {
        if (_heuristics is { Active: true } heuristics)
        {
            heuristics.OrphanAck(this, opcode, reason);
        }
    }

    /// <summary>Every authenticated client packet, before the dispatcher decides anything (network path): shape, cadence and replay.</summary>
    private void HeuristicsClientPacket(WorldOpcode opcode, byte[] payload)
    {
        if (_heuristics is { Active: true } heuristics && _state != SessionState.Connected)
        {
            heuristics.OnClientPacket(this, opcode, payload);
        }
    }

    /// <summary>
    /// A packet the dispatcher dropped because of the session state. Not reported once the session is closing or kicked, nor inside the requested
    /// <paramref name="grace"/> (the window after the player left the world, or the player being in a far transfer). <paramref name="reason"/> must be a
    /// constant: nothing is formatted here, the feature appends the state only when it reports. A second CMSG_PLAYER_LOGIN while the first is
    /// logging in (a double click on Enter World) is Info, not a Warning.
    /// </summary>
    private void HeuristicsWrongState(WorldOpcode opcode, string reason, WrongStateGrace grace)
    {
        if (_heuristics is not { Active: true } heuristics || _state == SessionState.Closed || _kick.IsCancellationRequested)
        {
            return;
        }

        if ((grace == WrongStateGrace.LeftWorld && Within(Volatile.Read(ref _leftWorldTick), heuristics.Settings.WrongStateGraceMs))
            || (grace == WrongStateGrace.Transfer && InFarTransfer(heuristics)))
        {
            return;
        }

        AnomalySeverity severity = opcode == WorldOpcode.CmsgPlayerLogin && _state is SessionState.LoggingIn or SessionState.InWorld
            ? AnomalySeverity.Info
            : AnomalySeverity.Warning;
        heuristics.WrongState(this, opcode, reason, _state, severity);
    }

    /// <summary>A frame header the reader refused; nothing is formatted unless a report goes out.</summary>
    private void HeuristicsBadHeader(int size, uint rawOpcode)
    {
        if (_heuristics is { Active: true } heuristics && _state != SessionState.Connected)
        {
            heuristics.BadHeader(this, rawOpcode <= ushort.MaxValue ? (WorldOpcode)rawOpcode : default, size, rawOpcode);
        }
    }

    private void HeuristicsUnhandled(WorldOpcode opcode, int length)
    {
        if (_heuristics is { Active: true } heuristics)
        {
            heuristics.Unhandled(this, opcode, length);
        }
    }

    private void HeuristicsMalformed(WorldOpcode opcode, int length, string reason)
    {
        if (_heuristics is { Active: true } heuristics && _state != SessionState.Connected)
        {
            heuristics.Malformed(this, opcode, length, reason);
        }
    }

    /// <summary>A world packet about to run its handler on the world thread (client path): keeps the GM-mode snapshot current (with the heuristics off too), then the incapacitated-action check.</summary>
    private void HeuristicsWorldPacket(Player player, WorldOpcode opcode)
    {
        // Refreshed with the heuristics off too: the dev-opcode staff audit (DropDevOpcode -> IsHeuristicsStaff) runs regardless of the toggle.
        bool gm = player.IsGameMaster;
        if (_heuristicsGm != gm)
        {
            _heuristicsGm = gm;
        }

        if (_heuristics is { Active: true } heuristics)
        {
            heuristics.OnWorldPacket(this, player, opcode);
        }
    }

    /// <summary>A managed bot's action that reached its handler (world thread): shape, cadence, replay and the incapacitated-action check.</summary>
    private void HeuristicsManagedPacket(Player player, WorldOpcode opcode, byte[] payload)
    {
        if (_heuristics is { Active: true } heuristics)
        {
            heuristics.OnManagedPacket(this, player, opcode, payload);
        }
    }

    /// <summary>
    /// <see cref="TryManagedAction"/> refused the action. Classifies why, with the same conditions in the same order, and reports what a bot should
    /// never do: an opcode with no handler, a session opcode sent as a world action, an action outside the world or without a map, an oversized
    /// payload. Not reported: a closing or superseded session, a quest settlement, a spent budget, a call off the world thread.
    /// </summary>
    private void NoteManagedRejected(WorldOpcode opcode, int length)
    {
        if (_managed && World.IsWorldThread && !_kick.IsCancellationRequested && IsCurrentSession && _state != SessionState.Closed
            && _opcodes.TryGet(opcode, out OpcodeHandler? stripped) && stripped.Stripped)
        {
            DropDevOpcode(opcode, length); // counted and flagged even with the heuristics off; the bot's action is still refused
            return;
        }

        if (_heuristics is not { Active: true } heuristics || !_managed || !World.IsWorldThread || _kick.IsCancellationRequested || !IsCurrentSession
            || _state == SessionState.Closed)
        {
            return;
        }

        if (_state != SessionState.InWorld)
        {
            heuristics.WrongState(this, opcode, "bot action while the session is not in the world", _state);
        }
        else if (Player is not { } player)
        {
            heuristics.WrongState(this, opcode, "bot action without a player");
        }
        else if (player.IsQuestSettlementPending)
        {
            // Documented benign drop: the settlement freezes the character.
        }
        else if (length > MaxClientPacketSize - 4)
        {
            heuristics.Malformed(this, opcode, length, "payload over the frame limit");
        }
        else if (!_opcodes.TryGet(opcode, out OpcodeHandler? handler))
        {
            heuristics.Unhandled(this, opcode, length);
        }
        else if (handler.World is null)
        {
            heuristics.WrongState(this, opcode, "session opcode sent as a world action");
        }
        else if (player.Map is null && opcode != WorldOpcode.MsgMoveWorldportAck && !heuristics.InFarTransfer(player))
        {
            heuristics.WrongState(this, opcode, "bot action while player.Map is null");
        }
    }

    /// <summary>
    /// The exception filter around a bot's handler call: reports a malformed payload (the controlled <see cref="MalformedPacket"/> outcome, which
    /// the bot's caller may swallow) and always returns false, so the exception continues exactly as it did before.
    /// </summary>
    private bool NoteManagedFault(WorldOpcode opcode, int length, Exception exception)
    {
        if (_heuristics is { Active: true } heuristics && MalformedPacket.Is(exception))
        {
            heuristics.Malformed(this, opcode, length, exception.Message);
        }

        return false;
    }
}
