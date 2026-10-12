using System.Buffers.Binary;
using ArcaneCore.Game;
using ArcaneCore.Game.ClientErrors;
using ArcaneCore.Game.Economy;
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
/// World handlers: containment is an ALLOWLIST (<see cref="IsContainableWorldOpcode"/>). Only opcodes known to be
/// read-only or idempotent are contained: the player is told and the next queued packet is handled. A handler that
/// mutates state before it can throw (loot money credits before it clears the bag, a split-stack decrements before it
/// stores) would turn a repeatable exception into a duplication loop if it were absorbed, so every opcode NOT on the
/// list sends its ClientError (the player sees the message) and then disconnects, as every handler fault did before. New
/// handlers therefore default to disconnect. Among the contained opcodes a run of faults is still treated as a broken
/// session: the <see cref="HandlerFaultStorm"/>-th fault inside <see cref="HandlerFaultStormWindowMs"/> disconnects.
/// Session (character screen) handlers: only CMSG_CHAR_CREATE, CMSG_CHAR_DELETE and CMSG_PLAYER_LOGIN are contained,
/// and only while the session is still at the character screen. They answer with the matching failure code, which the
/// client is waiting for, and count against the same storm limit. Any other session-handler fault still leaves the session.
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
    /// session's packets, or false when the session is being dropped (already closing, an opcode that is not on the
    /// containment allowlist, or a fault storm).
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
        if (!IsContainableWorldOpcode(opcode))
        {
            // State-changing (or unreviewed) handler: it may have half-applied its effect, and absorbing the fault
            // would let the client repeat it. The player has been told; now the session goes.
            _logger.LogError("[{Endpoint}] {Opcode} handler fault is not containable; disconnecting '{Player}'",
                RemoteEndpoint, WorldOpcodeNames.GetName(opcode), player.Name);
            Kick();
            return false;
        }

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

    /// <summary>
    /// Session task: answer a contained character-screen fault with its failure code. Returns false when the faults
    /// form a storm and the session was kicked.
    /// </summary>
    private bool ContainSessionHandlerFault(WorldOpcode opcode, Exception exception)
    {
        ClientErrorContext context = ContextForSessionPacket(opcode)!.Value;
        ClientError.Internal(this, context, $"{WorldOpcodeNames.GetName(opcode)} handler threw for account '{AccountName}'", _logger, exception);
        if (!_handlerFaults.TryAcquire(Environment.TickCount64))
        {
            _logger.LogError("[{Endpoint}] {Count} handler faults within {Seconds} s; disconnecting account '{Account}'",
                RemoteEndpoint, HandlerFaultStorm, HandlerFaultStormWindowMs / 1000, AccountName);
            Kick();
            return false;
        }

        return true;
    }

    /// <summary>
    /// The containment allowlist: world opcodes whose handlers only read state, or only repeat a cosmetic or
    /// idempotent change, so absorbing a fault cannot be turned into a duplication or corruption loop. Everything else,
    /// including every economy, inventory, loot, mail, trade, spell and quest-reward handler, is NOT contained.
    /// <para>
    /// Movement (MSG_MOVE_*) is deliberately excluded: a movement handler fault leaves position, the broadcast and the
    /// anti-cheat state out of step, and a client that keeps moving would repeat it. It keeps the old behaviour and
    /// disconnects.
    /// </para>
    /// </summary>
    internal static bool IsContainableWorldOpcode(WorldOpcode opcode) => opcode is
        // Queries handled on the world thread. The name, item, creature, gameobject, quest, page-text and time queries
        // run on the session task (OnSession), where only character-screen faults are contained, so they are not listed.
        WorldOpcode.CmsgGuildQuery or WorldOpcode.CmsgGuildInfo or WorldOpcode.CmsgGuildRoster
        or WorldOpcode.CmsgPetNameQuery or WorldOpcode.CmsgPetitionQuery or WorldOpcode.CmsgQuestgiverStatusQuery
        or WorldOpcode.CmsgWho or WorldOpcode.CmsgInspect or WorldOpcode.CmsgPlayedTime
        // Chat and channels. Chat commands catch their own faults (TryRunCommand); player chat script hooks and
        // IChatMessageHandler features run inside this contained path. Channel state is in memory only.
        // CMSG_EMOTE / CMSG_TEXT_EMOTE are NOT here: they cancel animation auras and a text emote runs creature
        // receive-emote scripts (quest events, casts, summons).
        or WorldOpcode.CmsgMessagechat
        or WorldOpcode.CmsgJoinChannel or WorldOpcode.CmsgLeaveChannel or WorldOpcode.CmsgChannelList
        or WorldOpcode.CmsgChannelPassword or WorldOpcode.CmsgChannelSetOwner or WorldOpcode.CmsgChannelOwner
        or WorldOpcode.CmsgChannelModerator or WorldOpcode.CmsgChannelUnmoderator or WorldOpcode.CmsgChannelMute
        or WorldOpcode.CmsgChannelUnmute or WorldOpcode.CmsgChannelInvite or WorldOpcode.CmsgChannelKick
        or WorldOpcode.CmsgChannelBan or WorldOpcode.CmsgChannelUnban or WorldOpcode.CmsgChannelAnnouncements
        or WorldOpcode.CmsgChannelModerate
        // Friend list read and sheathe state. CMSG_STANDSTATECHANGE (removes food/drink auras) and CMSG_SET_SELECTION
        // (retargets auto-repeat spells) are NOT here.
        or WorldOpcode.CmsgFriendList or WorldOpcode.CmsgSetsheathed;

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
    /// get the inventory internal error; the mail opcodes get the mail result (they disconnect afterwards, being economy);
    /// everything else gets the notification.
    /// </summary>
    internal static ClientErrorContext ContextForWorldPacket(WorldOpcode opcode, ReadOnlySpan<byte> payload) => opcode switch
    {
        WorldOpcode.CmsgSendMail => ClientErrorContext.Mail(0, MailAction.Send),
        WorldOpcode.CmsgMailTakeMoney => ClientErrorContext.Mail(0, MailAction.MoneyTaken),
        WorldOpcode.CmsgMailTakeItem => ClientErrorContext.Mail(0, MailAction.ItemTaken),
        WorldOpcode.CmsgCastSpell => ClientErrorContext.SpellCast(payload.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(payload) : 0),
        WorldOpcode.CmsgSwapItem or WorldOpcode.CmsgSwapInvItem or WorldOpcode.CmsgSplitItem or WorldOpcode.CmsgAutoequipItem
            or WorldOpcode.CmsgAutoequipItemSlot or WorldOpcode.CmsgAutostoreBagItem or WorldOpcode.CmsgDestroyitem
            or WorldOpcode.CmsgUseItem or WorldOpcode.CmsgOpenItem or WorldOpcode.CmsgWrapItem
            or WorldOpcode.CmsgAutobankItem or WorldOpcode.CmsgAutostoreBankItem => ClientErrorContext.ItemMove(ObjectGuid.Empty, ObjectGuid.Empty),
        _ => ClientErrorContext.Generic,
    };
}
