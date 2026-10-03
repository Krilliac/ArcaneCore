namespace ArcaneCore.Protocol;

/// <summary>
/// Character-screen result codes (1 byte). Vanilla values verified against gtker/wow_messages
/// world_result.wowm (versions "1") and the vmangos SharedDefines.h ResponseCodes enumeration
/// for builds &gt; 1.7.1 — they agree on every code below except the CHAR_NAME tail: vmangos has
/// an extra CHAR_NAME_CONSECUTIVE_SPACES (0x50), which shifts its CHAR_NAME_SUCCESS to 0x52.
/// gtker's 0x50 is kept (its comments describe observed client behaviour).
/// </summary>
public enum CharResult : byte
{
    CharCreateSuccess = 0x2E,
    CharCreateError = 0x2F,
    CharCreateFailed = 0x30,
    CharCreateNameInUse = 0x31,
    CharCreateDisabled = 0x32,
    CharCreatePvpTeamsViolation = 0x33,
    CharCreateServerLimit = 0x34,
    CharCreateAccountLimit = 0x35,
    CharDeleteSuccess = 0x39,
    CharDeleteFailed = 0x3A,
    CharLoginInProgress = 0x3C,
    CharLoginSuccess = 0x3D,
    CharLoginNoWorld = 0x3E,
    CharLoginDuplicateCharacter = 0x3F,
    CharLoginNoInstances = 0x40,
    CharLoginFailed = 0x41,
    CharLoginDisabled = 0x42,
    CharLoginNoCharacter = 0x43,
    CharNameNoName = 0x45,
    CharNameTooShort = 0x46,
    CharNameTooLong = 0x47,
    CharNameInvalidCharacter = 0x48, // gtker CHAR_NAME_ONLY_LETTERS
    CharNameMixedLanguages = 0x49,
    CharNameProfane = 0x4A,
    CharNameReserved = 0x4B,
    CharNameInvalidApostrophe = 0x4C,
    CharNameMultipleApostrophes = 0x4D,
    CharNameThreeConsecutive = 0x4E,
    CharNameInvalidSpace = 0x4F,
    CharNameSuccess = 0x50,
    CharNameFailure = 0x51,
}

/// <summary>
/// SMSG_AUTH_RESPONSE result codes. Values verified against vmangos SharedDefines.h
/// (enum ResponseCodes — AUTH_OK is the 13th entry = 0x0C).
/// </summary>
public enum AuthResponseCode : byte
{
    Ok = 0x0C,
    Failed = 0x0D,
    VersionMismatch = 0x14,
    UnknownAccount = 0x15,
}
