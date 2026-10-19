using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using ArcaneCore.Game;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.MockClient.Playbots;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.MockClient.Tests.Fuzz;

/// <summary>
/// Exception boundary and deterministic seeded fuzz tests for <c>ArcaneCore.MockClient</c>
/// protocol encoders/decoders, loopback guards, scenario wire parsers, CLI parsers, and playbot selectors.
/// </summary>
public sealed class MockClientFuzzAndExceptionTests
{
    /// <summary>
    /// Verifies that <c>ProtocolPackets.NormalizeAccount</c> and <c>ProtocolPackets.NormalizePassword</c>
    /// reject null, empty, overlong, and non-printable ASCII strings.
    /// </summary>
    [Fact]
    public void NormalizeCredentials_RejectsNullEmptyOverlongAndNonPrintableCharacters()
    {
        Assert.Throws<ArgumentNullException>(() => ProtocolPackets.NormalizeAccount(null!));
        Assert.Throws<ArgumentNullException>(() => ProtocolPackets.NormalizePassword(null!));

        foreach (string invalid in new[] { "", "ABCDEFGHIJKLMNOPQ", "USER\0NAME", "USER\rNAME", "USER\nNAME", "USER\x1F", "USER\x7F", "USER\u00E9" })
        {
            Assert.Throws<ArgumentException>(() => ProtocolPackets.NormalizeAccount(invalid));
            Assert.Throws<ArgumentException>(() => ProtocolPackets.NormalizePassword(invalid));
        }

        Assert.Equal("A", ProtocolPackets.NormalizeAccount("a"));
        Assert.Equal("ABCDEFGHIJKLMNOP", ProtocolPackets.NormalizePassword("abcdefghijklmnop"));
    }

    /// <summary>
    /// Verifies that <c>ProtocolPackets.LogonProof</c> and <c>ProtocolPackets.ClientHeader</c>
    /// enforce exact buffer and payload length boundaries.
    /// </summary>
    [Fact]
    public void LogonProofAndClientHeader_EnforceLengthAndArithmeticBounds()
    {
        var session = new ClientSrp6.Session(new byte[32], new byte[20], new byte[20], new byte[40]);

        Assert.Throws<ArgumentException>(() => ProtocolPackets.LogonProof(session, crcHash: new byte[19]));
        Assert.Throws<ArgumentException>(() => ProtocolPackets.LogonProof(session, crcHash: new byte[21]));
        Assert.Throws<ArgumentException>(() => ProtocolPackets.LogonProof(session, crcHash: new byte[20], pinData: new byte[35]));
        Assert.Throws<ArgumentException>(() => ProtocolPackets.LogonProof(session, crcHash: new byte[20], pinData: new byte[37]));

        byte[] validProofWithPin = ProtocolPackets.LogonProof(session, crcHash: new byte[20], pinData: new byte[36]);
        Assert.Equal(111, validProofWithPin.Length);
        Assert.Equal(1, validProofWithPin[74]);

        Assert.Throws<OverflowException>(() => ProtocolPackets.ClientHeader(0x01ED, -5));
        Assert.Throws<OverflowException>(() => ProtocolPackets.ClientHeader(0x01ED, 65532));

        byte[] maxHeader = ProtocolPackets.ClientHeader(0x01ED, 65531);
        Assert.Equal(6, maxHeader.Length);
        Assert.Equal(ushort.MaxValue, BinaryPrimitives.ReadUInt16BigEndian(maxHeader));
    }

    /// <summary>
    /// Verifies that <c>ProtocolPackets.RealmList</c> rejects truncated bodies, excessive counts,
    /// unterminated or oversized C-strings, non-ASCII bytes, and trailing bytes.
    /// </summary>
    [Fact]
    public void RealmList_RejectsMalformedTruncatedOversizedAndNonAsciiPayloads()
    {
        // Truncated before 4-byte prefix + count byte
        Assert.Throws<MockProtocolException>(() => ProtocolPackets.RealmList([]));
        Assert.Throws<MockProtocolException>(() => ProtocolPackets.RealmList([0, 0, 0, 0]));

        // Count > 128
        Assert.Throws<MockProtocolException>(() => ProtocolPackets.RealmList([0, 0, 0, 0, 129, 0, 0]));

        // Missing trailing 2 bytes on 0-count realm list
        Assert.Throws<MockProtocolException>(() => ProtocolPackets.RealmList([0, 0, 0, 0, 0, 0]));

        // Extra trailing byte on 0-count realm list
        Assert.Throws<MockProtocolException>(() => ProtocolPackets.RealmList([0, 0, 0, 0, 0, 0, 0, 1]));

        // Valid 0-count realm list
        Assert.Empty(ProtocolPackets.RealmList([0, 0, 0, 0, 0, 0, 0]));

        // Unterminated name string
        byte[] unterminated = [0, 0, 0, 0, 1, 0, 0, 0, 0, 0, (byte)'A', (byte)'B'];
        Assert.Throws<MockProtocolException>(() => ProtocolPackets.RealmList(unterminated));

        // Non-ASCII byte in realm name
        byte[] nonAscii = [0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0x80, 0, (byte)'1', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.Throws<MockProtocolException>(() => ProtocolPackets.RealmList(nonAscii));

        // Oversized realm name (> 512 bytes)
        var oversizedName = new List<byte> { 0, 0, 0, 0, 1, 0, 0, 0, 0, 0 };
        oversizedName.AddRange(Enumerable.Repeat((byte)'A', 513));
        oversizedName.Add(0);
        oversizedName.AddRange(Encoding.ASCII.GetBytes("127.0.0.1:8085\0"));
        oversizedName.AddRange(new byte[9]);
        Assert.Throws<MockProtocolException>(() => ProtocolPackets.RealmList(oversizedName.ToArray()));

        // Oversized realm address (> 256 bytes)
        var oversizedAddress = new List<byte> { 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, (byte)'R', 0 };
        oversizedAddress.AddRange(Enumerable.Repeat((byte)'1', 257));
        oversizedAddress.Add(0);
        oversizedAddress.AddRange(new byte[9]);
        Assert.Throws<MockProtocolException>(() => ProtocolPackets.RealmList(oversizedAddress.ToArray()));
    }

    /// <summary>
    /// Verifies that <c>LoopbackOnly.Validate</c> and <c>MockRealm.GetLoopbackEndpoint</c>
    /// reject null, non-numeric, non-loopback, and port-zero endpoints.
    /// </summary>
    [Fact]
    public void LoopbackOnlyAndMockRealm_EnforceLoopbackAndPortConstraints()
    {
        Assert.Throws<ArgumentNullException>(() => LoopbackOnly.Validate(null!));
        Assert.Throws<ArgumentException>(() => LoopbackOnly.Validate(new IPEndPoint(IPAddress.Loopback, 0)));
        Assert.Throws<ArgumentException>(() => LoopbackOnly.Validate(new IPEndPoint(IPAddress.Parse("10.0.0.1"), 3724)));
        Assert.Throws<ArgumentException>(() => LoopbackOnly.Validate(new IPEndPoint(IPAddress.Parse("192.168.1.1"), 8085)));
        Assert.Throws<ArgumentException>(() => LoopbackOnly.Validate(new IPEndPoint(IPAddress.IPv6Any, 8085)));

        LoopbackOnly.Validate(new IPEndPoint(IPAddress.Loopback, 1));
        LoopbackOnly.Validate(new IPEndPoint(IPAddress.IPv6Loopback, 65535));
        LoopbackOnly.Validate(new IPEndPoint(IPAddress.Loopback.MapToIPv6(), 8085));

        Assert.Throws<MockProtocolException>(() => new MockRealm("Test", "localhost:8085").GetLoopbackEndpoint());
        Assert.Throws<MockProtocolException>(() => new MockRealm("Test", "not-an-endpoint").GetLoopbackEndpoint());
        Assert.Throws<ArgumentException>(() => new MockRealm("Test", "10.1.2.3:8085").GetLoopbackEndpoint());
        Assert.Throws<ArgumentException>(() => new MockRealm("Test", "127.0.0.1:0").GetLoopbackEndpoint());

        IPEndPoint valid = new MockRealm("Test", "127.0.0.1:8085").GetLoopbackEndpoint();
        Assert.Equal(IPAddress.Loopback, valid.Address);
        Assert.Equal(8085, valid.Port);

        var authEx = new MockAuthException(0x04, "challenge");
        Assert.Equal((byte)0x04, authEx.Result);
        Assert.Contains("0x04", authEx.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that <c>ClientHeaderCipher</c> and <c>ClientSrp6</c> reject invalid key lengths
    /// and out-of-range cryptographic parameters.
    /// </summary>
    [Fact]
    public void ClientHeaderCipherAndClientSrp6_RejectInvalidSizesAndInvalidPublicKeys()
    {
        Assert.Throws<ArgumentException>(() => new ClientHeaderCipher([]));
        Assert.Throws<ArgumentException>(() => new ClientHeaderCipher(new byte[39]));
        Assert.Throws<ArgumentException>(() => new ClientHeaderCipher(new byte[41]));

        byte[] validSalt = Enumerable.Repeat((byte)0x11, 32).ToArray();
        byte[] validServerPub = Enumerable.Repeat((byte)0x02, 32).ToArray();
        byte[] validPriv = Enumerable.Repeat((byte)0x03, 32).ToArray();

        Assert.Throws<MockProtocolException>(() => ClientSrp6.Calculate("ACC", "PASS", new byte[31], validServerPub, validPriv));
        Assert.Throws<MockProtocolException>(() => ClientSrp6.Calculate("ACC", "PASS", validSalt, new byte[31], validPriv));
        Assert.Throws<MockProtocolException>(() => ClientSrp6.Calculate("ACC", "PASS", validSalt, validServerPub, new byte[31]));

        // Zero server public key, server public key == Prime, and zero private key must be rejected
        Assert.Throws<MockProtocolException>(() => ClientSrp6.Calculate("ACC", "PASS", validSalt, new byte[32], validPriv));
        Assert.Throws<MockProtocolException>(() => ClientSrp6.Calculate("ACC", "PASS", validSalt, (byte[])ClientSrp6.PrimeBytes.Clone(), validPriv));
        Assert.Throws<MockProtocolException>(() => ClientSrp6.Calculate("ACC", "PASS", validSalt, validServerPub, new byte[32]));

        Assert.Throws<ArgumentException>(() => ClientSrp6.Interleave(new byte[31]));
        Assert.Throws<ArgumentException>(() => ClientSrp6.Interleave(new byte[33]));

        // All-zero and leading-zero 32-byte shared secrets must interleave into 40-byte keys without throwing
        byte[] allZeroKey = ClientSrp6.Interleave(new byte[32]);
        Assert.Equal(40, allZeroKey.Length);

        byte[] oddLeadingZero = new byte[32];
        oddLeadingZero[1] = 0x5A;
        Assert.Equal(40, ClientSrp6.Interleave(oddLeadingZero).Length);
    }

    /// <summary>
    /// Verifies that <c>ProtocolIO.BoundedAsync</c> and <c>ProtocolIO.ReadExactAsync</c>
    /// translate internal deadline expiry into <c>TimeoutException</c>, preserve caller cancellation,
    /// and throw <c>EndOfStreamException</c> on premature stream closure.
    /// </summary>
    [Fact]
    public async Task ProtocolIO_HandlesDeadlinesCallerCancellationAndTruncatedStreams()
    {
        await Assert.ThrowsAsync<TimeoutException>(() =>
            ProtocolIO.BoundedAsync("test-op", TimeSpan.FromMilliseconds(20), CancellationToken.None, async token =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token);
                return true;
            }));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProtocolIO.BoundedAsync("test-op", TimeSpan.FromSeconds(5), cts.Token, token =>
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult(true);
            }));

        using var partialStream = new MemoryStream([1, 2, 3]);
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            ProtocolIO.ReadExactAsync(partialStream, 5, "test-field", CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            ProtocolIO.ConnectAsync(new IPEndPoint(IPAddress.Parse("10.0.0.1"), 3724), CancellationToken.None));
    }

    /// <summary>
    /// Verifies that <c>LogonClient.AuthenticateAsync</c> rejects malformed challenge headers,
    /// non-zero challenge/proof statuses, non-vanilla SRP6 parameters, invalid security flags,
    /// invalid PIN formats, forged M2 server proofs, and out-of-range realm list sizes.
    /// </summary>
    [Fact]
    public async Task LogonClient_RejectsInvalidPeerResponsesAndInvalidPinInput()
    {
        static byte[] BuildValidChallenge(byte securityFlag = 0)
        {
            var bytes = new List<byte> { 0x00, 0x00, 0x00 };
            bytes.AddRange(Enumerable.Repeat((byte)0x02, 32)); // B
            bytes.Add(1); // g len
            bytes.Add(7); // g
            bytes.Add(32); // N len
            bytes.AddRange(ClientSrp6.PrimeBytes);
            bytes.AddRange(Enumerable.Repeat((byte)0x11, 32)); // salt
            bytes.AddRange(new byte[16]); // version challenge
            bytes.Add(securityFlag);
            if (securityFlag == 1)
            {
                bytes.AddRange(new byte[20]); // PIN grid seed + salt
            }

            return bytes.ToArray();
        }

        // 1. Wrong challenge command
        await WithLoopbackPeerAsync(async stream =>
        {
            _ = await stream.ReadAsync(new byte[64]);
            await stream.WriteAsync(new byte[] { 0x01, 0x00, 0x00 });
        }, endpoint => Assert.ThrowsAsync<MockProtocolException>(() =>
            LogonClient.AuthenticateAsync(endpoint, "ACC", "PASS")));

        // 2. Non-zero challenge status -> MockAuthException
        await WithLoopbackPeerAsync(async stream =>
        {
            _ = await stream.ReadAsync(new byte[64]);
            await stream.WriteAsync(new byte[] { 0x00, 0x00, 0x04 });
        }, async endpoint =>
        {
            MockAuthException ex = await Assert.ThrowsAsync<MockAuthException>(() =>
                LogonClient.AuthenticateAsync(endpoint, "ACC", "PASS"));
            Assert.Equal((byte)0x04, ex.Result);
        });

        // 3. Unsupported generator length
        await WithLoopbackPeerAsync(async stream =>
        {
            _ = await stream.ReadAsync(new byte[64]);
            byte[] packet = BuildValidChallenge();
            packet[35] = 2;
            await stream.WriteAsync(packet);
        }, endpoint => Assert.ThrowsAsync<MockProtocolException>(() =>
            LogonClient.AuthenticateAsync(endpoint, "ACC", "PASS")));

        // 4. Unsupported prime length
        await WithLoopbackPeerAsync(async stream =>
        {
            _ = await stream.ReadAsync(new byte[64]);
            byte[] packet = BuildValidChallenge();
            packet[37] = 31;
            await stream.WriteAsync(packet);
        }, endpoint => Assert.ThrowsAsync<MockProtocolException>(() =>
            LogonClient.AuthenticateAsync(endpoint, "ACC", "PASS")));

        // 5. Non-vanilla generator value
        await WithLoopbackPeerAsync(async stream =>
        {
            _ = await stream.ReadAsync(new byte[64]);
            byte[] packet = BuildValidChallenge();
            packet[36] = 5;
            await stream.WriteAsync(packet);
        }, endpoint => Assert.ThrowsAsync<MockProtocolException>(() =>
            LogonClient.AuthenticateAsync(endpoint, "ACC", "PASS")));

        // 6. Unsupported security flag (2)
        await WithLoopbackPeerAsync(async stream =>
        {
            _ = await stream.ReadAsync(new byte[64]);
            byte[] packet = BuildValidChallenge();
            packet[118] = 2;
            await stream.WriteAsync(packet);
        }, endpoint => Assert.ThrowsAsync<MockProtocolException>(() =>
            LogonClient.AuthenticateAsync(endpoint, "ACC", "PASS")));

        // 7. Security flag 1 + invalid PIN format -> ArgumentException
        await WithLoopbackPeerAsync(async stream =>
        {
            _ = await stream.ReadAsync(new byte[64]);
            await stream.WriteAsync(BuildValidChallenge(securityFlag: 1));
        }, endpoint => Assert.ThrowsAsync<ArgumentException>(() =>
            LogonClient.AuthenticateAsync(endpoint, "ACC", "PASS", pin: "12a4")));

        // 8. Proof response with non-zero status -> MockAuthException
        await WithLoopbackPeerAsync(async stream =>
        {
            _ = await stream.ReadAsync(new byte[64]);
            await stream.WriteAsync(BuildValidChallenge());
            _ = await ProtocolIO.ReadExactAsync(stream, 75, "proof", CancellationToken.None);
            await stream.WriteAsync(new byte[] { 0x01, 0x04 });
        }, async endpoint =>
        {
            MockAuthException ex = await Assert.ThrowsAsync<MockAuthException>(() =>
                LogonClient.AuthenticateAsync(endpoint, "ACC", "PASS"));
            Assert.Equal((byte)0x04, ex.Result);
        });

        // 9. Bad M2 proof -> MockProtocolException
        await WithLoopbackPeerAsync(async stream =>
        {
            _ = await stream.ReadAsync(new byte[64]);
            await stream.WriteAsync(BuildValidChallenge());
            _ = await ProtocolIO.ReadExactAsync(stream, 75, "proof", CancellationToken.None);
            byte[] badProof = new byte[26];
            badProof[0] = 0x01;
            await stream.WriteAsync(badProof);
        }, endpoint => Assert.ThrowsAsync<MockProtocolException>(() =>
            LogonClient.AuthenticateAsync(endpoint, "ACC", "PASS")));
    }

    /// <summary>
    /// Verifies that <c>WorldClient</c> enforces session key length, single authentication,
    /// plaintext auth response validation, payload bounds, and packet search bounds.
    /// </summary>
    [Fact]
    public async Task WorldClient_EnforcesHandshakeAndFramingConstraints()
    {
        byte[] validSessionKey = Enumerable.Repeat((byte)0x22, 40).ToArray();

        // Plaintext AuthOk (0x0C) must be rejected as MockProtocolException
        await WithLoopbackPeerAsync(async stream =>
        {
            await stream.WriteAsync(new byte[] { 0, 6, 0xEC, 0x01, 1, 2, 3, 4 });
            _ = await stream.ReadAsync(new byte[128]);
            await stream.WriteAsync(new byte[] { 0, 3, 0xEE, 0x01, 0x0C });
            while (await stream.ReadAsync(new byte[128]) > 0)
            {
            }
        }, async endpoint =>
        {
            await using WorldClient client = await WorldClient.ConnectAsync(endpoint);
            await Assert.ThrowsAsync<ArgumentNullException>(() => client.AuthenticateAsync("ACC", null!));
            await Assert.ThrowsAsync<ArgumentException>(() => client.AuthenticateAsync("ACC", new byte[39]));
            await Assert.ThrowsAnyAsync<Exception>(() => client.AuthenticateAsync("ACC", validSessionKey));
        });

        // Plaintext rejection (e.g. 0x15) returns result code and closes client; subsequent SendAsync throws ObjectDisposedException
        await WithLoopbackPeerAsync(async stream =>
        {
            await stream.WriteAsync(new byte[] { 0, 6, 0xEC, 0x01, 1, 2, 3, 4 });
            _ = await stream.ReadAsync(new byte[128]);
            await stream.WriteAsync(new byte[] { 0, 3, 0xEE, 0x01, 0x15 });
            while (await stream.ReadAsync(new byte[128]) > 0)
            {
            }
        }, async endpoint =>
        {
            await using WorldClient client = await WorldClient.ConnectAsync(endpoint);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                client.SendAsync(0x01DC, new byte[0x2800 - 3]));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                client.ReadUntilAsync(0x01DC, maxPackets: 0));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                client.ReadUntilAsync(0x01DC, maxPackets: 1025));

            byte code = await client.AuthenticateAsync("ACC", validSessionKey);
            Assert.Equal((byte)0x15, code);
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.AuthenticateAsync("ACC", validSessionKey));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => client.SendAsync(0x01DC, new byte[4]));
        });

        // Invalid server frame size (< 2) must throw MockProtocolException
        await WithLoopbackPeerAsync(async stream =>
        {
            await stream.WriteAsync(new byte[] { 0, 1, 0xEC, 0x01 });
            while (await stream.ReadAsync(new byte[128]) > 0)
            {
            }
        }, async endpoint =>
        {
            await using WorldClient client = await WorldClient.ConnectAsync(endpoint);
            await Assert.ThrowsAsync<MockProtocolException>(() => client.ReadAsync());
        });
    }

    /// <summary>
    /// Verifies that <c>ScenarioWire</c> character, login, self-create, field-update, and compressed-update
    /// decoders reject out-of-range counts, invalid flags, non-finite coordinates, and corrupt streams.
    /// </summary>
    [Fact]
    public void ScenarioWire_CoreDecoders_RejectInvalidCountsFlagsAndNonFiniteFloats()
    {
        // CharacterList: count > 10
        Assert.Throws<MockProtocolException>(() => ScenarioWire.CharacterList([11]));
        // CharacterList: trailing bytes on 0-count
        Assert.Throws<MockProtocolException>(() => ScenarioWire.CharacterList([0, 99]));
        Assert.Empty(ScenarioWire.CharacterList([0]));

        // LoginLocation: must be exactly 20 bytes
        Assert.Throws<MockProtocolException>(() => ScenarioWire.LoginLocation(new byte[19]));
        Assert.Throws<MockProtocolException>(() => ScenarioWire.LoginLocation(new byte[21]));
        Assert.Equal(0u, ScenarioWire.LoginLocation(new byte[20]).Map);

        // IsMapTransportsPacket: false on empty, count 0, count > 128, hasTransport != 1, non-GameObject
        Assert.False(ScenarioWire.IsMapTransportsPacket([]));
        Assert.False(ScenarioWire.IsMapTransportsPacket([0, 0, 0, 0, 1]));
        Assert.False(ScenarioWire.IsMapTransportsPacket([129, 0, 0, 0, 1]));
        Assert.False(ScenarioWire.IsMapTransportsPacket([1, 0, 0, 0, 0]));
        Assert.False(ScenarioWire.IsMapTransportsPacket([1, 0, 0, 0, 1, 2, 1, 1, TypeId.Unit, 0, 0]));
        Assert.True(ScenarioWire.IsMapTransportsPacket([1, 0, 0, 0, 1, 2, 1, 1, TypeId.GameObject, 0, 0]));

        // SelfCreate: count 0, count > 128, hasTransport != 0, missing self player
        Assert.Throws<MockProtocolException>(() => ScenarioWire.SelfCreate([0, 0, 0, 0, 0]));
        Assert.Throws<MockProtocolException>(() => ScenarioWire.SelfCreate([129, 0, 0, 0, 0]));
        Assert.Throws<MockProtocolException>(() => ScenarioWire.SelfCreate([1, 0, 0, 0, 1]));
        Assert.Throws<MockProtocolException>(() => ScenarioWire.SelfCreate([1, 0, 0, 0, 0, 2, 1, 1, TypeId.Unit, 0, 0]));

        // FieldUpdates & RemovedGuids: count 0, count > 128, invalid hasTransport, unknown block type, maskCount > 64
        Assert.Throws<MockProtocolException>(() => ScenarioWire.FieldUpdates([0, 0, 0, 0, 0]));
        Assert.Throws<MockProtocolException>(() => ScenarioWire.FieldUpdates([1, 0, 0, 0, 2]));
        Assert.Throws<MockProtocolException>(() => ScenarioWire.FieldUpdates([1, 0, 0, 0, 0, 6]));
        Assert.Throws<MockProtocolException>(() => ScenarioWire.FieldUpdates([1, 0, 0, 0, 0, 4, 129, 0, 0, 0]));
        Assert.Throws<MockProtocolException>(() => ScenarioWire.FieldUpdates([1, 0, 0, 0, 0, 0, 1, 1, 65]));
        Assert.Throws<MockProtocolException>(() => ScenarioWire.RemovedGuids([0, 0, 0, 0, 0]));
        Assert.Throws<MockProtocolException>(() => ScenarioWire.RemovedGuids([1, 0, 0, 0, 0, 6]));

        // Movement flag 0x80 and NaN position in FieldUpdates
        Assert.Throws<MockProtocolException>(() => ScenarioWire.FieldUpdates([1, 0, 0, 0, 0, 1, 1, 1, 0x80]));
        byte[] nanPositionUpdate = new byte[5 + 1 + 2 + 1 + 16];
        nanPositionUpdate[0] = 1; // count = 1
        nanPositionUpdate[5] = 1; // movement block
        nanPositionUpdate[6] = 1; // packed guid mask
        nanPositionUpdate[7] = 1; // guid = 1
        nanPositionUpdate[8] = 0x40; // HAS_POSITION
        BinaryPrimitives.WriteSingleLittleEndian(nanPositionUpdate.AsSpan(9), float.NaN);
        Assert.Throws<MockProtocolException>(() => ScenarioWire.FieldUpdates(nanPositionUpdate));

        // InflateUpdate: length <= 4, size < 5, size > MaximumInflatedBytes, expanding past size, corrupt zlib
        Assert.Throws<MockProtocolException>(() => ScenarioWire.InflateUpdate([1, 2, 3, 4]));
        Assert.Throws<MockProtocolException>(() => ScenarioWire.InflateUpdate([4, 0, 0, 0, 0x78]));
        Assert.Throws<MockProtocolException>(() => ScenarioWire.InflateUpdate([1, 0, 0x10, 0, 0x78]));
        Assert.Throws<InvalidDataException>(() => ScenarioWire.InflateUpdate([5, 0, 0, 0, 0xFF, 0xFF, 0xFF]));

        byte[] rawSixBytes = [1, 2, 3, 4, 5, 6];
        using var compressedStream = new MemoryStream();
        compressedStream.Write(BitConverter.GetBytes(5u)); // declare 5 but compress 6 bytes
        using (var zlib = new ZLibStream(compressedStream, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(rawSixBytes);
        }

        Assert.Throws<MockProtocolException>(() => ScenarioWire.InflateUpdate(compressedStream.ToArray()));
    }

    /// <summary>
    /// Verifies that <c>ScenarioWire</c> quest, gossip, and monster-move decoders reject invalid
    /// bounds, unknown move types, non-finite coordinates, and trailing bytes.
    /// </summary>
    [Fact]
    public void ScenarioWire_QuestGossipAndMonsterMove_RejectOutOfRangeFields()
    {
        Assert.Throws<MockProtocolException>(() => ScenarioWire.QuestgiverStatus(new byte[11]));
        Assert.Throws<MockProtocolException>(() => ScenarioWire.QuestgiverStatus(new byte[13]));
        Assert.Throws<MockProtocolException>(() => ScenarioWire.QuestKill(new byte[23]));
        Assert.Throws<MockProtocolException>(() => ScenarioWire.QuestKill(new byte[25]));

        // QuestComplete with rewardCount = 5 (> 4)
        byte[] questCompleteTooMany = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(questCompleteTooMany.AsSpan(16), 5);
        Assert.Throws<MockProtocolException>(() => ScenarioWire.QuestComplete(questCompleteTooMany));

        // GossipMessage with option count = 33 (> 32)
        byte[] gossipTooMany = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(gossipTooMany.AsSpan(12), 33);
        Assert.Throws<MockProtocolException>(() => ScenarioWire.GossipMessage(gossipTooMany));

        // GossipMessage with coded = 2 (> 1)
        byte[] gossipBadCoded = new byte[16 + 6 + 2 + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(gossipBadCoded.AsSpan(12), 1);
        gossipBadCoded[21] = 2; // coded = 2
        Assert.Throws<MockProtocolException>(() => ScenarioWire.GossipMessage(gossipBadCoded));

        // MonsterMove: empty guid (mask = 0)
        Assert.Throws<MockProtocolException>(() => ScenarioWire.MonsterMove(new byte[18]));

        // MonsterMove: valid stop packet (type = 1)
        byte[] stopMove = new byte[2 + 12 + 4 + 1];
        stopMove[0] = 1;
        stopMove[1] = 42;
        stopMove[18] = 1;
        MockMonsterMove parsedStop = ScenarioWire.MonsterMove(stopMove);
        Assert.True(parsedStop.IsStop);
        Assert.Equal(42ul, parsedStop.Guid);

        // MonsterMove: unknown type = 5
        byte[] badTypeMove = (byte[])stopMove.Clone();
        badTypeMove[18] = 5;
        Assert.Throws<MockProtocolException>(() => ScenarioWire.MonsterMove(badTypeMove));

        // MonsterMove: type 3 with zero target GUID
        byte[] zeroTargetMove = new byte[2 + 12 + 4 + 1 + 8 + 12 + 12];
        zeroTargetMove[0] = 1;
        zeroTargetMove[1] = 42;
        zeroTargetMove[18] = 3;
        Assert.Throws<MockProtocolException>(() => ScenarioWire.MonsterMove(zeroTargetMove));
    }

    /// <summary>
    /// Verifies that scenario helpers (<c>LiveSession</c>, <c>ItemFeedbackProbe</c>, <c>StartingZoneProbe</c>,
    /// <c>StartingZoneCombat</c>, <c>StartingZoneLoot</c>, and <c>StartingZoneFood</c>) enforce all argument
    /// and wire invariants.
    /// </summary>
    [Fact]
    public void ScenarioHelpers_EnforceCliAndWireInvariants()
    {
        const string EnvVar = "ARCANE_FUZZ_TEST_PW";
        Environment.SetEnvironmentVariable(EnvVar, "SECRET123");
        try
        {
            // LiveSession.Parse invalid cases
            Assert.Throws<ArgumentException>(() => LiveSession.Parse(["--account"]));
            Assert.Throws<ArgumentException>(() => LiveSession.Parse(["--unknown", "1"]));
            Assert.Throws<ArgumentException>(() => LiveSession.Parse(["--password-env", EnvVar])); // missing --account
            Assert.Throws<ArgumentException>(() => LiveSession.Parse(["--account", "A", "--password-env", "UNSET_VAR_XYZ"]));
            Assert.Throws<ArgumentException>(() => LiveSession.Parse(["--account", "A", "--password-env", EnvVar, "--interval-ms", "49"]));
            Assert.Throws<ArgumentException>(() => LiveSession.Parse(["--account", "A", "--password-env", EnvVar, "--duration-s", "0"]));
            Assert.Throws<ArgumentException>(() => LiveSession.Parse(["--account", "A", "--password-env", EnvVar, "--realm", "not-an-ip"]));
            Assert.Throws<ArgumentException>(() => LiveSession.Parse(["--account", "A", "--password-env", EnvVar, "--realm", "10.0.0.1:3724"]));

            // LiveSession chat helpers
            Assert.Null(LiveSession.DecodePlayerLine([1, 2, 3]));
            Assert.Null(LiveSession.DecodeSystemLine([1, 2, 3]));
            Assert.Equal("hello", LiveSession.DecodeNotification("hello\0world"u8.ToArray()));
            Assert.Equal((uint)ChatType.Whisper, BinaryPrimitives.ReadUInt32LittleEndian(LiveSession.ScriptPacket("/w Bob hi there")));
            Assert.Equal((uint)ChatType.Say, BinaryPrimitives.ReadUInt32LittleEndian(LiveSession.ScriptPacket("/w Bob   ")));

            // ItemFeedbackProbe validation
            Assert.Throws<MockProtocolException>(() =>
                ItemFeedbackProbe.IsOwnSuccess(new WorldFrame((ushort)WorldOpcode.SmsgItemPushResult, [1, 2, 3]), 1));
            Assert.Throws<MockProtocolException>(() =>
                ItemFeedbackProbe.IsOwnSuccess(new WorldFrame((ushort)WorldOpcode.SmsgSpellGo, [0xFF, 1]), 1));
            Assert.NotNull(ItemFeedbackProbe.ValidateFailure(new WorldFrame((ushort)WorldOpcode.SmsgPong, new byte[18])));
            Assert.NotNull(ItemFeedbackProbe.ValidateFailure(new WorldFrame((ushort)WorldOpcode.SmsgInventoryChangeFailure, new byte[17])));
            Assert.NotNull(ItemFeedbackProbe.ValidateFailure(new WorldFrame((ushort)WorldOpcode.SmsgInventoryChangeFailure, new byte[18])));

            byte[] validFailure = new byte[18];
            validFailure[0] = 23; // InventoryResult.ItemNotFound
            Assert.Null(ItemFeedbackProbe.ValidateFailure(new WorldFrame((ushort)WorldOpcode.SmsgInventoryChangeFailure, validFailure)));

            // StartingZoneProbe.Parse invalid combinations
            Assert.Throws<ArgumentException>(() => StartingZoneProbe.Parse(["--loot", "maybe"]));
            Assert.Throws<ArgumentException>(() => StartingZoneProbe.Parse(["--heroic", "maybe"]));
            Assert.Throws<ArgumentException>(() => StartingZoneProbe.Parse(["--complete-quest", "maybe"]));
            Assert.Throws<ArgumentException>(() => StartingZoneProbe.Parse(["--combat-spawn", "abc"]));
            Assert.Throws<ArgumentException>(() => StartingZoneProbe.Parse(["--combat-position", "1,2,NaN"]));
            Assert.Throws<ArgumentException>(() => StartingZoneProbe.Parse(["--password-env", EnvVar, "--combat-spawn", "100"]));
            Assert.Throws<ArgumentException>(() => StartingZoneProbe.Parse(["--password-env", EnvVar, "--loot", "true"]));
            Assert.Throws<ArgumentException>(() => StartingZoneProbe.Parse(["--password-env", EnvVar, "--heroic", "true"]));
            Assert.Throws<ArgumentException>(() => StartingZoneProbe.Parse(["--password-env", EnvVar, "--complete-quest", "true"]));
            Assert.Throws<ArgumentException>(() => StartingZoneProbe.Parse([
                "--password-env", EnvVar, "--combat-spawn", "100", "--combat-position", "1,2,3",
                "--loot", "true", "--complete-quest", "true"
            ]));

            // StartingZoneProbe.QuestRefusal
            Assert.Throws<MockProtocolException>(() =>
                StartingZoneProbe.QuestRefusal(WorldOpcode.SmsgQuestgiverQuestInvalid, [1, 2], 7));
            Assert.Contains("quest 7", StartingZoneProbe.QuestRefusal(WorldOpcode.SmsgQuestlogFull, [], 7), StringComparison.Ordinal);

            // StartingZoneCombat
            Assert.Throws<ArgumentOutOfRangeException>(() => new StartingZoneCombat.CombatBudget(0, 1024));
            Assert.Throws<ArgumentOutOfRangeException>(() => new StartingZoneCombat.CombatBudget(10, 0));
            Assert.False(StartingZoneCombat.TryReadAttackerState([1, 2, 3], out _));
            Assert.Throws<MockProtocolException>(() => StartingZoneCombat.ThrowIfObservedPlayerDeath(
                new Dictionary<int, uint> { [UpdateFields.UnitFieldHealth] = 0 }, 1, 10, 7, 1, false, false, false));

            // StartingZoneLoot
            Assert.Throws<MockProtocolException>(() => StartingZoneLoot.ParseWindow([]));
            Assert.Throws<MockProtocolException>(() => StartingZoneLoot.ParseWindow(new byte[14])); // zero guid / type 0
            Assert.Throws<MockProtocolException>(() => StartingZoneLoot.ParseItemPush(new byte[40]));
            Assert.Throws<MockProtocolException>(() => StartingZoneLoot.ParseItemPush(new byte[41]));

            // StartingZoneFood
            Assert.Throws<MockProtocolException>(() => StartingZoneFood.BuildUseItemPayload(22));
            Assert.Throws<MockProtocolException>(() => StartingZoneFood.BuildUseItemPayload(39));
            Assert.NotEmpty(StartingZoneFood.BuildUseItemPayload(23));
            Assert.Throws<MockProtocolException>(() => StartingZoneFood.RequireFoodIdentity((1, 23, 5), (1, 23, 4)));
            Assert.Throws<MockProtocolException>(() => StartingZoneFood.FindFood(
                new Dictionary<int, uint>(), 1, _ => new Dictionary<int, uint>()));
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVar, null);
        }
    }

    /// <summary>
    /// Verifies that <c>AutonomousPlaybot</c>, <c>PlaybotCommand</c>, <c>OllamaPlaybotSelector</c>,
    /// and hosting fixtures validate options, endpoints, spell payloads, and ownership markers.
    /// </summary>
    [Fact]
    public async Task PlaybotAndHosting_EnforceValidationAndOwnershipGuards()
    {
        var loopback = new IPEndPoint(IPAddress.Loopback, 3724);
        Assert.Throws<ArgumentException>(() => AutonomousPlaybot.Validate(new PlaybotRunOptions(loopback, "ACC", "PASS", "A")));
        Assert.Throws<ArgumentException>(() => AutonomousPlaybot.Validate(new PlaybotRunOptions(loopback, "ACC", "PASS", "Bad123")));
        Assert.Throws<ArgumentException>(() => AutonomousPlaybot.Validate(new PlaybotRunOptions(loopback, "ACC", "PASS", "Warrior", Seconds: 0)));
        Assert.Throws<ArgumentException>(() => AutonomousPlaybot.Validate(new PlaybotRunOptions(loopback, "ACC", "PASS", "Warrior", Steps: 501)));

        Assert.Throws<MockProtocolException>(() => AutonomousPlaybot.ReadKnownSpells([]));
        Assert.Throws<MockProtocolException>(() => AutonomousPlaybot.ReadKnownSpells([1, 0, 0, 0, 0]));
        Assert.Throws<MockProtocolException>(() => AutonomousPlaybot.ReadKnownSpells([0, 2, 0, 0, 0]));
        Assert.Throws<MockProtocolException>(() => AutonomousPlaybot.ReadKnownSpells([0, 0, 0, 1, 0]));

        byte[] validSpells = [0, 1, 0, 78, 0, 0, 0, 0, 0];
        Assert.Contains(78u, AutonomousPlaybot.ReadKnownSpells(validSpells));

        // PlaybotCommand.Parse validation
        const string PlaybotEnv = "ARCANE_FUZZ_PLAYBOT_PW";
        Environment.SetEnvironmentVariable(PlaybotEnv, "SECRET123");
        string existingReport = Path.GetTempFileName();
        try
        {
            Assert.Throws<ArgumentException>(() => PlaybotCommand.Parse(["--account"]));
            Assert.Throws<ArgumentException>(() => PlaybotCommand.Parse(["--unknown", "1"]));
            Assert.Throws<ArgumentException>(() => PlaybotCommand.Parse(["--account", "ACC", "--password-env", PlaybotEnv])); // missing --character
            Assert.Throws<ArgumentException>(() => PlaybotCommand.Parse(["--account", "ACC", "--character", "Hero", "--password-env", PlaybotEnv, "--duration-s", "601"]));
            Assert.Throws<ArgumentException>(() => PlaybotCommand.Parse(["--account", "ACC", "--character", "Hero", "--password-env", PlaybotEnv, "--steps", "501"]));
            Assert.Throws<ArgumentException>(() => PlaybotCommand.Parse(["--account", "ACC", "--character", "Hero", "--password-env", PlaybotEnv, "--attack-entry", "bad"]));
            Assert.Throws<ArgumentException>(() => PlaybotCommand.Parse(["--account", "ACC", "--character", "Hero", "--password-env", PlaybotEnv, "--movement", "maybe"]));
            Assert.Throws<ArgumentException>(() => PlaybotCommand.Parse(["--account", "ACC", "--character", "Hero", "--password-env", PlaybotEnv, "--llm", "maybe"]));
            Assert.Throws<ArgumentException>(() => PlaybotCommand.Parse(["--account", "ACC", "--character", "Hero", "--password-env", PlaybotEnv, "--model-endpoint", "not-a-uri"]));
            Assert.Throws<ArgumentException>(() => PlaybotCommand.Parse(["--account", "ACC", "--character", "Hero", "--password-env", PlaybotEnv, "--report", existingReport]));
        }
        finally
        {
            Environment.SetEnvironmentVariable(PlaybotEnv, null);
            File.Delete(existingReport);
        }

        // OllamaPlaybotSelector validation & fallback behavior
        Assert.Throws<ArgumentException>(() => OllamaPlaybotSelector.ValidateEndpoint(new Uri("https://127.0.0.1:11435/")));
        Assert.Throws<ArgumentException>(() => OllamaPlaybotSelector.ValidateEndpoint(new Uri("http://10.0.0.1:11435/")));
        Assert.Throws<ArgumentException>(() => OllamaPlaybotSelector.ValidateEndpoint(new Uri("http://127.0.0.1:11435/v1")));
        Assert.Throws<ArgumentException>(() => OllamaPlaybotSelector.ValidateEndpoint(new Uri("http://127.0.0.1:11435/?q=1")));
        Assert.Throws<ArgumentException>(() => new OllamaPlaybotSelector(new Uri("http://127.0.0.1:11435/"), ""));
        Assert.Throws<ArgumentException>(() => new OllamaPlaybotSelector(new Uri("http://127.0.0.1:11435/"), "bad\nmodel"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OllamaPlaybotSelector(new Uri("http://127.0.0.1:11435/"), "qwen3.5:4b", TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OllamaPlaybotSelector(new Uri("http://127.0.0.1:11435/"), "qwen3.5:4b", TimeSpan.FromSeconds(31)));

        using var fakeHttp = new HttpClient(new StubHttpMessageHandler("{not-json"));
        using var selector = new OllamaPlaybotSelector(new Uri("http://127.0.0.1:11435/"), "qwen3.5:4b", TimeSpan.FromSeconds(2), fakeHttp);
        var context = new PlaybotDecisionContext(1, 100, 100, false, [new PlaybotCandidate("1:observe", PlaybotActionKind.Observe, 0)]);
        PlaybotSelection fallbackSelection = await selector.SelectAsync(context, CancellationToken.None);
        Assert.Equal("deterministic", fallbackSelection.Provider);
        Assert.Equal("provider-error", fallbackSelection.FallbackReason);

        // Hosting guards
        await Assert.ThrowsAsync<ArgumentNullException>(() => ClientFixture.PrepareAsync(null!));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            ClientFixture.PrepareAsync(new ClientFixtureOptions("relative/path", "ACC", "PASS")));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SyntheticQuestContent.GetDefinition((SyntheticQuestProfile)999));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            SyntheticQuestContent.SeedAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            SyntheticArcaneServer.StartAsync(null!, null, CancellationToken.None));

        OwnedFixtureDirectory owned = OwnedFixtureDirectory.Create();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(owned.Path, ".fixture-owner"), "tampered");
            Assert.Throws<IOException>(() => owned.Delete());
        }
        finally
        {
            if (Directory.Exists(owned.Path))
            {
                Directory.Delete(owned.Path, recursive: true);
            }
        }
    }

    /// <summary>
    /// Deterministic seeded fuzz test across all <c>ScenarioWire</c>, <c>ProtocolPackets.RealmList</c>,
    /// <c>StartingZoneLoot</c>, <c>StartingZoneCombat</c>, <c>StartingZoneProbe</c>, <c>AutonomousPlaybot</c>,
    /// and <c>LiveSession</c> binary decoders. Verifies that arbitrary and mutated frames never trigger
    /// unhandled runtime exceptions (such as <c>IndexOutOfRangeException</c> or <c>NullReferenceException</c>).
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public void WireDecoders_DeterministicFuzz_NeverThrowsUnexpectedException(int seed)
    {
        var rng = new Random(seed);
        for (int i = 0; i < 250; i++)
        {
            byte[] payload = new byte[rng.Next(0, 512)];
            rng.NextBytes(payload);

            InvokeWireDecoder(() => ProtocolPackets.RealmList(payload));
            InvokeWireDecoder(() => ScenarioWire.CharacterList(payload));
            InvokeWireDecoder(() => ScenarioWire.LoginLocation(payload));
            InvokeWireDecoder(() => ScenarioWire.SelfCreate(payload));
            InvokeWireDecoder(() => ScenarioWire.FieldUpdates(payload));
            InvokeWireDecoder(() => ScenarioWire.RemovedGuids(payload));
            InvokeWireDecoder(() => ScenarioWire.InflateUpdate(payload));
            InvokeWireDecoder(() => ScenarioWire.QuestgiverStatus(payload));
            InvokeWireDecoder(() => ScenarioWire.QuestDetails(payload));
            InvokeWireDecoder(() => ScenarioWire.QuestOfferReward(payload));
            InvokeWireDecoder(() => ScenarioWire.QuestComplete(payload));
            InvokeWireDecoder(() => ScenarioWire.QuestKill(payload));
            InvokeWireDecoder(() => ScenarioWire.MonsterMove(payload));
            InvokeWireDecoder(() => ScenarioWire.QuestQuery(payload));
            InvokeWireDecoder(() => ScenarioWire.QuestList(payload));
            InvokeWireDecoder(() => ScenarioWire.GossipMessage(payload));
            InvokeWireDecoder(() => ScenarioWire.QuestRequestItems(payload));
            InvokeWireDecoder(() => StartingZoneLoot.ParseWindow(payload));
            InvokeWireDecoder(() => StartingZoneLoot.ParseItemPush(payload));
            InvokeWireDecoder(() => AutonomousPlaybot.ReadKnownSpells(payload));

            _ = ScenarioWire.IsMapTransportsPacket(payload);
            _ = StartingZoneCombat.TryReadAttackerState(payload, out _);
            _ = StartingZoneProbe.IsSelfSpellGo(payload, 1ul, 2457u);
            _ = LiveSession.DecodePlayerLine(payload);
            _ = LiveSession.DecodeSystemLine(payload);
            _ = LiveSession.DecodeNotification(payload);
            _ = LiveSession.IsSuccessfulLogoutResponse(payload);
        }
    }

    /// <summary>
    /// Deterministic seeded fuzz test for <c>ClientHeaderCipher</c>, <c>ClientSrp6.Interleave</c>,
    /// and credential/CLI parsers.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public void CipherSrpAndCliParsers_DeterministicFuzz_PreservesRoundTripAndBounds(int seed)
    {
        var rng = new Random(seed);
        byte[] sessionKey = new byte[40];
        rng.NextBytes(sessionKey);
        var sender = new ClientHeaderCipher(sessionKey);
        var receiver = new ClientHeaderCipher(sessionKey);

        for (int i = 0; i < 120; i++)
        {
            byte[] originalHeader = new byte[rng.Next(1, 16)];
            rng.NextBytes(originalHeader);
            byte[] wire = (byte[])originalHeader.Clone();
            sender.Encrypt(wire);
            receiver.Decrypt(wire);
            Assert.Equal(originalHeader, wire);

            byte[] secret = new byte[32];
            rng.NextBytes(secret);
            if (i % 5 == 0)
            {
                int zeroPrefix = rng.Next(1, 32);
                Array.Clear(secret, 0, zeroPrefix);
            }

            byte[] interleaved = ClientSrp6.Interleave(secret);
            Assert.Equal(40, interleaved.Length);

            char[] chars = new char[rng.Next(0, 24)];
            for (int c = 0; c < chars.Length; c++)
            {
                chars[c] = (char)rng.Next(0, 140);
            }

            string candidate = new(chars);
            bool validAscii = candidate.Length is >= 1 and <= 16 && candidate.All(ch => ch is >= ' ' and <= '~');
            if (validAscii)
            {
                Assert.Equal(candidate.ToUpperInvariant(), ProtocolPackets.NormalizeAccount(candidate));
            }
            else
            {
                Assert.Throws<ArgumentException>(() => ProtocolPackets.NormalizeAccount(candidate));
            }
        }

        sender.Clear();
        receiver.Clear();
    }

    private static void InvokeWireDecoder(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is MockProtocolException or DecoderFallbackException or InvalidDataException or EndOfStreamException)
        {
            // Expected domain rejection for malformed or random fuzz bytes.
        }
    }

    private static async Task WithLoopbackPeerAsync(Func<NetworkStream, Task> serverAction, Func<IPEndPoint, Task> clientAction)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        Task peerTask = Task.Run(async () =>
        {
            using TcpClient peer = await listener.AcceptTcpClientAsync();
            peer.NoDelay = true;
            await using NetworkStream stream = peer.GetStream();
            try
            {
                await serverAction(stream);
            }
            catch (IOException)
            {
                // Client may close early after rejecting a malformed frame.
            }
            catch (SocketException)
            {
            }
        });

        try
        {
            await clientAction(endpoint);
        }
        finally
        {
            listener.Stop();
            await peerTask;
        }
    }

    private sealed class StubHttpMessageHandler(string responseBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            });
    }
}
