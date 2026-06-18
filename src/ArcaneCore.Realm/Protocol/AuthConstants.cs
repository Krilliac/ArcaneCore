namespace ArcaneCore.Realm.Protocol;

public static class AuthConstants
{
    /// <summary>
    /// The 16-byte "version challenge" / crc_salt appended to the logon challenge reply.
    /// Constant value verified against vmangos AuthSocket.cpp:65 (VersionChallenge).
    /// </summary>
    public static ReadOnlySpan<byte> VersionChallenge =>
    [
        0xBA, 0xA3, 0x1E, 0x99, 0xA0, 0x0B, 0x21, 0x57,
        0xFC, 0x37, 0x3F, 0xB3, 0x69, 0xCD, 0xD2, 0xF1,
    ];
}
