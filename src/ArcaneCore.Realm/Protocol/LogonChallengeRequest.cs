using System.Buffers.Binary;
using System.Text;

namespace ArcaneCore.Realm.Protocol;

/// <summary>
/// Parsed CMD_AUTH_LOGON_CHALLENGE client body. Layout verified against vmangos
/// AuthPackets.h (sAuthLogonChallengeBody): gamename[4], version[3], build(u16 LE),
/// platform[4], os[4], country[4], timezone(u32), ip(u32), username_len(1), username[].
/// </summary>
public sealed class LogonChallengeRequest
{
    private const int FixedPrefixLength = 30; // bytes preceding the username

    public required ushort Build { get; init; }

    /// <summary>Account name as sent by the client (already uppercased by the client).</summary>
    public required string Username { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> body, out LogonChallengeRequest? request)
    {
        request = null;
        if (body.Length < FixedPrefixLength)
        {
            return false;
        }

        ushort build = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(7, 2));

        int usernameLength = body[29];
        if (usernameLength == 0 || FixedPrefixLength + usernameLength > body.Length)
        {
            return false;
        }

        string username = Encoding.ASCII.GetString(body.Slice(FixedPrefixLength, usernameLength));

        request = new LogonChallengeRequest { Build = build, Username = username };
        return true;
    }
}
