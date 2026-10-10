namespace ArcaneCore.Realm.Protocol;

/// <summary>
/// Logon-stream command opcodes (1 byte). Values verified against vmangos
/// src/realmd/AuthCodes.h (eAuthCmd). M1 handles CHALLENGE, PROOF and REALM_LIST.
/// </summary>
public enum AuthCommand : byte
{
    LogonChallenge = 0x00,
    LogonProof = 0x01,
    ReconnectChallenge = 0x02,
    ReconnectProof = 0x03,
    RealmList = 0x10,

    /// <summary>CMD_XFER_INITIATE (server): a file transfer is offered (vmangos AuthCodes.h).</summary>
    XferInitiate = 0x30,

    /// <summary>CMD_XFER_DATA (server): one chunk of the file.</summary>
    XferData = 0x31,

    /// <summary>CMD_XFER_ACCEPT (client): send the file from the start.</summary>
    XferAccept = 0x32,

    /// <summary>CMD_XFER_RESUME (client) + u64 offset: send the rest of a partial download.</summary>
    XferResume = 0x33,

    /// <summary>CMD_XFER_CANCEL (client): the transfer is refused; close.</summary>
    XferCancel = 0x34,
}

/// <summary>
/// Logon result codes (1 byte). Values verified against vmangos AuthCodes.h.
/// Note: the client refuses further attempts after INCORRECT_PASSWORD, so vmangos
/// reports UNKNOWN_ACCOUNT for bad credentials too — ArcaneCore does the same.
/// </summary>
public enum AuthResult : byte
{
    Success = 0x00,
    Banned = 0x03,
    UnknownAccount = 0x04,
    IncorrectPassword = 0x05,

    /// <summary>WOW_FAIL_DB_BUSY (vmangos AuthCodes.h): the logon server could not use its database; the client is told to try again later.</summary>
    FailDbBusy = 0x08,
    VersionInvalid = 0x09,

    /// <summary>WOW_FAIL_VERSION_UPDATE: the client must download the patch that follows (vmangos AuthSocket.cpp:642).</summary>
    VersionUpdate = 0x0A,
    Suspended = 0x0C,

    /// <summary>WOW_FAIL_NOACCESS: used when the stored credentials are unusable (vmangos AuthCodes.h).</summary>
    FailNoAccess = 0x0D,
}
