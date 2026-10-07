using System.Buffers.Binary;
using System.IO.Compression;
using ArcaneCore.Kernel;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using Xunit;

namespace ArcaneCore.World.Tests.Net;

/// <summary>
/// Deterministic fuzz harness over the packet readers (docs/ops/netguard.md): the real client
/// packet shapes the end-to-end tests send (CMSG_AUTH_SESSION with an addon block, every
/// MSG_MOVE_* flag combination, CMSG_PING, CMSG_MOVE_TIME_SKIPPED, CMSG_CHAR_CREATE, packed
/// GUIDs) are mutated with a seeded <see cref="Random"/> (truncation, bit flips, extreme length
/// bytes, insertions, deletions, junk tails, lost terminators, noise) and fed to each reader.
/// <para>
/// What is proven, per mutation: the throwing surface throws nothing but the controlled
/// <see cref="ArgumentOutOfRangeException"/> (<see cref="MalformedPacket"/>); the Try surface never
/// throws and fails exactly when the throwing surface does, leaving the cursor in place; and the
/// cursor never passes the end of the buffer. A failure prints the seed, the iteration and the
/// bytes, so it replays.
/// </para>
/// </summary>
public sealed class PacketFuzzTests
{
    private const int Iterations = 3_000;

    public static TheoryData<int> Seeds => [1, 42, 20251004];

    // --- shapes (the layouts the handshake, movement and character tests send) -------------

    private static byte[] AuthSessionShape()
    {
        var writer = new PacketWriter(128);
        writer.WriteUInt32(ClientBuild.Vanilla1121);
        writer.WriteUInt32(0);
        writer.WriteCString("TESTER");
        writer.WriteUInt32(0x1BADD00D);
        writer.WriteBytes(new byte[20]);
        writer.WriteBytes(AddonBlock(AddonRecords(["Blizzard_AuthChallenge", "Blizzard_CombatText", "MyAddon"])));
        return writer.ToArray();
    }

    private static byte[] AddonRecords(IEnumerable<string> names, uint modulusCrc = 0x4C1C776D)
    {
        var inner = new PacketWriter(64);
        foreach (string name in names)
        {
            inner.WriteCString(name);
            inner.WriteByte(1);
            inner.WriteUInt32(modulusCrc);
            inner.WriteUInt32(0);
        }

        return inner.ToArray();
    }

    private static byte[] AddonBlock(byte[] uncompressed)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(uncompressed);
        }

        byte[] body = compressed.ToArray();
        byte[] block = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(block, (uint)uncompressed.Length);
        body.CopyTo(block, 4);
        return block;
    }

    private static IEnumerable<byte[]> MovementShapes()
    {
        MovementFlags[] optional = [MovementFlags.OnTransport, MovementFlags.Swimming, MovementFlags.Jumping, MovementFlags.SplineElevation];
        for (int mask = 0; mask < 16; mask++)
        {
            var info = new MovementInfo
            {
                Flags = MovementFlags.Forward, Time = 1234, X = -8949.95f, Y = -132.493f, Z = 83.5312f, Orientation = 1.5f,
                TransportGuid = 0xF13000000000001A, TransportX = 1, TransportY = 2, TransportZ = 3, TransportOrientation = 4,
                Pitch = 0.25f, FallTime = 100, JumpZSpeed = -7.95f, JumpCosAngle = 1, JumpSinAngle = 0, JumpXySpeed = 7, SplineElevation = 2,
            };
            for (int bit = 0; bit < optional.Length; bit++)
            {
                if ((mask & (1 << bit)) != 0)
                {
                    info.Flags |= optional[bit];
                }
            }

            var writer = new PacketWriter(64);
            info.Write(writer);
            yield return writer.ToArray();
        }
    }

    private static byte[] CharCreateShape()
    {
        var create = new PacketWriter(32);
        create.WriteCString("Tester");
        for (int i = 0; i < 9; i++)
        {
            create.WriteByte((byte)(i == 0 ? 1 : 0));
        }

        return create.ToArray();
    }

    private static byte[] PingShape()
    {
        byte[] ping = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(ping, 7);
        BinaryPrimitives.WriteUInt32LittleEndian(ping.AsSpan(4), 60);
        return ping;
    }

    private static byte[] TimeSkippedShape()
    {
        byte[] body = new byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(body, 0x0000000000000042);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), 1);
        return body;
    }

    private static byte[] PackedGuidShape()
    {
        var writer = new PacketWriter(16);
        writer.WritePackedGuid(0xF13000000000001A);
        writer.WritePackedGuid(0x42);
        writer.WriteUInt32(5);
        return writer.ToArray();
    }

    private static IEnumerable<byte[]> AllShapes()
    {
        yield return AuthSessionShape();
        foreach (byte[] movement in MovementShapes())
        {
            yield return movement;
        }

        yield return CharCreateShape();
        yield return PingShape();
        yield return TimeSkippedShape();
        yield return PackedGuidShape();
    }

    // --- the harness ---------------------------------------------------------------------

    private static string Describe(int seed, int iteration, byte[] data, Exception ex)
        => $"seed {seed} iteration {iteration}: {ex.GetType().Name}: {ex.Message}\n{Convert.ToHexString(data)}";

    /// <summary>Run a throwing reader; anything but the controlled outcome fails the test.</summary>
    private static bool Controlled(int seed, int iteration, byte[] data, Action<byte[]> read)
    {
        try
        {
            read(data);
            return true;
        }
        catch (ArgumentOutOfRangeException ex)
        {
            Assert.Equal(MalformedPacket.ParamName, ex.ParamName);
            return false;
        }
        catch (Exception ex)
        {
            Assert.Fail(Describe(seed, iteration, data, ex));
            return false;
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void MovementReader_ThrowsOnlyTheControlledOutcome_AndTheTrySurfaceAgrees(int seed)
    {
        var random = new Random(seed);
        byte[][] shapes = [.. MovementShapes()];
        for (int i = 0; i < Iterations; i++)
        {
            byte[] data = PacketMutator.Mutate(shapes[random.Next(shapes.Length)], random);
            int endPosition = -1;
            MovementInfo fromRead = default;
            bool readOk = Controlled(seed, i, data, d =>
            {
                var reader = new PacketReader(d);
                fromRead = MovementInfo.Read(ref reader);
                endPosition = reader.Position;
            });

            var tryReader = new PacketReader(data);
            bool tryOk;
            try
            {
                tryOk = MovementInfo.TryRead(ref tryReader, out MovementInfo fromTry);
                if (tryOk)
                {
                    Assert.Equal(fromRead.Flags, fromTry.Flags);
                    Assert.Equal(fromRead.FallTime, fromTry.FallTime);
                    Assert.Equal(endPosition, tryReader.Position);
                }
                else
                {
                    Assert.Equal(0, tryReader.Position);
                }
            }
            catch (Exception ex)
            {
                Assert.Fail(Describe(seed, i, data, ex));
                return;
            }

            Assert.Equal(readOk, tryOk);
            Assert.True(tryReader.Position <= data.Length);
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void AuthSessionReader_NeverThrows_AndItsWindowsLieInsideThePayload(int seed)
    {
        var random = new Random(seed);
        byte[] shape = AuthSessionShape();
        int parsed = 0;
        for (int i = 0; i < Iterations; i++)
        {
            byte[] data = PacketMutator.Mutate(shape, random);
            try
            {
                if (AuthSessionRequest.TryParse(data, out AuthSessionRequest request))
                {
                    parsed++;
                    Assert.Equal(AuthSessionRequest.DigestLength, request.ClientDigest.Length);
                    Assert.True(request.Account.Length <= AuthSessionRequest.MaxAccountNameBytes);
                    Assert.True(request.AddonBlock.Length >= 0 && request.AddonBlock.Length <= data.Length);
                    Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(request.AddonBlock, out ArraySegment<byte> segment) && ReferenceEquals(segment.Array, data), "the addon block must be a window over the payload");
                    _ = AddonInfo.BuildResponse(request.AddonBlock.Span); // the next reader in the chain never throws either
                }
            }
            catch (Exception ex)
            {
                Assert.Fail(Describe(seed, i, data, ex));
            }
        }

        Assert.True(parsed > 0, "the mutations must leave some packets intact, or the harness proves nothing");
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void AddonBlockReader_NeverThrows_AndNeverBuildsAnOversizedResponse(int seed)
    {
        var random = new Random(seed);
        string[] names = [.. Enumerable.Range(0, 300).Select(n => n % 3 == 0 ? $"Blizzard_{n}" : $"Addon{n}")];
        byte[] records = AddonRecords(names);
        for (int i = 0; i < Iterations / 3; i++)
        {
            // Mutate the record list (so zlib stays valid) and, separately, the compressed block itself.
            byte[] data = random.Next(2) == 0
                ? AddonBlock(PacketMutator.Mutate(records, random))
                : PacketMutator.Mutate(AddonBlock(records), random);
            try
            {
                byte[] response = AddonInfo.BuildResponse(data);
                Assert.True(response.Length <= AddonInfo.MaxResponseBytes);
            }
            catch (Exception ex)
            {
                Assert.Fail(Describe(seed, i, data, ex));
            }
        }
    }

    [Fact]
    public void AddonBlock_WithTensOfThousandsOfRecords_IsCappedToOneFrame()
    {
        // A wrong modulus CRC makes every Blizzard record carry the 256-byte key: the size cap ends the list.
        byte[] keyed = AddonRecords(Enumerable.Range(0, 30_000).Select(n => "Blizzard_x"), modulusCrc: 0);
        byte[] response = AddonInfo.BuildResponse(AddonBlock(keyed));
        Assert.True(response.Length <= AddonInfo.MaxResponseBytes, $"response {response.Length} bytes");
        Assert.True(response.Length > 60_000, $"the size cap must be reached, not an early stop ({response.Length} bytes)");

        // Small records (8 bytes each) run into the record cap instead: exactly MaxAddons of them are answered.
        byte[] small = AddonRecords(Enumerable.Range(0, 30_000).Select(n => "Blizzard_x"));
        Assert.Equal(AddonInfo.MaxAddons * 8, AddonInfo.BuildResponse(AddonBlock(small)).Length);

        byte[] longName = AddonRecords([new string('A', AddonInfo.MaxAddonNameBytes + 1)]);
        Assert.Empty(AddonInfo.BuildResponse(AddonBlock(longName))); // an oversized name ends the list before any record
    }

    /// <summary>The reader primitives themselves, driven by a random program over a random buffer: both surfaces agree and the cursor is bounded.</summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void ReaderPrimitives_ThrowingAndTrySurfaces_Agree_AndNeverPassTheEnd(int seed)
    {
        var random = new Random(seed);
        byte[][] shapes = [.. AllShapes()];
        for (int i = 0; i < Iterations; i++)
        {
            byte[] data = PacketMutator.Mutate(shapes[random.Next(shapes.Length)], random);
            var throwing = new PacketReader(data);
            var trying = new PacketReader(data);
            for (int step = 0; step < 12; step++)
            {
                int op = random.Next(10);
                int count = random.Next(-1, 24);
                int before = trying.Position;
                bool tryOk;
                ulong readValue = 0;
                bool readOk;
                try
                {
                    readValue = Apply(op, count, ref throwing);
                    readOk = true;
                }
                catch (ArgumentOutOfRangeException ex)
                {
                    Assert.Equal(MalformedPacket.ParamName, ex.ParamName);
                    readOk = false;
                }
                catch (Exception ex)
                {
                    Assert.Fail(Describe(seed, i, data, ex) + $" (op {op}, count {count}, step {step})");
                    return;
                }

                try
                {
                    tryOk = TryApply(op, count, ref trying, out ulong value);
                    if (tryOk && readOk)
                    {
                        Assert.Equal(readValue, value);
                    }
                }
                catch (Exception ex)
                {
                    Assert.Fail(Describe(seed, i, data, ex) + $" (op {op}, count {count}, step {step})");
                    return;
                }

                Assert.True(readOk == tryOk, $"seed {seed} iteration {i} step {step} op {op} count {count}: Read {(readOk ? "ok" : "threw")} but Try {(tryOk ? "ok" : "failed")}");
                Assert.True(trying.Position <= data.Length && throwing.Position <= data.Length);
                if (!tryOk)
                {
                    Assert.Equal(before, trying.Position);
                    break; // the throwing reader's cursor is unspecified after an exception; stop comparing
                }

                Assert.Equal(throwing.Position, trying.Position);
            }
        }
    }

    private static ulong Apply(int op, int count, ref PacketReader reader) => op switch
    {
        0 => reader.ReadByte(),
        1 => reader.ReadUInt16(),
        2 => reader.ReadUInt32(),
        3 => reader.ReadUInt64(),
        4 => BitConverter.SingleToUInt32Bits(reader.ReadSingle()),
        5 => reader.ReadPackedGuid(),
        6 => (ulong)reader.ReadBytes(count).Length,
        7 => Skip(ref reader, count),
        8 => (ulong)reader.ReadCount(Math.Max(0, count) * 1000),
        _ => (ulong)reader.ReadCStringBytes(Math.Max(0, count)).Length,
    };

    private static ulong Skip(ref PacketReader reader, int count)
    {
        reader.Skip(count);
        return 0;
    }

    private static bool TryApply(int op, int count, ref PacketReader reader, out ulong value)
    {
        bool ok;
        switch (op)
        {
            case 0:
                ok = reader.TryReadByte(out byte b);
                value = b;
                return ok;
            case 1:
                ok = reader.TryReadUInt16(out ushort u16);
                value = u16;
                return ok;
            case 2:
                ok = reader.TryReadUInt32(out uint u32);
                value = u32;
                return ok;
            case 3:
                return reader.TryReadUInt64(out value);
            case 4:
                ok = reader.TryReadSingle(out float f);
                value = BitConverter.SingleToUInt32Bits(f);
                return ok;
            case 5:
                return reader.TryReadPackedGuid(out value);
            case 6:
                ok = reader.TryReadBytes(count, out ReadOnlySpan<byte> bytes);
                value = (ulong)bytes.Length;
                return ok;
            case 7:
                value = 0;
                return reader.TrySkip(count);
            case 8:
                ok = reader.TryReadCount(Math.Max(0, count) * 1000, out int n);
                value = (ulong)n;
                return ok;
            default:
                ok = reader.TryReadCStringBytes(Math.Max(0, count), out ReadOnlySpan<byte> text);
                value = (ulong)text.Length;
                return ok;
        }
    }
}

/// <summary>One random mutation of a packet shape (the same menu the realm-side fuzz uses).</summary>
internal static class PacketMutator
{
    public static byte[] Mutate(byte[] shape, Random random)
    {
        byte[] data = (byte[])shape.Clone();
        switch (random.Next(9))
        {
            case 0: // truncate
                return data[..random.Next(data.Length + 1)];
            case 1: // flip bits in a few bytes
                for (int n = random.Next(1, 4); n > 0 && data.Length > 0; n--)
                {
                    data[random.Next(data.Length)] ^= (byte)(1 << random.Next(8));
                }

                return data;
            case 2: // an extreme length, count, flag or mask byte
                if (data.Length > 0)
                {
                    data[random.Next(data.Length)] = random.Next(2) == 0 ? (byte)0xFF : (byte)0;
                }

                return data;
            case 3: // append junk
                {
                    byte[] junk = new byte[random.Next(1, 64)];
                    random.NextBytes(junk);
                    return [.. data, .. junk];
                }

            case 4: // delete a run
                if (data.Length > 1)
                {
                    int at = random.Next(data.Length);
                    int count = random.Next(1, Math.Min(8, data.Length - at) + 1);
                    return [.. data[..at], .. data[(at + count)..]];
                }

                return data;
            case 5: // insert a run
                {
                    int at = random.Next(data.Length + 1);
                    byte[] run = new byte[random.Next(1, 8)];
                    random.NextBytes(run);
                    return [.. data[..at], .. run, .. data[at..]];
                }

            case 6: // remove every null terminator
                for (int k = 0; k < data.Length; k++)
                {
                    if (data[k] == 0)
                    {
                        data[k] = 0x41;
                    }
                }

                return data;
            case 7: // set a 32-bit field to an extreme
                if (data.Length >= 4)
                {
                    int at = random.Next(data.Length - 3);
                    uint extreme = random.Next(3) switch { 0 => uint.MaxValue, 1 => 0x80000000, _ => 0 };
                    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at), extreme);
                }

                return data;
            default: // random bytes of a random length around the shape's
                {
                    byte[] noise = new byte[random.Next(0, data.Length * 2 + 1)];
                    random.NextBytes(noise);
                    return noise;
                }
        }
    }
}
