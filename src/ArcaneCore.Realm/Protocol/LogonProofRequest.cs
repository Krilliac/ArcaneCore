namespace ArcaneCore.Realm.Protocol;

/// <summary>
/// Parsed CMD_AUTH_LOGON_PROOF client body. Layout verified against vmangos
/// AuthPackets.h (sAuthLogonProof_C): A[32], M1[20], crc_hash[20], number_of_keys(1),
/// securityFlags(1, present for build >= 5428 / 1.11.0+).
/// </summary>
public sealed class LogonProofRequest
{
    public const int PublicKeyLength = 32;
    public const int ProofLength = 20;
    public const int CrcLength = 20;

    /// <summary>Body length for a 1.11.0+ client (includes the securityFlags byte).</summary>
    public const int BodyLength = PublicKeyLength + ProofLength + CrcLength + 1 + 1;

    public required byte[] ClientPublicKey { get; init; }

    public required byte[] ClientProof { get; init; }

    public required byte SecurityFlags { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> body, out LogonProofRequest? request)
    {
        request = null;
        if (body.Length < BodyLength)
        {
            return false;
        }

        byte[] publicKey = body.Slice(0, PublicKeyLength).ToArray();
        byte[] proof = body.Slice(PublicKeyLength, ProofLength).ToArray();
        byte securityFlags = body[PublicKeyLength + ProofLength + CrcLength + 1];

        request = new LogonProofRequest
        {
            ClientPublicKey = publicKey,
            ClientProof = proof,
            SecurityFlags = securityFlags,
        };
        return true;
    }
}
