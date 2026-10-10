using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ArcaneCore.Cryptography;

namespace ArcaneCore.MockClient.Protocol;

public static class LogonClient
{
    private const int MaximumRealmListSize = 16 * 1024;

    public static async Task<LogonResult> AuthenticateAsync(
        IPEndPoint endpoint, string account, string password, CancellationToken ct = default, ushort build = 5875,
        string? pin = null, byte[]? integrityHash = null, Action<bool, byte[]>? tap = null)
    {
        string username = ProtocolPackets.NormalizeAccount(account);
        string normalizedPassword = ProtocolPackets.NormalizePassword(password);
        using TcpClient client = await ProtocolIO.ConnectAsync(endpoint, ct).ConfigureAwait(false);
        await using Stream stream = tap is null ? client.GetStream() : new TappedStream(client.GetStream(), tap);

        (byte[] serverKey, byte[] salt, uint gridSeed, byte[] pinSalt) =
            await ProtocolIO.BoundedAsync("Logon challenge", ct, async token =>
        {
            await stream.WriteAsync(ProtocolPackets.LogonChallenge(username, build), token).ConfigureAwait(false);
            byte[] header = await ProtocolIO.ReadExactAsync(stream, 3, "logon challenge header", token).ConfigureAwait(false);
            ExpectCommand(header[0], 0x00, "challenge");
            if (header[2] != 0)
            {
                throw new MockAuthException(header[2], "challenge");
            }

            byte[] publicKey = await ProtocolIO.ReadExactAsync(stream, 32, "SRP6 server public key", token).ConfigureAwait(false);
            byte[] generatorLength = await ProtocolIO.ReadExactAsync(stream, 1, "SRP6 generator length", token).ConfigureAwait(false);
            if (generatorLength[0] != 1)
            {
                throw new MockProtocolException($"Unsupported SRP6 generator length {generatorLength[0]}.");
            }

            byte[] generator = await ProtocolIO.ReadExactAsync(stream, 1, "SRP6 generator", token).ConfigureAwait(false);
            byte[] primeLength = await ProtocolIO.ReadExactAsync(stream, 1, "SRP6 prime length", token).ConfigureAwait(false);
            if (primeLength[0] != 32)
            {
                throw new MockProtocolException($"Unsupported SRP6 prime length {primeLength[0]}.");
            }

            byte[] prime = await ProtocolIO.ReadExactAsync(stream, 32, "SRP6 prime", token).ConfigureAwait(false);
            if (generator[0] != 7 || !prime.AsSpan().SequenceEqual(ClientSrp6.PrimeBytes))
            {
                throw new MockProtocolException("Peer supplied SRP6 parameters outside the vanilla protocol.");
            }

            byte[] receivedSalt = await ProtocolIO.ReadExactAsync(stream, 32, "SRP6 salt", token).ConfigureAwait(false);
            byte[] challengeTail = await ProtocolIO.ReadExactAsync(stream, 17, "version challenge and security flag", token).ConfigureAwait(false);
            if (challengeTail[16] is not (0 or 1))
            {
                throw new MockProtocolException($"Unsupported logon security flags 0x{challengeTail[16]:X2}.");
            }

            byte[] extra = challengeTail[16] == 1
                ? await ProtocolIO.ReadExactAsync(stream, 20, "PIN grid seed and salt", token).ConfigureAwait(false)
                : [];

            return (publicKey, receivedSalt,
                extra.Length == 20 ? BinaryPrimitives.ReadUInt32LittleEndian(extra) : 0,
                extra.Length == 20 ? extra[4..] : []);
        }).ConfigureAwait(false);

        ClientSrp6.Session session = ClientSrp6.Create(username, normalizedPassword, salt, serverKey);
        try
        {
            byte[]? pinData = null;
            if (pinSalt.Length > 0 && pin is not null)
            {
                if (pin.Length is < 4 or > 10 || pin.Any(c => c is < '0' or > '9'))
                    throw new ArgumentException("PIN must be 4-10 decimal digits", nameof(pin));
                byte[] clientSalt = RandomNumberGenerator.GetBytes(PinHash.SaltLength);
                byte[] digest = PinHash.Calculate(pin.Select(c => (byte)(c - '0')).ToArray(),
                    gridSeed, pinSalt, clientSalt);
                pinData = [.. clientSalt, .. digest];
            }
            byte[]? crc = integrityHash is null ? null : ClientIntegrity.VersionProof(session.PublicKey, integrityHash);
            await ProtocolIO.BoundedAsync("Logon proof", ct, async token =>
            {
                await stream.WriteAsync(ProtocolPackets.LogonProof(session, crc, pinData), token).ConfigureAwait(false);
                byte[] header = await ProtocolIO.ReadExactAsync(stream, 2, "logon proof header", token).ConfigureAwait(false);
                ExpectCommand(header[0], 0x01, "proof");
                if (header[1] != 0)
                {
                    throw new MockAuthException(header[1], "proof");
                }

                byte[] proof = await ProtocolIO.ReadExactAsync(stream, 24, "logon server proof and survey id", token).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(session.ExpectedServerProof, proof.AsSpan(0, 20)))
                {
                    throw new MockProtocolException("SRP6 server proof M2 did not verify; session key was not released.");
                }

                return true;
            }).ConfigureAwait(false);

            IReadOnlyList<MockRealm> realms = await ProtocolIO.BoundedAsync("Realm list", ct, async token =>
            {
                await stream.WriteAsync(new byte[] { 0x10, 0, 0, 0, 0 }, token).ConfigureAwait(false);
                byte[] header = await ProtocolIO.ReadExactAsync(stream, 3, "realm list header", token).ConfigureAwait(false);
                ExpectCommand(header[0], 0x10, "realm list");
                int size = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1));
                if (size is < 7 or > MaximumRealmListSize)
                {
                    throw new MockProtocolException($"Realm list size {size} is outside 7..{MaximumRealmListSize} bytes.");
                }

                byte[] body = await ProtocolIO.ReadExactAsync(stream, size, "realm list body", token).ConfigureAwait(false);
                return ProtocolPackets.RealmList(body);
            }).ConfigureAwait(false);

            return new LogonResult((byte[])session.SessionKey.Clone(), realms);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(session.SessionKey);
        }
    }

    private static void ExpectCommand(byte actual, byte expected, string stage)
    {
        if (actual != expected)
        {
            throw new MockProtocolException($"Expected logon {stage} command 0x{expected:X2}, received 0x{actual:X2}.");
        }
    }

    /// <summary>Test tap over the logon stream: reports every chunk read (false) and written (true). Used by the replay transcript tests.</summary>
    private sealed class TappedStream(Stream inner, Action<bool, byte[]> tap) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = inner.Read(buffer, offset, count);
            tap(false, buffer.AsSpan(offset, read).ToArray());
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            tap(false, buffer[..read].ToArray());
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count)
        {
            tap(true, buffer.AsSpan(offset, count).ToArray());
            inner.Write(buffer, offset, count);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            tap(true, buffer.ToArray());
            return inner.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
