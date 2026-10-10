using ArcaneCore.Kernel.Ops.Metrics;
using ArcaneCore.Realm.Protocol;

namespace ArcaneCore.Realm.Net;

/// <summary>
/// The per-opcode traffic table of the logon stream (protocol label <c>logon</c>). The labels are the eAuthCmd names of
/// vmangos src/realmd/AuthCodes.h:29-40; the separate CMD_GRUNT_* enum (AuthCodes.h:46-56) is another stream and is not
/// registered. A command byte outside this set counts in the <c>unknown</c> bucket.
/// </summary>
public static class LogonPacketMetrics
{
    /// <summary>The <c>protocol</c> label value.</summary>
    public const string Protocol = "logon";

    /// <summary>Every <see cref="AuthCommand"/> with its reference name (AuthCodes.h:31-40).</summary>
    public static IReadOnlyList<KeyValuePair<int, string>> Commands { get; } =
    [
        new((int)AuthCommand.LogonChallenge, "CMD_AUTH_LOGON_CHALLENGE"),
        new((int)AuthCommand.LogonProof, "CMD_AUTH_LOGON_PROOF"),
        new((int)AuthCommand.ReconnectChallenge, "CMD_AUTH_RECONNECT_CHALLENGE"),
        new((int)AuthCommand.ReconnectProof, "CMD_AUTH_RECONNECT_PROOF"),
        new((int)AuthCommand.RealmList, "CMD_REALM_LIST"),
        new((int)AuthCommand.XferInitiate, "CMD_XFER_INITIATE"),
        new((int)AuthCommand.XferData, "CMD_XFER_DATA"),
        new((int)AuthCommand.XferAccept, "CMD_XFER_ACCEPT"),
        new((int)AuthCommand.XferResume, "CMD_XFER_RESUME"),
        new((int)AuthCommand.XferCancel, "CMD_XFER_CANCEL"),
    ];

    /// <summary>The logon table; recording is a no-op until the store listens.</summary>
    public static OpcodeTable Table { get; } = ArcaneMeters.Opcodes.Register(Protocol, Commands);
}
