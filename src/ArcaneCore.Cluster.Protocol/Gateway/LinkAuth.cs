using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ArcaneCore.World.Cluster.Gateway;

/// <summary>
/// The key material of one authenticated link: the two per-direction frame-MAC keys derived from the shared link secret and both
/// handshake nonces. <see cref="ClientToServer"/> keys the frames the connecting end (gateway, peer client) sends,
/// <see cref="ServerToClient"/> the frames the listening end (worker, peer holder) sends. Immutable; both arrays are 32 bytes and must not be mutated.
/// </summary>
internal sealed class LinkKeys
{
    public LinkKeys(byte[] clientToServer, byte[] serverToClient)
    {
        ClientToServer = clientToServer;
        ServerToClient = serverToClient;
    }

    public byte[] ClientToServer { get; }

    public byte[] ServerToClient { get; }
}

/// <summary>
/// Mutual authentication and the per-frame MAC shared by the gateway-worker link and the peer link (link protocol v3, gateway contract
/// version 3, realm-service protocol version 2). Before this, only the connecting end proved the secret and frames carried no MAC, so the
/// link was safe only on a loopback or fully trusted network. Pure functions over byte spans; thread-safe.
/// <list type="number">
/// <item>The listener sends a random 32-byte challenge nonce (<c>nonceL</c>).</item>
/// <item>The connector answers with <c>HMAC(secret, nonceL)</c> (the legacy proof, <see cref="GatewaySessionWire.ComputeProof"/>) followed by its own random nonce (<c>nonceC</c>): 64 bytes.</item>
/// <item>The listener checks it and answers with <see cref="ComputeServerProof"/>, which is bound to both nonces and to a label no client proof can collide with, so the connector knows the listener holds the secret and the answer is fresh for this connection.</item>
/// <item>From the next frame on, in both directions, every frame carries a 16-byte tag (<see cref="LinkMac"/>): HMAC-SHA256 under the direction's key over an 8-byte frame counter and the frame. A flipped bit, a replayed, dropped or reordered frame, or a frame reflected to the other direction fails verification and the link is closed.</item>
/// </list>
/// A handshake recorded on one connection is useless on another: the listener's nonce differs per connection, so the recorded proof does not verify.
/// </summary>
internal static class LinkAuth
{
    private static readonly byte[] ServerProofLabel = Encoding.ASCII.GetBytes("arcanecore-link-v3-server-proof");
    private static readonly byte[] KeyInfo = Encoding.ASCII.GetBytes("arcanecore-link-v3-frame-keys");

    /// <summary>Bytes of the connector's proof frame body: the proof then its nonce.</summary>
    public const int ClientProofBodyBytes = GatewaySessionWire.NonceBytes * 2;

    /// <summary>The listener's proof: HMAC-SHA256 under the link secret over a fixed label and both nonces.</summary>
    public static byte[] ComputeServerProof(string linkSecret, ReadOnlySpan<byte> listenerNonce, ReadOnlySpan<byte> connectorNonce)
    {
        ArgumentNullException.ThrowIfNull(linkSecret);
        byte[] data = new byte[ServerProofLabel.Length + listenerNonce.Length + connectorNonce.Length];
        ServerProofLabel.CopyTo(data, 0);
        listenerNonce.CopyTo(data.AsSpan(ServerProofLabel.Length));
        connectorNonce.CopyTo(data.AsSpan(ServerProofLabel.Length + listenerNonce.Length));
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(linkSecret), data);
    }

    /// <summary>The two 32-byte frame keys (HKDF-SHA256, salt = both nonces, info = a fixed label).</summary>
    public static LinkKeys DeriveKeys(string linkSecret, ReadOnlySpan<byte> listenerNonce, ReadOnlySpan<byte> connectorNonce)
    {
        ArgumentNullException.ThrowIfNull(linkSecret);
        byte[] salt = new byte[listenerNonce.Length + connectorNonce.Length];
        listenerNonce.CopyTo(salt);
        connectorNonce.CopyTo(salt.AsSpan(listenerNonce.Length));
        byte[] okm = HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(linkSecret), 64, salt, KeyInfo);
        return new LinkKeys(okm[..32], okm[32..]);
    }

    /// <summary>A frame of one type byte then <paramref name="first"/> (and <paramref name="second"/> when given).</summary>
    public static byte[] Frame(byte type, ReadOnlySpan<byte> first, ReadOnlySpan<byte> second = default)
    {
        byte[] frame = new byte[1 + first.Length + second.Length];
        frame[0] = type;
        first.CopyTo(frame.AsSpan(1));
        second.CopyTo(frame.AsSpan(1 + first.Length));
        return frame;
    }

    /// <summary>
    /// Splits a connector proof frame body into its proof and nonce. False when the body is the 32-byte legacy proof of a pre-v3 peer
    /// (<paramref name="legacy"/> is then true) or any other length.
    /// </summary>
    public static bool TrySplitClientProof(ReadOnlySpan<byte> body, out byte[] proof, out byte[] nonce, out bool legacy)
    {
        proof = [];
        nonce = [];
        legacy = body.Length == GatewaySessionWire.NonceBytes;
        if (body.Length != ClientProofBodyBytes)
        {
            return false;
        }

        proof = body[..GatewaySessionWire.NonceBytes].ToArray();
        nonce = body[GatewaySessionWire.NonceBytes..].ToArray();
        return true;
    }
}

/// <summary>The per-frame MAC: a truncated HMAC-SHA256 over a frame counter and the frame. Stateless; the counters live in the channel.</summary>
internal static class LinkMac
{
    /// <summary>Bytes appended to every frame once the link is secured.</summary>
    public const int TagBytes = 16;

    /// <summary>Writes the tag of <paramref name="frame"/> as frame number <paramref name="counter"/> into <paramref name="tag"/> (<see cref="TagBytes"/> long).</summary>
    public static void Compute(byte[] key, ulong counter, ReadOnlySpan<byte> frame, Span<byte> tag)
    {
        int length = 8 + frame.Length;
        byte[] rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            BinaryPrimitives.WriteUInt64LittleEndian(rented, counter);
            frame.CopyTo(rented.AsSpan(8));
            Span<byte> hash = stackalloc byte[HMACSHA256.HashSizeInBytes];
            HMACSHA256.HashData(key, rented.AsSpan(0, length), hash);
            hash[..TagBytes].CopyTo(tag);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>True when <paramref name="tag"/> is the tag of <paramref name="frame"/> as frame number <paramref name="counter"/> (constant time).</summary>
    public static bool Verify(byte[] key, ulong counter, ReadOnlySpan<byte> frame, ReadOnlySpan<byte> tag)
    {
        Span<byte> expected = stackalloc byte[TagBytes];
        Compute(key, counter, frame, expected);
        return CryptographicOperations.FixedTimeEquals(expected, tag);
    }
}
