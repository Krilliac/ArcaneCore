using System.Runtime.CompilerServices;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.ClientErrors;

/// <summary>
/// One typed way to tell a player "the server failed". The player gets the client's own generic failure text for
/// the action they were doing. The real cause goes to the log and to <see cref="FaultLedger.Shared"/>, never to the
/// client. The packet for each context was checked against the 1.12.1 client binary (docs/areas/client-errors.md,
/// WoW-RE-Research protocol/client-error-codes.md):
/// <list type="table">
/// <item><term><see cref="ClientErrorKind.SpellCast"/></term><description>SMSG_CAST_RESULT, result 0x1C SPELL_FAILED_ERROR: red "Internal error".</description></item>
/// <item><term><see cref="ClientErrorKind.ItemMove"/></term><description>SMSG_INVENTORY_CHANGE_FAILURE 40 ERR_INTERNAL_BAG_ERROR: red "Internal Bag Error".</description></item>
/// <item><term><see cref="ClientErrorKind.CharacterCreate"/></term><description>SMSG_CHAR_CREATE 0x2F CHAR_CREATE_ERROR.</description></item>
/// <item><term><see cref="ClientErrorKind.CharacterDelete"/></term><description>SMSG_CHAR_DELETE 0x3A CHAR_DELETE_FAILED.</description></item>
/// <item><term><see cref="ClientErrorKind.CharacterLogin"/></term><description>SMSG_CHARACTER_LOGIN_FAILED 0x41 CHAR_LOGIN_FAILED.</description></item>
/// <item><term><see cref="ClientErrorKind.Mail"/></term><description>SMSG_SEND_MAIL_RESULT result 6 ERR_MAIL_DATABASE_ERROR.</description></item>
/// <item><term><see cref="ClientErrorKind.Generic"/></term><description>SMSG_NOTIFICATION "Internal error": red text plus a chat line.</description></item>
/// </list>
/// <para>
/// Rate limit: the unsolicited contexts (generic, spell, item) send at most <see cref="Burst"/> packets per
/// <see cref="WindowMs"/> per session. The fault is still logged and recorded every time. The answer contexts (character
/// screen, mail) always send, because the client waits for that reply. Thread-safe: callable from the world thread and
/// from session tasks.
/// </para>
/// </summary>
public static class ClientError
{
    /// <summary>The fixed SMSG_NOTIFICATION text. It is the client's own SPELL_FAILED_ERROR wording, so every context reads alike.</summary>
    public const string GenericText = "Internal error";

    /// <summary>Unsolicited client errors allowed per session in one <see cref="WindowMs"/>.</summary>
    public const int Burst = 3;

    /// <summary>The rate-limit window in milliseconds.</summary>
    public const long WindowMs = 10_000;

    /// <summary>
    /// FaultLedger site prefix. With an exception the site is <c>client-error/&lt;kind&gt;</c> (the exception's own frames
    /// tell callers apart). A rejection has no thrown stack, so its site also names the caller:
    /// <c>client-error/&lt;kind&gt;/&lt;File&gt;.&lt;Member&gt;</c>.
    /// </summary>
    public const string SitePrefix = "client-error/";

    private static readonly ConditionalWeakTable<IPlayerSession, ClientErrorRateLimiter> Limiters = new();

    /// <summary>
    /// Tell the client the server failed at <paramref name="context"/>. Logs <paramref name="cause"/> (and
    /// <paramref name="exception"/> when there is one) at Error, throttled by the fault ledger, and records a ledger entry.
    /// The packet carries no part of <paramref name="cause"/> or of the exception. Without an exception the ledger entry
    /// is a <see cref="ServerRejectionException"/> carrying the cause, under a site that names the calling member, so each
    /// rejecting call site is its own signature. Leave the caller-info parameters to the compiler.
    /// </summary>
    public static ClientErrorOutcome Internal(
        IPlayerSession session, ClientErrorContext context, string cause, ILogger? logger = null, Exception? exception = null,
        [CallerMemberName] string callerMember = "", [CallerFilePath] string callerFile = "")
        => Report(session, context, cause, logger, exception, Environment.TickCount64, callerMember, callerFile);

    /// <summary><see cref="Internal(IPlayerSession, ClientErrorContext, string, ILogger?, Exception?, string, string)"/> at an explicit monotonic time (tests).</summary>
    public static ClientErrorOutcome Internal(
        IPlayerSession session, ClientErrorContext context, string cause, ILogger? logger, Exception? exception, long nowMs,
        [CallerMemberName] string callerMember = "", [CallerFilePath] string callerFile = "")
        => Report(session, context, cause, logger, exception, nowMs, callerMember, callerFile);

    /// <summary>A spell (or gather / open) cast failed on the server: red "Internal error".</summary>
    public static ClientErrorOutcome SpellCast(
        IPlayerSession session, uint spellId, string cause, ILogger? logger = null, Exception? exception = null,
        [CallerMemberName] string callerMember = "", [CallerFilePath] string callerFile = "")
        => Report(session, ClientErrorContext.SpellCast(spellId), cause, logger, exception, Environment.TickCount64, callerMember, callerFile);

    /// <summary>An item move, equip, split or use failed on the server: red "Internal Bag Error".</summary>
    public static ClientErrorOutcome ItemMove(
        IPlayerSession session, ObjectGuid item1, ObjectGuid item2, string cause, ILogger? logger = null, Exception? exception = null,
        [CallerMemberName] string callerMember = "", [CallerFilePath] string callerFile = "")
        => Report(session, ClientErrorContext.ItemMove(item1, item2), cause, logger, exception, Environment.TickCount64, callerMember, callerFile);

    /// <summary>Any other server failure: SMSG_NOTIFICATION "Internal error".</summary>
    public static ClientErrorOutcome Generic(
        IPlayerSession session, string cause, ILogger? logger = null, Exception? exception = null,
        [CallerMemberName] string callerMember = "", [CallerFilePath] string callerFile = "")
        => Report(session, ClientErrorContext.Generic, cause, logger, exception, Environment.TickCount64, callerMember, callerFile);

    /// <summary>The ledger site of a call (see <see cref="SitePrefix"/>).</summary>
    public static string SiteOf(ClientErrorKind kind, bool hasException, string callerMember, string callerFile)
    {
        string site = SitePrefix + KindName(kind);
        if (hasException)
        {
            return site;
        }

        // Path.GetFileNameWithoutExtension would treat '\' as a separator only on Windows; the path comes from the build host.
        string file = callerFile;
        int slash = Math.Max(file.LastIndexOf('/'), file.LastIndexOf('\\'));
        file = slash >= 0 ? file[(slash + 1)..] : file;
        int dot = file.LastIndexOf('.');
        file = dot > 0 ? file[..dot] : file;
        return $"{site}/{(file.Length == 0 ? "unknown" : file)}.{(callerMember.Length == 0 ? "unknown" : callerMember)}";
    }

    private static ClientErrorOutcome Report(
        IPlayerSession session, ClientErrorContext context, string cause, ILogger? logger, Exception? exception, long nowMs,
        string callerMember, string callerFile)
    {
        ArgumentNullException.ThrowIfNull(session);
        cause = string.IsNullOrWhiteSpace(cause) ? "unspecified server failure" : cause;
        string kind = KindName(context.Kind);

        FaultRecording recording = FaultLedger.Shared.Record(
            SiteOf(context.Kind, exception is not null, callerMember, callerFile), exception ?? new ServerRejectionException(cause));
        if (logger is not null && recording.ShouldLog)
        {
            logger.LogError(exception, "server failure shown to the client as {Kind} internal error (fault {FaultId}, {Suppressed} unlogged since): {Cause}",
                kind, recording.Id, recording.Suppressed, cause);
        }

        (WorldOpcode opcode, byte[] payload) = Build(context);
        if (IsRateLimited(context.Kind)
            && !Limiters.GetValue(session, static _ => new ClientErrorRateLimiter(Burst, WindowMs)).TryAcquire(nowMs))
        {
            return new ClientErrorOutcome(false, opcode, recording.Id);
        }

        session.Send(opcode, payload);
        return new ClientErrorOutcome(true, opcode, recording.Id);
    }

    /// <summary>The packet a context sends. Pure: it depends on the context only, never on a cause or an exception.</summary>
    public static (WorldOpcode Opcode, byte[] Payload) Build(ClientErrorContext context) => context.Kind switch
    {
        ClientErrorKind.SpellCast => (WorldOpcode.SmsgCastResult, SpellPackets.BuildCastResult(context.SpellId, SpellCastResult.Error)),
        ClientErrorKind.ItemMove => (WorldOpcode.SmsgInventoryChangeFailure,
            ItemPackets.InventoryChangeFailure(InventoryResult.IntBagError, context.Item1, context.Item2)),
        ClientErrorKind.CharacterCreate => (WorldOpcode.SmsgCharCreate, [(byte)CharResult.CharCreateError]),
        ClientErrorKind.CharacterDelete => (WorldOpcode.SmsgCharDelete, [(byte)CharResult.CharDeleteFailed]),
        ClientErrorKind.CharacterLogin => (WorldOpcode.SmsgCharacterLoginFailed, [(byte)CharResult.CharLoginFailed]),
        ClientErrorKind.Mail => (WorldOpcode.SmsgSendMailResult, MailResult(context.MailId, context.MailAction)),
        _ => (WorldOpcode.SmsgNotification, Notification(GenericText)),
    };

    /// <summary>Whether the context is unsolicited (rate-limited) rather than the answer the client is waiting for.</summary>
    public static bool IsRateLimited(ClientErrorKind kind)
        => kind is ClientErrorKind.Generic or ClientErrorKind.SpellCast or ClientErrorKind.ItemMove;

    private static string KindName(ClientErrorKind kind) => kind switch
    {
        ClientErrorKind.SpellCast => "spell",
        ClientErrorKind.ItemMove => "item",
        ClientErrorKind.CharacterCreate => "char-create",
        ClientErrorKind.CharacterDelete => "char-delete",
        ClientErrorKind.CharacterLogin => "char-login",
        ClientErrorKind.Mail => "mail",
        _ => "generic",
    };

    /// <summary>SMSG_SEND_MAIL_RESULT: u32 mail id, u32 action, u32 result (6, no inventory-result field).</summary>
    private static byte[] MailResult(uint mailId, MailAction action)
    {
        var writer = new PacketWriter(12);
        writer.WriteUInt32(mailId);
        writer.WriteUInt32((uint)action);
        writer.WriteUInt32((uint)Economy.MailResult.InternalError);
        return writer.ToArray();
    }

    /// <summary>SMSG_NOTIFICATION: one NUL-terminated string.</summary>
    private static byte[] Notification(string text)
    {
        var writer = new PacketWriter(text.Length + 1);
        writer.WriteCString(text);
        return writer.ToArray();
    }
}

/// <summary>What a <see cref="ClientError"/> call did: whether it sent, which opcode, and the fault ledger id of the cause.</summary>
public readonly record struct ClientErrorOutcome(bool Sent, WorldOpcode Opcode, string FaultId);

/// <summary>The kind of action that failed; it picks the packet the client understands for it.</summary>
public enum ClientErrorKind
{
    /// <summary>No typed channel: SMSG_NOTIFICATION.</summary>
    Generic,

    /// <summary>A spell, gather or open cast.</summary>
    SpellCast,

    /// <summary>An item move, equip, split, destroy or use.</summary>
    ItemMove,

    /// <summary>CMSG_CHAR_CREATE.</summary>
    CharacterCreate,

    /// <summary>CMSG_CHAR_DELETE.</summary>
    CharacterDelete,

    /// <summary>CMSG_PLAYER_LOGIN.</summary>
    CharacterLogin,

    /// <summary>A mail send or take.</summary>
    Mail,
}

/// <summary>The failed action and the identifiers its packet echoes back (spell id, item guids, mail id and action).</summary>
public readonly record struct ClientErrorContext
{
    private ClientErrorContext(ClientErrorKind kind, uint spellId = 0, ObjectGuid item1 = default, ObjectGuid item2 = default,
        uint mailId = 0, MailAction mailAction = MailAction.Send)
    {
        Kind = kind;
        SpellId = spellId;
        Item1 = item1;
        Item2 = item2;
        MailId = mailId;
        MailAction = mailAction;
    }

    public ClientErrorKind Kind { get; }

    public uint SpellId { get; }

    public ObjectGuid Item1 { get; }

    public ObjectGuid Item2 { get; }

    public uint MailId { get; }

    public MailAction MailAction { get; }

    public static ClientErrorContext Generic => new(ClientErrorKind.Generic);

    public static ClientErrorContext CharacterCreate => new(ClientErrorKind.CharacterCreate);

    public static ClientErrorContext CharacterDelete => new(ClientErrorKind.CharacterDelete);

    public static ClientErrorContext CharacterLogin => new(ClientErrorKind.CharacterLogin);

    public static ClientErrorContext SpellCast(uint spellId) => new(ClientErrorKind.SpellCast, spellId: spellId);

    public static ClientErrorContext ItemMove(ObjectGuid item1 = default, ObjectGuid item2 = default)
        => new(ClientErrorKind.ItemMove, item1: item1, item2: item2);

    public static ClientErrorContext Mail(uint mailId, MailAction action) => new(ClientErrorKind.Mail, mailId: mailId, mailAction: action);
}

/// <summary>A server-side refusal recorded in the fault ledger without a thrown exception (never thrown).</summary>
public sealed class ServerRejectionException : Exception
{
    public ServerRejectionException(string message)
        : base(message)
    {
    }

    public ServerRejectionException()
    {
    }

    public ServerRejectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A sliding-window counter: at most <c>burst</c> acquisitions in any <c>windowMs</c>. Thread-safe.</summary>
public sealed class ClientErrorRateLimiter
{
    private readonly long[] _stamps;
    private readonly long _windowMs;
    private readonly object _gate = new();
    private int _next;
    private int _count;

    public ClientErrorRateLimiter(int burst, long windowMs)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(burst, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(windowMs, 1);
        _stamps = new long[burst];
        _windowMs = windowMs;
    }

    /// <summary>True (and counted) when fewer than the burst were acquired in the window ending at <paramref name="nowMs"/>.</summary>
    public bool TryAcquire(long nowMs)
    {
        lock (_gate)
        {
            if (_count == _stamps.Length)
            {
                // _next is the oldest stamp of a full ring.
                if (nowMs - _stamps[_next] < _windowMs)
                {
                    return false;
                }

                _count--;
            }

            _stamps[_next] = nowMs;
            _next = (_next + 1) % _stamps.Length;
            _count++;
            return true;
        }
    }
}
