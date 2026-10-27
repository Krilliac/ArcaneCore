namespace ArcaneCore.Protocol;

/// <summary>
/// Character-screen result codes (1 byte), the client's response-code table. Every value below was checked against the
/// owned 1.12.1 client: its glue screen maps code N to the N-th GlueStrings key of one 83-entry table (codes 0x00-0x52;
/// anything else is shown as "(N)"). See WoW-RE-Research protocol/client-error-codes.md and docs/areas/client-errors.md.
/// gtker/wow_messages world_result.wowm disagrees on the CHAR_NAME tail (it puts CHAR_NAME_SUCCESS at 0x50); the binary
/// has CHAR_NAME_CONSECUTIVE_SPACES at 0x50 and CHAR_NAME_SUCCESS at 0x52, as vmangos SharedDefines.h does. The binary wins.
/// </summary>
public enum CharResult : byte
{
    /// <summary>CHAR_CREATE_SUCCESS "Character created".</summary>
    CharCreateSuccess = 0x2E,

    /// <summary>CHAR_CREATE_ERROR "Error creating character": the server failed (<c>ClientError</c> uses it).</summary>
    CharCreateError = 0x2F,

    /// <summary>CHAR_CREATE_FAILED "Character creation failed".</summary>
    CharCreateFailed = 0x30,

    /// <summary>CHAR_CREATE_NAME_IN_USE "That name is unavailable".</summary>
    CharCreateNameInUse = 0x31,

    /// <summary>CHAR_CREATE_DISABLED.</summary>
    CharCreateDisabled = 0x32,

    /// <summary>CHAR_CREATE_PVP_TEAMS_VIOLATION.</summary>
    CharCreatePvpTeamsViolation = 0x33,

    /// <summary>CHAR_CREATE_SERVER_LIMIT.</summary>
    CharCreateServerLimit = 0x34,

    /// <summary>CHAR_CREATE_ACCOUNT_LIMIT.</summary>
    CharCreateAccountLimit = 0x35,

    /// <summary>CHAR_CREATE_SERVER_QUEUE (creation paused while the realm is queued).</summary>
    CharCreateServerQueue = 0x36,

    /// <summary>CHAR_CREATE_ONLY_EXISTING (only accounts with a character here may create).</summary>
    CharCreateOnlyExisting = 0x37,

    /// <summary>CHAR_DELETE_SUCCESS "Character deleted".</summary>
    CharDeleteSuccess = 0x39,

    /// <summary>CHAR_DELETE_FAILED "Character deletion failed": also the server-failure code for a delete.</summary>
    CharDeleteFailed = 0x3A,

    /// <summary>CHAR_DELETE_FAILED_LOCKED_FOR_TRANSFER.</summary>
    CharDeleteFailedLockedForTransfer = 0x3B,

    /// <summary>CHAR_LOGIN_IN_PROGRESS.</summary>
    CharLoginInProgress = 0x3C,

    /// <summary>CHAR_LOGIN_SUCCESS.</summary>
    CharLoginSuccess = 0x3D,

    /// <summary>CHAR_LOGIN_NO_WORLD "World server is down".</summary>
    CharLoginNoWorld = 0x3E,

    /// <summary>CHAR_LOGIN_DUPLICATE_CHARACTER.</summary>
    CharLoginDuplicateCharacter = 0x3F,

    /// <summary>CHAR_LOGIN_NO_INSTANCES.</summary>
    CharLoginNoInstances = 0x40,

    /// <summary>CHAR_LOGIN_FAILED "Login failed": the server-failure code for entering the world.</summary>
    CharLoginFailed = 0x41,

    /// <summary>CHAR_LOGIN_DISABLED.</summary>
    CharLoginDisabled = 0x42,

    /// <summary>CHAR_LOGIN_NO_CHARACTER "Character not found".</summary>
    CharLoginNoCharacter = 0x43,

    /// <summary>CHAR_LOGIN_LOCKED_FOR_TRANSFER.</summary>
    CharLoginLockedForTransfer = 0x44,

    /// <summary>CHAR_NAME_NO_NAME.</summary>
    CharNameNoName = 0x45,

    /// <summary>CHAR_NAME_TOO_SHORT.</summary>
    CharNameTooShort = 0x46,

    /// <summary>CHAR_NAME_TOO_LONG.</summary>
    CharNameTooLong = 0x47,

    /// <summary>CHAR_NAME_INVALID_CHARACTER "Names can only contain letters" (gtker CHAR_NAME_ONLY_LETTERS).</summary>
    CharNameInvalidCharacter = 0x48,

    /// <summary>CHAR_NAME_MIXED_LANGUAGES.</summary>
    CharNameMixedLanguages = 0x49,

    /// <summary>CHAR_NAME_PROFANE.</summary>
    CharNameProfane = 0x4A,

    /// <summary>CHAR_NAME_RESERVED.</summary>
    CharNameReserved = 0x4B,

    /// <summary>CHAR_NAME_INVALID_APOSTROPHE.</summary>
    CharNameInvalidApostrophe = 0x4C,

    /// <summary>CHAR_NAME_MULTIPLE_APOSTROPHES.</summary>
    CharNameMultipleApostrophes = 0x4D,

    /// <summary>CHAR_NAME_THREE_CONSECUTIVE.</summary>
    CharNameThreeConsecutive = 0x4E,

    /// <summary>CHAR_NAME_INVALID_SPACE.</summary>
    CharNameInvalidSpace = 0x4F,

    /// <summary>CHAR_NAME_CONSECUTIVE_SPACES (the key has no GlueStrings text in 1.12.1).</summary>
    CharNameConsecutiveSpaces = 0x50,

    /// <summary>CHAR_NAME_FAILURE "Invalid character name".</summary>
    CharNameFailure = 0x51,

    /// <summary>CHAR_NAME_SUCCESS (binary 0x52; gtker's 0x50 is the consecutive-spaces code).</summary>
    CharNameSuccess = 0x52,
}

/// <summary>
/// SMSG_AUTH_RESPONSE result codes. Values verified against vmangos SharedDefines.h
/// (enum ResponseCodes — AUTH_OK is the 13th entry = 0x0C); the 0x10, 0x1C, 0x1D, 0x1F and 0x20
/// values are confirmed against wow_messages world/enums/world_result.wowm.
/// </summary>
public enum AuthResponseCode : byte
{
    Ok = 0x0C,
    Failed = 0x0D,
    Unavailable = 0x10,
    VersionMismatch = 0x14,
    UnknownAccount = 0x15,
    Banned = 0x1C,
    AlreadyOnline = 0x1D,
    DbBusy = 0x1F,
    Suspended = 0x20,
}
