using System.Buffers.Binary;
using System.Text;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.Protocol.Versioning;
using ArcaneCore.Sniff;
using Xunit;

namespace ArcaneCore.World.Tests.Fuzz;

/// <summary>
/// Exception-path and deterministic seeded fuzz tests for <c>ArcaneCore.Protocol</c> and <c>ArcaneCore.Sniff</c>.
/// </summary>
public sealed class ProtocolAndSniffFuzzAndExceptionTests
{
    [Fact]
    public void MalformedPacket_ThrowAndIs_MatchControlledContract()
    {
        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(() => MalformedPacket.Throw("truncated test field"));
        Assert.Equal(MalformedPacket.ParamName, ex.ParamName);
        Assert.Contains("malformed packet: truncated test field", ex.Message, StringComparison.Ordinal);
        Assert.True(MalformedPacket.Is(ex));
        Assert.False(MalformedPacket.Is(new ArgumentException("other")));
        Assert.False(MalformedPacket.Is(new InvalidDataException("other")));
    }

    [Fact]
    public void PacketReader_ThrowingPrimitives_ThrowMalformedPacketOnTruncatedInput()
    {
        var empty = new PacketReader([]);
        AssertMalformed( static () =>
        {
            var r = new PacketReader([]);
            _ = r.ReadByte();
        });
        Assert.False(empty.TryReadByte(out byte b));
        Assert.Equal(0, b);
        Assert.Equal(0, empty.Position);

        for (int len = 0; len < 2; len++)
        {
            byte[] buf = new byte[len];
            AssertMalformed(() =>
            {
                var r = new PacketReader(buf);
                _ = r.ReadUInt16();
            });
            var reader = new PacketReader(buf);
            Assert.False(reader.TryReadUInt16(out ushort u16));
            Assert.Equal((ushort)0, u16);
            Assert.Equal(0, reader.Position);
        }

        for (int len = 0; len < 4; len++)
        {
            byte[] buf = new byte[len];
            AssertMalformed(() =>
            {
                var r = new PacketReader(buf);
                _ = r.ReadUInt32();
            });
            AssertMalformed(() =>
            {
                var r = new PacketReader(buf);
                _ = r.ReadInt32();
            });
            AssertMalformed(() =>
            {
                var r = new PacketReader(buf);
                _ = r.ReadSingle();
            });

            var reader = new PacketReader(buf);
            Assert.False(reader.TryReadUInt32(out uint u32));
            Assert.Equal(0u, u32);
            Assert.False(reader.TryReadInt32(out int i32));
            Assert.Equal(0, i32);
            Assert.False(reader.TryReadSingle(out float f32));
            Assert.Equal(0f, f32);
            Assert.Equal(0, reader.Position);
        }

        for (int len = 0; len < 8; len++)
        {
            byte[] buf = new byte[len];
            AssertMalformed(() =>
            {
                var r = new PacketReader(buf);
                _ = r.ReadUInt64();
            });
            var reader = new PacketReader(buf);
            Assert.False(reader.TryReadUInt64(out ulong u64));
            Assert.Equal(0UL, u64);
            Assert.Equal(0, reader.Position);
        }
    }

    [Fact]
    public void PacketReader_ReadFiniteSingle_ThrowsOnTruncatedNaNAndInfinity_AndLeavesCursorUnchanged()
    {
        for (int len = 0; len < 4; len++)
        {
            byte[] shortBuf = new byte[len];
            AssertMalformed(() =>
            {
                var r = new PacketReader(shortBuf);
                _ = r.ReadFiniteSingle();
            });
            var reader = new PacketReader(shortBuf);
            Assert.False(reader.TryReadFiniteSingle(out float v));
            Assert.Equal(0f, v);
            Assert.Equal(0, reader.Position);
        }

        float[] nonFinite = [float.NaN, float.PositiveInfinity, float.NegativeInfinity, BitConverter.UInt32BitsToSingle(0xFFFFFFFFu)];
        foreach (float bad in nonFinite)
        {
            byte[] buf = new byte[6];
            buf[0] = 0xAA;
            BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(1, 4), bad);
            buf[5] = 0xBB;

            AssertMalformed(() =>
            {
                var r = new PacketReader(buf);
                _ = r.ReadByte();
                _ = r.ReadFiniteSingle();
            });

            var reader = new PacketReader(buf);
            Assert.Equal(0xAA, reader.ReadByte());
            Assert.False(reader.TryReadFiniteSingle(out float val));
            Assert.Equal(0f, val);
            Assert.Equal(1, reader.Position);
        }

        byte[] validBuf = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(validBuf, -123.5f);
        var okReader = new PacketReader(validBuf);
        Assert.Equal(-123.5f, okReader.ReadFiniteSingle());
        Assert.Equal(4, okReader.Position);
    }

    [Fact]
    public void PacketReader_TryReadFiniteCoordinates_RejectsTruncatedNonFiniteAndOutOfBounds_AndPreservesCursor()
    {
        for (int len = 0; len < 12; len++)
        {
            byte[] shortBuf = new byte[len];
            var r = new PacketReader(shortBuf);
            Assert.False(r.TryReadFiniteCoordinates(out float x, out float y, out float z));
            Assert.Equal(0f, x);
            Assert.Equal(0f, y);
            Assert.Equal(0f, z);
            Assert.Equal(0, r.Position);
        }

        (float X, float Y, float Z)[] invalidCoords =
        [
            (float.NaN, 0f, 0f),
            (0f, float.PositiveInfinity, 0f),
            (0f, 0f, float.NegativeInfinity),
            (64_000.01f, 0f, 0f),
            (0f, -64_000.01f, 0f),
            (0f, 0f, 64_001f),
        ];

        foreach ((float cx, float cy, float cz) in invalidCoords)
        {
            byte[] buf = new byte[14];
            buf[0] = 0x55;
            BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(1, 4), cx);
            BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(5, 4), cy);
            BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(9, 4), cz);

            var r = new PacketReader(buf);
            Assert.Equal(0x55, r.ReadByte());
            Assert.False(r.TryReadFiniteCoordinates(out float ox, out float oy, out float oz));
            Assert.Equal(0f, ox);
            Assert.Equal(0f, oy);
            Assert.Equal(0f, oz);
            Assert.Equal(1, r.Position);
        }

        byte[] valid = new byte[12];
        BinaryPrimitives.WriteSingleLittleEndian(valid.AsSpan(0, 4), -64_000f);
        BinaryPrimitives.WriteSingleLittleEndian(valid.AsSpan(4, 4), 0f);
        BinaryPrimitives.WriteSingleLittleEndian(valid.AsSpan(8, 4), 64_000f);
        var ok = new PacketReader(valid);
        Assert.True(ok.TryReadFiniteCoordinates(out float vx, out float vy, out float vz));
        Assert.Equal(-64_000f, vx);
        Assert.Equal(0f, vy);
        Assert.Equal(64_000f, vz);
        Assert.Equal(12, ok.Position);
    }

    [Fact]
    public void PacketReader_PackedGuid_ThrowsOrReturnsFalseWhenTruncated_AndPreservesCursor()
    {
        AssertMalformed(static () =>
        {
            var r = new PacketReader([]);
            _ = r.ReadPackedGuid();
        });

        var empty = new PacketReader([]);
        Assert.False(empty.TryReadPackedGuid(out ulong emptyGuid));
        Assert.Equal(0UL, emptyGuid);

        byte[] masks = [0x01, 0x03, 0x07, 0x0F, 0x1F, 0x3F, 0x7F, 0xFF];
        for (int i = 0; i < masks.Length; i++)
        {
            int neededBytes = i + 1;
            byte[] buf = new byte[1 + neededBytes];
            buf[0] = 0x99;
            buf[1] = masks[i];
            for (int j = 0; j < neededBytes - 1; j++)
            {
                buf[2 + j] = (byte)(j + 1);
            }

            AssertMalformed(() =>
            {
                var r = new PacketReader(buf);
                _ = r.ReadByte();
                _ = r.ReadPackedGuid();
            });

            var reader = new PacketReader(buf);
            Assert.Equal(0x99, reader.ReadByte());
            Assert.False(reader.TryReadPackedGuid(out ulong g));
            Assert.Equal(0UL, g);
            Assert.Equal(1, reader.Position);
        }
    }

    [Fact]
    public void PacketReader_ReadBytesAndSkip_RejectNegativeAndPastEndCounts()
    {
        byte[] data = [1, 2, 3, 4];
        int[] badCounts = [-1, int.MinValue, 5, 100];

        foreach (int bad in badCounts)
        {
            AssertMalformed(() =>
            {
                var r = new PacketReader(data);
                _ = r.ReadBytes(bad);
            });
            AssertMalformed(() =>
            {
                var r = new PacketReader(data);
                r.Skip(bad);
            });

            var reader = new PacketReader(data);
            Assert.False(reader.TryReadBytes(bad, out ReadOnlySpan<byte> slice));
            Assert.True(slice.IsEmpty);
            Assert.False(reader.TrySkip(bad));
            Assert.Equal(0, reader.Position);
        }

        var ok = new PacketReader(data);
        Assert.True(ok.TryReadBytes(0, out ReadOnlySpan<byte> zeroSlice));
        Assert.Equal(0, zeroSlice.Length);
        Assert.True(ok.TrySkip(2));
        Assert.Equal(2, ok.Position);
        Assert.Equal([3, 4], ok.ReadToEnd().ToArray());
        Assert.Equal(0, ok.Remaining);
    }

    [Fact]
    public void PacketReader_ReadCountAndTryReadByteCount_EnforceBoundsAndPreserveCursor()
    {
        byte[] shortData = [1, 2, 3];
        AssertMalformed(() =>
        {
            var r = new PacketReader(shortData);
            _ = r.ReadCount(10);
        });

        byte[] fourBytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(fourBytes, 11u);
        AssertMalformed(() =>
        {
            var r = new PacketReader(fourBytes);
            _ = r.ReadCount(10);
        });
        AssertMalformed(() =>
        {
            var r = new PacketReader(fourBytes);
            _ = r.ReadCount(-1);
        });

        var countReader = new PacketReader(fourBytes);
        Assert.False(countReader.TryReadCount(-1, out int cNeg));
        Assert.Equal(0, cNeg);
        Assert.False(countReader.TryReadCount(10, out int cOver));
        Assert.Equal(0, cOver);
        Assert.Equal(0, countReader.Position);
        Assert.Equal(11, countReader.ReadCount(11));
        Assert.Equal(4, countReader.Position);

        byte[] byteCountData = [25];
        var byteReader = new PacketReader(byteCountData);
        Assert.False(byteReader.TryReadByteCount(-1, out int bcNeg));
        Assert.Equal(0, bcNeg);
        Assert.False(byteReader.TryReadByteCount(24, out int bcOver));
        Assert.Equal(0, bcOver);
        Assert.Equal(0, byteReader.Position);
        Assert.True(byteReader.TryReadByteCount(25, out int bcOk));
        Assert.Equal(25, bcOk);
        Assert.Equal(1, byteReader.Position);
        Assert.False(byteReader.TryReadByteCount(25, out _));
    }

    [Fact]
    public void PacketReader_CStringAndCStringBytes_EnforceBoundsAndHandleInvalidUtf8AndMissingTerminator()
    {
        byte[] terminated = "hello\0world"u8.ToArray();
        AssertMalformed(() =>
        {
            var r = new PacketReader(terminated);
            _ = r.ReadCString(4);
        });
        AssertMalformed(() =>
        {
            var r = new PacketReader(terminated);
            _ = r.ReadCString(-1);
        });
        AssertMalformed(() =>
        {
            var r = new PacketReader(terminated);
            _ = r.ReadCStringBytes(4);
        });
        AssertMalformed(() =>
        {
            var r = new PacketReader(terminated);
            _ = r.ReadCStringBytes(-1);
        });

        var tryReader = new PacketReader(terminated);
        Assert.False(tryReader.TryReadCString(4, out string failedStr));
        Assert.Equal(string.Empty, failedStr);
        Assert.Equal(0, tryReader.Position);
        Assert.Equal("hello", tryReader.ReadCString(5));
        Assert.Equal(6, tryReader.Position);
        Assert.Equal("world", tryReader.ReadCString(5));
        Assert.Equal(11, tryReader.Position);

        byte[] oversizeDefault = new byte[PacketReader.MaxCStringBytes + 1];
        Array.Fill(oversizeDefault, (byte)'a');
        AssertMalformed(() =>
        {
            var r = new PacketReader(oversizeDefault);
            _ = r.ReadCString();
        });
        AssertMalformed(() =>
        {
            var r = new PacketReader(oversizeDefault);
            _ = r.ReadCStringBytes();
        });

        byte[] invalidUtf8 = [0xFF, 0xFE, 0x00, 0x00];
        var utf8Reader = new PacketReader(invalidUtf8);
        string replaced = utf8Reader.ReadCString(4);
        Assert.Contains('\uFFFD', replaced);
        Assert.Equal(3, utf8Reader.Position);
        Assert.Equal(string.Empty, utf8Reader.ReadCString(4));
        Assert.Equal(4, utf8Reader.Position);
    }

    [Fact]
    public void PacketWriter_ExceptionsInvariantsAndBufferGrowth()
    {
        var writer = new PacketWriter(0);
        Assert.Throws<ArgumentNullException>(() => writer.WriteCString(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.PatchUInt32(-1, 1u));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.PatchUInt32(0, 1u));

        writer.WriteByte(0x11);
        writer.WriteUInt16(0x2233);
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.PatchUInt32(0, 1u));

        writer.WriteUInt32(0x44556677u);
        writer.WriteInt32(-42);
        writer.WriteUInt64(0x0102030405060708UL);
        writer.WriteSingle(3.5f);
        writer.WritePackedGuid(0UL);
        writer.WritePackedGuid(0x8877665544332211UL);
        writer.WriteBytes([0xAA, 0xBB, 0xCC]);
        writer.WriteCString("Arcane");

        int endPatchPos = writer.Length - 4;
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.PatchUInt32(endPatchPos + 1, 99u));
        writer.PatchUInt32(3, 0xCAFEBABEu);

        using (InvariantCapture capture = Invariant.Capture())
        {
            writer.WriteFiniteSingle(float.NaN);
            writer.WriteFiniteSingle(float.PositiveInfinity);
            writer.WriteFiniteSingle(float.NegativeInfinity);
            Assert.Equal(3, capture.Failures.Count);
        }

        ReadOnlyMemory<byte> memory = writer.AsMemory();
        Assert.Equal(writer.Length, memory.Length);
        byte[] copy = writer.ToArray();
        Assert.Equal(copy, writer.AsSpan().ToArray());

        var reader = new PacketReader(copy);
        Assert.Equal(0x11, reader.ReadByte());
        Assert.Equal((ushort)0x2233, reader.ReadUInt16());
        Assert.Equal(0xCAFEBABEu, reader.ReadUInt32());
        Assert.Equal(-42, reader.ReadInt32());
        Assert.Equal(0x0102030405060708UL, reader.ReadUInt64());
        Assert.Equal(3.5f, reader.ReadSingle());
        Assert.Equal(0UL, reader.ReadPackedGuid());
        Assert.Equal(0x8877665544332211UL, reader.ReadPackedGuid());
        Assert.Equal([0xAA, 0xBB, 0xCC], reader.ReadBytes(3).ToArray());
        Assert.Equal("Arcane", reader.ReadCString());
        Assert.Equal(0f, reader.ReadFiniteSingle());
        Assert.Equal(0f, reader.ReadFiniteSingle());
        Assert.Equal(0f, reader.ReadFiniteSingle());
        Assert.Equal(0, reader.Remaining);

        writer.Reset();
        Assert.Equal(0, writer.Length);
    }

    [Fact]
    public void MovementInfo_CorrectData_RemovesIllegalFlagCombinations()
    {
        MovementFlags[] movingFlags =
        [
            MovementFlags.Forward,
            MovementFlags.Backward,
            MovementFlags.StrafeLeft,
            MovementFlags.StrafeRight,
            MovementFlags.PitchUp,
            MovementFlags.PitchDown,
            MovementFlags.Jumping,
            MovementFlags.FallingFar,
            MovementFlags.SplineElevation,
        ];

        foreach (MovementFlags move in movingFlags)
        {
            var info = new MovementInfo { Flags = MovementFlags.Root | move | MovementFlags.WalkMode };
            info.CorrectData();
            Assert.False(info.HasFlag(MovementFlags.Root));
            Assert.True(info.HasFlag(move));
            Assert.True(info.HasFlag(MovementFlags.WalkMode));
        }

        var stationaryRoot = new MovementInfo { Flags = MovementFlags.Root | MovementFlags.TurnLeft | MovementFlags.Hover };
        stationaryRoot.CorrectData();
        Assert.True(stationaryRoot.HasFlag(MovementFlags.Root));
        Assert.True(stationaryRoot.HasFlag(MovementFlags.TurnLeft));
        Assert.True(stationaryRoot.HasFlag(MovementFlags.Hover));

        var opposing = new MovementInfo
        {
            Flags = MovementFlags.TurnLeft | MovementFlags.TurnRight
                | MovementFlags.StrafeLeft | MovementFlags.StrafeRight
                | MovementFlags.PitchUp | MovementFlags.PitchDown
                | MovementFlags.Forward | MovementFlags.Backward
                | MovementFlags.Levitating,
        };
        opposing.CorrectData();
        Assert.Equal(MovementFlags.Levitating, opposing.Flags);
    }

    [Fact]
    public void MovementInfo_ReadAndTryRead_RejectTruncationsAtEverySection_AndPreserveCursor()
    {
        MovementFlags[] flagCombos =
        [
            MovementFlags.None,
            MovementFlags.OnTransport,
            MovementFlags.Swimming,
            MovementFlags.Jumping,
            MovementFlags.SplineElevation,
            MovementFlags.OnTransport | MovementFlags.Swimming | MovementFlags.Jumping | MovementFlags.SplineElevation,
        ];

        foreach (MovementFlags flags in flagCombos)
        {
            var original = new MovementInfo
            {
                Flags = flags,
                Time = 1000u,
                X = 10f,
                Y = 20f,
                Z = 30f,
                Orientation = 1.5f,
                TransportGuid = 0x1122334455667788UL,
                TransportX = 1f,
                TransportY = 2f,
                TransportZ = 3f,
                TransportOrientation = 0.5f,
                Pitch = -0.25f,
                FallTime = 250u,
                JumpZSpeed = 7.5f,
                JumpCosAngle = 0.6f,
                JumpSinAngle = 0.8f,
                JumpXySpeed = 7.0f,
                SplineElevation = 4.25f,
            };

            var writer = new PacketWriter();
            writer.WriteByte(0xFE);
            original.Write(writer);
            byte[] full = writer.ToArray();

            for (int truncatedLen = 1; truncatedLen < full.Length; truncatedLen++)
            {
                byte[] slice = full[..truncatedLen];
                var tryReader = new PacketReader(slice);
                Assert.Equal(0xFE, tryReader.ReadByte());
                Assert.False(MovementInfo.TryRead(ref tryReader, out MovementInfo failed));
                Assert.Equal(default, failed);
                Assert.Equal(1, tryReader.Position);

                AssertMalformed(() =>
                {
                    var r = new PacketReader(slice);
                    _ = r.ReadByte();
                    _ = MovementInfo.Read(ref r);
                });
            }

            var okReader = new PacketReader(full);
            Assert.Equal(0xFE, okReader.ReadByte());
            MovementInfo parsed = MovementInfo.Read(ref okReader);
            Assert.Equal(0, okReader.Remaining);
            Assert.Equal(original.Flags, parsed.Flags);
            Assert.Equal(original.Time, parsed.Time);
            Assert.Equal(original.X, parsed.X);
            Assert.Equal(original.Y, parsed.Y);
            Assert.Equal(original.Z, parsed.Z);
            Assert.Equal(original.Orientation, parsed.Orientation);
            Assert.Equal(original.FallTime, parsed.FallTime);
            if (flags.HasFlag(MovementFlags.OnTransport))
            {
                Assert.Equal(original.TransportGuid, parsed.TransportGuid);
                Assert.Equal(original.TransportX, parsed.TransportX);
                Assert.Equal(original.TransportY, parsed.TransportY);
                Assert.Equal(original.TransportZ, parsed.TransportZ);
                Assert.Equal(original.TransportOrientation, parsed.TransportOrientation);
            }

            if (flags.HasFlag(MovementFlags.Swimming))
            {
                Assert.Equal(original.Pitch, parsed.Pitch);
            }

            if (flags.HasFlag(MovementFlags.Jumping))
            {
                Assert.Equal(original.JumpZSpeed, parsed.JumpZSpeed);
                Assert.Equal(original.JumpCosAngle, parsed.JumpCosAngle);
                Assert.Equal(original.JumpSinAngle, parsed.JumpSinAngle);
                Assert.Equal(original.JumpXySpeed, parsed.JumpXySpeed);
            }

            if (flags.HasFlag(MovementFlags.SplineElevation))
            {
                Assert.Equal(original.SplineElevation, parsed.SplineElevation);
            }
        }
    }

    [Fact]
    public void WorldHeaderCrypt_ExceptionsInvariantsAndRollingRoundTrip()
    {
        var crypt = new WorldHeaderCrypt();
        IWorldHeaderCrypt iface = crypt;
        Assert.False(crypt.IsInitialized);
        Assert.Equal(WorldHeaderCrypt.OutgoingHeaderLength, iface.OutgoingHeaderLength);
        Assert.Equal(WorldHeaderCrypt.IncomingHeaderLength, iface.IncomingHeaderLength);

        byte[] uninitHeader = [1, 2, 3, 4, 5, 6];
        crypt.EncryptHeader(uninitHeader);
        Assert.Equal([1, 2, 3, 4, 5, 6], uninitHeader);
        crypt.DecryptHeader(uninitHeader);
        Assert.Equal([1, 2, 3, 4, 5, 6], uninitHeader);

        Assert.Throws<ArgumentNullException>(() => crypt.Initialize(null!));
        ArgumentException emptyEx = Assert.Throws<ArgumentException>(() => crypt.Initialize([]));
        Assert.Equal("sessionKey", emptyEx.ParamName);

        byte[] key40 = new byte[WorldHeaderCrypt.SessionKeyLength];
        for (int i = 0; i < key40.Length; i++)
        {
            key40[i] = (byte)(i * 7 + 3);
        }

        crypt.Initialize(key40);
        Assert.True(crypt.IsInitialized);

        using (InvariantCapture capture = Invariant.Capture())
        {
            crypt.Initialize([1, 2, 3]);
            Assert.Equal(2, capture.Failures.Count);
        }

        var sender = new WorldHeaderCrypt();
        var receiver = new WorldHeaderCrypt();
        sender.Initialize(key40);
        receiver.Initialize(key40);

        byte[] smsgPlain = [0x00, 0x10, 0xDD, 0x01];
        byte[] smsgWire = new byte[4];
        byte[] cmsgPlain = [0x00, 0x0C, 0xDC, 0x01, 0x00, 0x00];
        byte[] cmsgWire = new byte[6];
        for (int i = 0; i < 32; i++)
        {
            smsgPlain[0] = (byte)i;
            smsgPlain.CopyTo(smsgWire, 0);
            sender.EncryptHeader(smsgWire);
            Assert.NotEqual(smsgPlain, smsgWire);
            receiver.DecryptHeader(smsgWire);
            Assert.Equal(smsgPlain, smsgWire);

            cmsgPlain[0] = (byte)(i * 3);
            cmsgPlain.CopyTo(cmsgWire, 0);
            sender.EncryptHeader(cmsgWire);
            Assert.NotEqual(cmsgPlain, cmsgWire);
            receiver.DecryptHeader(cmsgWire);
            Assert.Equal(cmsgPlain, cmsgWire);
        }
    }

    [Fact]
    public void OpcodeNamesAndMaps_HandleKnownGapAndOutOfRangeValues()
    {
        Assert.Equal("MSG_NULL_ACTION", WorldOpcodeNames.GetName(WorldOpcode.MsgNullAction));
        Assert.Equal("CMSG_PING", WorldOpcodeNames.GetName(WorldOpcode.CmsgPing));
        Assert.Equal("SMSG_PONG", WorldOpcodeNames.GetName((uint)WorldOpcode.SmsgPong));
        Assert.Equal("SMSG_DEFENSE_MESSAGE", WorldOpcodeNames.GetName(WorldOpcode.SmsgDefenseMessage));

        Assert.Equal("0x13F", WorldOpcodeNames.GetName((WorldOpcode)319));
        Assert.Equal("0x13F", WorldOpcodeNames.GetName(319u));
        Assert.Equal("0x33A", WorldOpcodeNames.GetName((WorldOpcode)826));
        Assert.Equal("0x33C", WorldOpcodeNames.GetName((WorldOpcode)828));
        Assert.Equal("0xFFFF", WorldOpcodeNames.GetName((WorldOpcode)ushort.MaxValue));
        Assert.Equal("0xFFFFFFFF", WorldOpcodeNames.GetName(uint.MaxValue));

        IdentityOpcodeMap map = IdentityOpcodeMap.Instance;
        Assert.Equal((ushort)WorldOpcode.CmsgPing, map.ToWire(WorldOpcode.CmsgPing));
        Assert.Equal(WorldOpcode.CmsgPing, map.FromWire((uint)WorldOpcode.CmsgPing));
        Assert.Equal((WorldOpcode)0xBEEF, map.FromWire(0xBEEFu));

        Assert.Equal(21, MovementOpcodes.Relayable.Count);
        Assert.True(MovementOpcodes.IsRelayable(WorldOpcode.MsgMoveStartForward));
        Assert.True(MovementOpcodes.IsRelayable(WorldOpcode.MsgMoveHeartbeat));
        Assert.False(MovementOpcodes.IsRelayable(WorldOpcode.CmsgMoveFallReset));
        Assert.False(MovementOpcodes.IsRelayable(WorldOpcode.CmsgPing));
    }

    [Fact]
    public async Task SniffDecoder_RejectsMalformedPktHeadersAndRecords()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "arcane-sniff-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            using var sink = new StringWriter();
            await Assert.ThrowsAsync<FileNotFoundException>(() => SniffDecoder.DecodeAsync(Path.Combine(tempDir, "missing.pkt"), sink));

            await AssertInvalidPktAsync(tempDir, [], "expected PKT 3.1");
            await AssertInvalidPktAsync(tempDir, new byte[65], "expected PKT 3.1");

            byte[] badMagic = BuildPktHeader(magic: "PKX", version: 0x0301, build: 5875, headerExtra: 0);
            await AssertInvalidPktAsync(tempDir, badMagic, "expected PKT 3.1");

            byte[] badVersion = BuildPktHeader(magic: "PKT", version: 0x0300, build: 5875, headerExtra: 0);
            await AssertInvalidPktAsync(tempDir, badVersion, "expected PKT 3.1");

            byte[] badBuild = BuildPktHeader(magic: "PKT", version: 0x0301, build: 5874, headerExtra: 0);
            await AssertInvalidPktAsync(tempDir, badBuild, "expected build 5875");

            byte[] negHeaderExtra = BuildPktHeader(magic: "PKT", version: 0x0301, build: 5875, headerExtra: -1);
            await AssertInvalidPktAsync(tempDir, negHeaderExtra, "bad header length");

            byte[] overHeaderExtra = BuildPktHeader(magic: "PKT", version: 0x0301, build: 5875, headerExtra: 10);
            await AssertInvalidPktAsync(tempDir, overHeaderExtra, "bad header length");

            byte[] truncatedRecordHeader = [.. BuildPktHeader(), .. new byte[19]];
            await AssertInvalidPktAsync(tempDir, truncatedRecordHeader, "truncated record header");

            byte[] invalidDirection = [.. BuildPktHeader(), .. BuildRecordHeader("MSG_", session: 1, tick: 10, extra: 0, size: 4)];
            await AssertInvalidPktAsync(tempDir, invalidDirection, "invalid record direction");

            byte[] negExtra = [.. BuildPktHeader(), .. BuildRecordHeader("CMSG", session: 1, tick: 10, extra: -1, size: 4)];
            await AssertInvalidPktAsync(tempDir, negExtra, "invalid record size");

            byte[] sizeBelowFour = [.. BuildPktHeader(), .. BuildRecordHeader("CMSG", session: 1, tick: 10, extra: 0, size: 3)];
            await AssertInvalidPktAsync(tempDir, sizeBelowFour, "invalid record size");

            byte[] cmsgOverMax = [.. BuildPktHeader(), .. BuildRecordHeader("CMSG", session: 1, tick: 10, extra: 0, size: ushort.MaxValue + 1)];
            await AssertInvalidPktAsync(tempDir, cmsgOverMax, "invalid record size");

            byte[] smsgOverMax = [.. BuildPktHeader(), .. BuildRecordHeader("SMSG", session: 1, tick: 10, extra: 0, size: ushort.MaxValue + 3)];
            await AssertInvalidPktAsync(tempDir, smsgOverMax, "invalid record size");

            byte[] sizePastEof = [.. BuildPktHeader(), .. BuildRecordHeader("CMSG", session: 1, tick: 10, extra: 2, size: 8), 1, 2, 3, 4];
            await AssertInvalidPktAsync(tempDir, sizePastEof, "invalid record size");

            byte[] negativeOpcode = [.. BuildPktHeader(), .. BuildRecord("CMSG", session: 1, tick: 10, opcode: -1, payload: [])];
            await AssertInvalidPktAsync(tempDir, negativeOpcode, "is not a 16-bit world opcode");

            byte[] over16BitOpcode = [.. BuildPktHeader(), .. BuildRecord("SMSG", session: 1, tick: 10, opcode: 0x10000, payload: [])];
            await AssertInvalidPktAsync(tempDir, over16BitOpcode, "is not a 16-bit world opcode");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SniffDecoder_DecodesValidRecords_AndCountsOracleFailuresAndTrailingBytes()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "arcane-sniff-valid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            byte[] headerWithExtra = [.. BuildPktHeader(headerExtra: 3), 0xAA, 0xBB, 0xCC];
            byte[] validPing = BuildRecord("CMSG", session: 7, tick: 1050, opcode: (int)WorldOpcode.CmsgPing, payload: [0x01, 0x02, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00], extraBytes: [0x99]);
            byte[] validPong = BuildRecord("SMSG", session: 7, tick: 1060, opcode: (int)WorldOpcode.SmsgPong, payload: [0x01, 0x02, 0x00, 0x00]);

            string okFile = Path.Combine(tempDir, "ok.pkt");
            await File.WriteAllBytesAsync(okFile, [.. headerWithExtra, .. validPing, .. validPong]);
            using var okOut = new StringWriter();
            int okCode = await SniffDecoder.DecodeAsync(okFile, okOut);
            Assert.Equal(0, okCode);
            string okText = okOut.ToString();
            Assert.Contains("CMSG CMSG_PING size=8 session=7", okText, StringComparison.Ordinal);
            Assert.Contains("SMSG SMSG_PONG size=4 session=7", okText, StringComparison.Ordinal);

            byte[] trailingPong = BuildRecord("SMSG", session: 7, tick: 1070, opcode: (int)WorldOpcode.SmsgPong, payload: [0x01, 0x02, 0x00, 0x00, 0xFF]);
            byte[] truncatedPing = BuildRecord("CMSG", session: 7, tick: 1080, opcode: (int)WorldOpcode.CmsgPing, payload: [0x01, 0x02]);
            string errFile = Path.Combine(tempDir, "err.pkt");
            await File.WriteAllBytesAsync(errFile, [.. BuildPktHeader(), .. validPong, .. trailingPong, .. truncatedPing]);
            using var errOut = new StringWriter();
            int errCode = await SniffDecoder.DecodeAsync(errFile, errOut);
            Assert.Equal(1, errCode);
            string errText = errOut.ToString();
            Assert.Contains("trailing payload bytes", errText, StringComparison.Ordinal);
            Assert.Contains("CMSG CMSG_PING size=2 decode failed at byte", errText, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public void Fuzz_PacketReaderAndWriter_RoundTripAndRandomMutationsNeverThrowUnexpectedException(int seed)
    {
        var rng = new Random(seed);
        var writer = new PacketWriter(1);

        for (int iter = 0; iter < 64; iter++)
        {
            writer.Reset();
            byte b = (byte)rng.Next(256);
            ushort u16 = (ushort)rng.Next(65536);
            uint u32 = (uint)rng.Next();
            int i32 = rng.Next(int.MinValue, int.MaxValue);
            ulong u64 = ((ulong)(uint)rng.Next() << 32) | (uint)rng.Next();
            float f32 = (float)(rng.NextDouble() * 2000.0 - 1000.0);
            ulong guid = ((ulong)(uint)rng.Next() << 32) | (uint)rng.Next();
            if (rng.Next(4) == 0)
            {
                guid &= 0x00FF00FF00FF00FFUL;
            }

            int byteLen = rng.Next(0, 32);
            byte[] rawBytes = new byte[byteLen];
            rng.NextBytes(rawBytes);
            string str = $"fuzz-{seed}-{iter}-{rng.Next(1000)}";

            writer.WriteByte(b);
            writer.WriteUInt16(u16);
            int patchAt = writer.Length;
            writer.WriteUInt32(0u);
            writer.WriteInt32(i32);
            writer.WriteUInt64(u64);
            writer.WriteFiniteSingle(f32);
            writer.WritePackedGuid(guid);
            writer.WriteBytes(rawBytes);
            writer.WriteCString(str);
            writer.PatchUInt32(patchAt, u32);

            byte[] payload = writer.ToArray();
            var reader = new PacketReader(payload);
            Assert.Equal(b, reader.ReadByte());
            Assert.Equal(u16, reader.ReadUInt16());
            Assert.Equal(u32, reader.ReadUInt32());
            Assert.Equal(i32, reader.ReadInt32());
            Assert.Equal(u64, reader.ReadUInt64());
            Assert.Equal(f32, reader.ReadFiniteSingle());
            Assert.Equal(guid, reader.ReadPackedGuid());
            Assert.Equal(rawBytes, reader.ReadBytes(byteLen).ToArray());
            Assert.Equal(str, reader.ReadCString(64));
            Assert.Equal(0, reader.Remaining);

            byte[] noise = new byte[rng.Next(0, 96)];
            rng.NextBytes(noise);
            ExerciseArbitraryReader(noise, rng);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public void Fuzz_MovementInfoAndHeaderCrypt_RoundTripTruncationAndMutation(int seed)
    {
        var rng = new Random(seed);
        var writer = new PacketWriter();

        byte[] key = new byte[WorldHeaderCrypt.SessionKeyLength];
        rng.NextBytes(key);
        var enc = new WorldHeaderCrypt();
        var dec = new WorldHeaderCrypt();
        enc.Initialize(key);
        dec.Initialize(key);

        byte[] header4 = new byte[4];
        byte[] header4Copy = new byte[4];
        byte[] header6 = new byte[6];
        byte[] header6Copy = new byte[6];

        for (int iter = 0; iter < 64; iter++)
        {
            rng.NextBytes(header4);
            header4.CopyTo(header4Copy, 0);
            enc.EncryptHeader(header4Copy);
            dec.DecryptHeader(header4Copy);
            Assert.Equal(header4, header4Copy);

            rng.NextBytes(header6);
            header6.CopyTo(header6Copy, 0);
            enc.EncryptHeader(header6Copy);
            dec.DecryptHeader(header6Copy);
            Assert.Equal(header6, header6Copy);

            var info = new MovementInfo
            {
                Flags = (MovementFlags)(uint)rng.Next(),
                Time = (uint)rng.Next(),
                X = (float)(rng.NextDouble() * 1000.0 - 500.0),
                Y = (float)(rng.NextDouble() * 1000.0 - 500.0),
                Z = (float)(rng.NextDouble() * 200.0 - 100.0),
                Orientation = (float)(rng.NextDouble() * 6.28),
                TransportGuid = ((ulong)(uint)rng.Next() << 32) | (uint)rng.Next(),
                TransportX = (float)rng.NextDouble(),
                TransportY = (float)rng.NextDouble(),
                TransportZ = (float)rng.NextDouble(),
                TransportOrientation = (float)rng.NextDouble(),
                Pitch = (float)rng.NextDouble(),
                FallTime = (uint)rng.Next(),
                JumpZSpeed = (float)rng.NextDouble(),
                JumpCosAngle = (float)rng.NextDouble(),
                JumpSinAngle = (float)rng.NextDouble(),
                JumpXySpeed = (float)rng.NextDouble(),
                SplineElevation = (float)rng.NextDouble(),
            };
            info.CorrectData();
            Assert.False(info.HasFlag(MovementFlags.Root) && (info.Flags & MovementFlags.MaskMoving) != 0);
            Assert.False(info.HasFlag(MovementFlags.TurnLeft) && info.HasFlag(MovementFlags.TurnRight));
            Assert.False(info.HasFlag(MovementFlags.StrafeLeft) && info.HasFlag(MovementFlags.StrafeRight));
            Assert.False(info.HasFlag(MovementFlags.PitchUp) && info.HasFlag(MovementFlags.PitchDown));
            Assert.False(info.HasFlag(MovementFlags.Forward) && info.HasFlag(MovementFlags.Backward));

            writer.Reset();
            info.Write(writer);
            byte[] encoded = writer.ToArray();

            var fullReader = new PacketReader(encoded);
            Assert.True(MovementInfo.TryRead(ref fullReader, out MovementInfo decoded));
            Assert.Equal(0, fullReader.Remaining);
            Assert.Equal(info.Flags, decoded.Flags);
            Assert.Equal(info.Time, decoded.Time);
            Assert.Equal(info.FallTime, decoded.FallTime);

            int cut = rng.Next(0, encoded.Length);
            byte[] truncated = encoded[..cut];
            var truncReader = new PacketReader(truncated);
            Assert.False(MovementInfo.TryRead(ref truncReader, out _));
            Assert.Equal(0, truncReader.Position);
            AssertMalformed(() =>
            {
                var r = new PacketReader(truncated);
                _ = MovementInfo.Read(ref r);
            });

            byte[] randomBytes = new byte[rng.Next(0, 96)];
            rng.NextBytes(randomBytes);
            var randReader = new PacketReader(randomBytes);
            bool ok = MovementInfo.TryRead(ref randReader, out _);
            if (!ok)
            {
                Assert.Equal(0, randReader.Position);
                AssertMalformed(() =>
                {
                    var r = new PacketReader(randomBytes);
                    _ = MovementInfo.Read(ref r);
                });
            }
            else
            {
                Assert.InRange(randReader.Position, 28, randomBytes.Length);
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(20251004)]
    public async Task Fuzz_SniffDecoder_RandomAndMutatedPktStreams(int seed)
    {
        var rng = new Random(seed);
        string tempDir = Path.Combine(Path.GetTempPath(), $"arcane-sniff-fuzz-{seed}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string pktPath = Path.Combine(tempDir, "fuzz.pkt");
            for (int iter = 0; iter < 32; iter++)
            {
                byte[] candidate;
                int mode = rng.Next(4);
                if (mode == 0)
                {
                    candidate = new byte[rng.Next(0, 160)];
                    rng.NextBytes(candidate);
                }
                else
                {
                    int extraHeaderLen = rng.Next(0, 12);
                    byte[] extraHeader = new byte[extraHeaderLen];
                    rng.NextBytes(extraHeader);

                    var stream = new MemoryStream();
                    stream.Write(BuildPktHeader(headerExtra: extraHeaderLen));
                    stream.Write(extraHeader);

                    int records = rng.Next(1, 5);
                    for (int r = 0; r < records; r++)
                    {
                        string dir = rng.Next(2) == 0 ? "CMSG" : "SMSG";
                        int opcode = rng.Next(3) switch
                        {
                            0 => (int)WorldOpcode.CmsgPing,
                            1 => (int)WorldOpcode.SmsgPong,
                            _ => rng.Next(0, 900),
                        };
                        byte[] payload = new byte[rng.Next(0, 24)];
                        rng.NextBytes(payload);
                        stream.Write(BuildRecord(dir, session: r, tick: (uint)(1000 + r * 10), opcode: opcode, payload: payload));
                    }

                    candidate = stream.ToArray();
                    if (mode == 1 && candidate.Length > 0)
                    {
                        candidate = candidate[..rng.Next(0, candidate.Length)];
                    }
                    else if (mode == 2 && candidate.Length > 0)
                    {
                        int flipAt = rng.Next(0, candidate.Length);
                        candidate[flipAt] ^= (byte)(1 << rng.Next(0, 8));
                    }
                }

                await File.WriteAllBytesAsync(pktPath, candidate);
                using var sink = new StringWriter();
                try
                {
                    int exitCode = await SniffDecoder.DecodeAsync(pktPath, sink);
                    Assert.True(exitCode is 0 or 1);
                }
                catch (InvalidDataException)
                {
                    // Expected controlled error for corrupt PKT headers or record headers.
                }
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static void ExerciseArbitraryReader(byte[] data, Random rng)
    {
        var reader = new PacketReader(data);
        while (reader.Remaining > 0)
        {
            int before = reader.Position;
            int op = rng.Next(12);
            switch (op)
            {
                case 0:
                    Assert.True(reader.TryReadByte(out _));
                    Assert.Equal(before + 1, reader.Position);
                    break;
                case 1:
                    if (!reader.TryReadUInt16(out _))
                    {
                        Assert.Equal(before, reader.Position);
                        _ = reader.ReadByte();
                    }
                    break;
                case 2:
                    if (!reader.TryReadUInt32(out _))
                    {
                        Assert.Equal(before, reader.Position);
                        _ = reader.ReadByte();
                    }
                    break;
                case 3:
                    if (!reader.TryReadUInt64(out _))
                    {
                        Assert.Equal(before, reader.Position);
                        _ = reader.ReadByte();
                    }
                    break;
                case 4:
                    if (!reader.TryReadFiniteSingle(out _))
                    {
                        Assert.Equal(before, reader.Position);
                        _ = reader.ReadByte();
                    }
                    break;
                case 5:
                    if (!reader.TryReadFiniteCoordinates(out _, out _, out _))
                    {
                        Assert.Equal(before, reader.Position);
                        _ = reader.ReadByte();
                    }
                    break;
                case 6:
                    if (!reader.TryReadPackedGuid(out _))
                    {
                        Assert.Equal(before, reader.Position);
                        _ = reader.ReadByte();
                    }
                    break;
                case 7:
                    int countBound = rng.Next(-2, 64);
                    if (!reader.TryReadCount(countBound, out _))
                    {
                        Assert.Equal(before, reader.Position);
                        _ = reader.ReadByte();
                    }
                    break;
                case 8:
                    int byteBound = rng.Next(-2, 260);
                    if (!reader.TryReadByteCount(byteBound, out _))
                    {
                        Assert.Equal(before, reader.Position);
                        _ = reader.ReadByte();
                    }
                    break;
                case 9:
                    int strBound = rng.Next(-2, 16);
                    if (!reader.TryReadCString(strBound, out _))
                    {
                        Assert.Equal(before, reader.Position);
                        _ = reader.ReadByte();
                    }
                    break;
                case 10:
                    int sliceLen = rng.Next(-2, 16);
                    if (!reader.TryReadBytes(sliceLen, out _))
                    {
                        Assert.Equal(before, reader.Position);
                        _ = reader.ReadByte();
                    }
                    break;
                default:
                    int skipLen = rng.Next(-2, 16);
                    if (!reader.TrySkip(skipLen))
                    {
                        Assert.Equal(before, reader.Position);
                        _ = reader.ReadByte();
                    }
                    break;
            }

            Assert.InRange(reader.Position, 0, data.Length);
        }
    }

    private static void AssertMalformed(Action action)
    {
        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(action);
        Assert.Equal(MalformedPacket.ParamName, ex.ParamName);
        Assert.True(MalformedPacket.Is(ex));
    }

    private static async Task AssertInvalidPktAsync(string dir, byte[] content, string expectedMessageSubstring)
    {
        string path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".pkt");
        await File.WriteAllBytesAsync(path, content);
        using var output = new StringWriter();
        InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(() => SniffDecoder.DecodeAsync(path, output));
        Assert.Contains(expectedMessageSubstring, ex.Message, StringComparison.Ordinal);
    }

    private static byte[] BuildPktHeader(string magic = "PKT", ushort version = 0x0301, uint build = 5875, int headerExtra = 0)
    {
        byte[] header = new byte[66];
        Encoding.ASCII.GetBytes(magic, header.AsSpan(0, 3));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(3, 2), version);
        header[5] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(6, 4), build);
        Encoding.ASCII.GetBytes("enUS", header.AsSpan(10, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(54, 4), 1700000000u);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(58, 4), 1000u);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(62, 4), headerExtra);
        return header;
    }

    private static byte[] BuildRecordHeader(string direction, int session, uint tick, int extra, int size)
    {
        byte[] header = new byte[20];
        Encoding.ASCII.GetBytes(direction, header.AsSpan(0, 4));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), session);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), tick);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12, 4), extra);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16, 4), size);
        return header;
    }

    private static byte[] BuildRecord(string direction, int session, uint tick, int opcode, byte[] payload, byte[]? extraBytes = null)
    {
        byte[] extra = extraBytes ?? [];
        byte[] record = new byte[20 + extra.Length + 4 + payload.Length];
        BuildRecordHeader(direction, session, tick, extra.Length, 4 + payload.Length).CopyTo(record, 0);
        extra.CopyTo(record, 20);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(20 + extra.Length, 4), opcode);
        payload.CopyTo(record, 24 + extra.Length);
        return record;
    }
}
