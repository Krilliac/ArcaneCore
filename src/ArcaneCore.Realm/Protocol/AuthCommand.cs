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
    VersionInvalid = 0x09,
    Suspended = 0x0C,
}
