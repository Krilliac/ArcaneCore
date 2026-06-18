namespace ArcaneCore.Protocol;

/// <summary>
/// World-stream opcodes (2 bytes on the wire). Values are build-5875-specific, verified
/// against vmangos src/game/Server/Protocol/Opcodes_1_12_1.h. Only the opcodes M2 uses
/// are defined; later milestones add more.
/// </summary>
public enum WorldOpcode : ushort
{
    CmsgCharCreate = 54,      // 0x036
    CmsgCharEnum = 55,        // 0x037
    CmsgCharDelete = 56,      // 0x038
    SmsgCharCreate = 58,      // 0x03A
    SmsgCharEnum = 59,        // 0x03B
    SmsgCharDelete = 60,      // 0x03C
    CmsgPlayerLogin = 61,     // 0x03D
    SmsgLoginSetTimeSpeed = 66,   // 0x042
    SmsgUpdateObject = 169,   // 0x0A9
    SmsgTutorialFlags = 253,  // 0x0FD
    SmsgInitialSpells = 298,  // 0x12A
    CmsgPing = 476,           // 0x1DC
    SmsgPong = 477,           // 0x1DD
    SmsgLoginVerifyWorld = 566, // 0x236
    SmsgAuthChallenge = 492,  // 0x1EC
    CmsgAuthSession = 493,    // 0x1ED
    SmsgAuthResponse = 494,   // 0x1EE
    SmsgAddonInfo = 751,      // 0x2EF
}

/// <summary>
/// Character-screen result codes (1 byte). Vanilla values verified against
/// gtker/wow_world_base vanilla world_result (which differ from TBC).
/// </summary>
public enum CharResult : byte
{
    CharCreateSuccess = 0x2E,
    CharCreateError = 0x2F,
    CharCreateFailed = 0x30,
    CharCreateNameInUse = 0x31,
    CharCreateDisabled = 0x32,
    CharCreateServerLimit = 0x34,
    CharDeleteSuccess = 0x39,
    CharDeleteFailed = 0x3A,
    CharNameNoName = 0x45,
    CharNameTooShort = 0x46,
    CharNameTooLong = 0x47,
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
