using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.AntiCheat.PacketHeuristics;
using ArcaneCore.World.Handlers;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Net;

/// <summary>
/// The session's hooks into the packet heuristics (docs/areas/packet-heuristics.md). Report only: every hook is a call that returns nothing, never
/// throws and never changes what the dispatcher does next, so a packet that dispatched before still dispatches and one that was dropped is still
/// dropped. Resolved once from the connection's services; with the feature absent or off each hook is a null check and one volatile read, and no
/// per-session state exists.
/// </summary>
public sealed partial class WorldSession
{
    private PacketHeuristicsFeature? _heuristics;
    private PacketHeuristicsSession? _heuristicsState;
    private long _leftWorldTick = long.MinValue;

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

    /// <summary>A packet the dispatcher dropped because of the session state. Not reported once the session is closing or kicked, nor (when <paramref name="honourGrace"/>) in the grace after the player left the world.</summary>
    private void HeuristicsWrongState(WorldOpcode opcode, string reason, bool honourGrace)
    {
        if (_heuristics is not { Active: true } heuristics || _state == SessionState.Closed || _kick.IsCancellationRequested)
        {
            return;
        }

        long leftAt = Volatile.Read(ref _leftWorldTick);
        if (honourGrace && leftAt != long.MinValue && Environment.TickCount64 - leftAt < heuristics.Settings.WrongStateGraceMs)
        {
            return;
        }

        heuristics.WrongState(this, opcode, reason + " (state " + _state + ")");
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

    /// <summary>A world packet about to run its handler on the world thread (client path): the incapacitated-action check.</summary>
    private void HeuristicsWorldPacket(Player player, WorldOpcode opcode)
    {
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
        if (_heuristics is not { Active: true } heuristics || !_managed || !World.IsWorldThread || _kick.IsCancellationRequested || !IsCurrentSession
            || _state == SessionState.Closed)
        {
            return;
        }

        if (_state != SessionState.InWorld)
        {
            heuristics.WrongState(this, opcode, "bot action while the session is " + _state);
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
            heuristics.Malformed(this, opcode, length, $"payload over the {MaxClientPacketSize - 4} byte frame limit");
        }
        else if (!_opcodes.TryGet(opcode, out OpcodeHandler? handler))
        {
            heuristics.Unhandled(this, opcode, length);
        }
        else if (handler.World is null)
        {
            heuristics.WrongState(this, opcode, "session opcode sent as a world action");
        }
        else if (player.Map is null && opcode != WorldOpcode.MsgMoveWorldportAck)
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
