namespace ArcaneCore.Protocol;

/// <summary>
/// World-stream opcodes (2 bytes on the wire). Values are build-5875-specific, verified
/// against vmangos src/game/Server/Protocol/Opcodes_1_12_1.h. Only the opcodes M2 uses
/// are defined; later milestones add more.
/// </summary>
public enum WorldOpcode : ushort
{
    CmsgCharEnum = 55,        // 0x037
    SmsgCharEnum = 59,        // 0x03B
    CmsgPlayerLogin = 61,     // 0x03D
    CmsgPing = 476,           // 0x1DC
    SmsgPong = 477,           // 0x1DD
    SmsgAuthChallenge = 492,  // 0x1EC
    CmsgAuthSession = 493,    // 0x1ED
    SmsgAuthResponse = 494,   // 0x1EE
    SmsgAddonInfo = 751,      // 0x2EF
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
