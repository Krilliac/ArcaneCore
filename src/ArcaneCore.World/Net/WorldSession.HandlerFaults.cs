using System.Buffers.Binary;
using ArcaneCore.Game;
using ArcaneCore.Game.ClientErrors;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Net;

/// <summary>
/// What happens when an opcode handler throws (a server bug, not a malformed packet). The player is told through
/// <see cref="ClientError"/>, in the words the client uses for the action that failed (docs/areas/client-errors.md).
/// The cause is logged and goes into the fault ledger; the packet never carries it.
/// <para>
/// World handlers: the fault is contained and the next queued packet is handled. A run of faults is treated as a broken
/// session: the <see cref="HandlerFaultStorm"/>-th fault inside <see cref="HandlerFaultStormWindowMs"/> disconnects, as
/// every handler fault did before. Session (character screen) handlers: only CMSG_CHAR_CREATE, CMSG_CHAR_DELETE and
/// CMSG_PLAYER_LOGIN are contained, and only while the session is still at the character screen. They answer with the
/// matching failure code, which the client is waiting for. Any other session-handler fault still leaves the session.
/// </para>
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>Handler faults inside <see cref="HandlerFaultStormWindowMs"/> that disconnect the session.</summary>
    public const int HandlerFaultStorm = 5;

    /// <summary>The window of <see cref="HandlerFaultStorm"/>, in milliseconds.</summary>
    public const long HandlerFaultStormWindowMs = 60_000;

    private readonly ClientErrorRateLimiter _handlerFaults = new(HandlerFaultStorm - 1, HandlerFaultStormWindowMs);

    /// <summary>
    /// World thread: a world handler threw. Tells the client, records the fault, and returns true to keep handling this
    /// session's packets, or false when the session is being dropped (already closing, or a fault storm).
    /// </summary>
    private bool ContainWorldHandlerFault(WorldOpcode opcode, byte[] payload, Player player, Exception exception)
    {
        string cause = $"{WorldOpcodeNames.GetName(opcode)} handler threw for '{player.Name}'";
        if (_kick.IsCancellationRequested)
        {
            // Dropped anyway: nobody will read a reply. Still a fault worth counting.
            ArcaneCore.Kernel.Diagnostics.FaultLedger.Shared.Record("packet-handling", exception);
            return false;
        }

        ClientError.Internal(this, ContextForWorldPacket(opcode, payload), cause, _logger, exception);
        if (!_handlerFaults.TryAcquire(Environment.TickCount64))
        {
            _logger.LogError("[{Endpoint}] {Count} handler faults within {Seconds} s; disconnecting '{Player}'",
                RemoteEndpoint, HandlerFaultStorm, HandlerFaultStormWindowMs / 1000, player.Name);
            Kick();
            return false;
        }

        return true;
    }

    /// <summary>Session task: whether a fault in this session handler is answered here instead of leaving the session.</summary>
    private bool IsContainedSessionFault(WorldOpcode opcode)
        => _state == SessionState.CharacterSelect && !_kick.IsCancellationRequested && ContextForSessionPacket(opcode) is not null;

    /// <summary>Session task: answer a contained character-screen fault with its failure code.</summary>
    private void ContainSessionHandlerFault(WorldOpcode opcode, Exception exception)
    {
        ClientErrorContext context = ContextForSessionPacket(opcode)!.Value;
        ClientError.Internal(this, context, $"{WorldOpcodeNames.GetName(opcode)} handler threw for account '{AccountName}'", _logger, exception);
    }

    /// <summary>The character-screen opcodes whose client waits for a result code.</summary>
    internal static ClientErrorContext? ContextForSessionPacket(WorldOpcode opcode) => opcode switch
    {
        WorldOpcode.CmsgCharCreate => ClientErrorContext.CharacterCreate,
        WorldOpcode.CmsgCharDelete => ClientErrorContext.CharacterDelete,
        WorldOpcode.CmsgPlayerLogin => ClientErrorContext.CharacterLogin,
        _ => null,
    };

    /// <summary>
    /// The client error for a world opcode. A cast echoes its spell id (CMSG_CAST_SPELL starts with it); the item opcodes
    /// get the inventory internal error; everything else gets the notification.
    /// </summary>
    internal static ClientErrorContext ContextForWorldPacket(WorldOpcode opcode, ReadOnlySpan<byte> payload) => opcode switch
    {
        WorldOpcode.CmsgCastSpell => ClientErrorContext.SpellCast(payload.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(payload) : 0),
        WorldOpcode.CmsgSwapItem or WorldOpcode.CmsgSwapInvItem or WorldOpcode.CmsgSplitItem or WorldOpcode.CmsgAutoequipItem
            or WorldOpcode.CmsgAutoequipItemSlot or WorldOpcode.CmsgAutostoreBagItem or WorldOpcode.CmsgDestroyitem
            or WorldOpcode.CmsgUseItem or WorldOpcode.CmsgOpenItem or WorldOpcode.CmsgWrapItem
            or WorldOpcode.CmsgAutobankItem or WorldOpcode.CmsgAutostoreBankItem => ClientErrorContext.ItemMove(ObjectGuid.Empty, ObjectGuid.Empty),
        _ => ClientErrorContext.Generic,
    };
}
