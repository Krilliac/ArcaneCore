using System.Buffers.Binary;
using System.IO.Compression;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Net;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.World.Tests.Net;

/// <summary>
/// <see cref="AddonInfo.BuildResponse"/> on a block that is not zlib must not raise a first-chance exception at all (a debugger or an
/// exception-rate counter sees every thrown-and-caught one), and a valid block must answer exactly as before.
/// </summary>
public sealed class AddonInfoFirstChanceTests
{
    // The 256-byte Blizzard public-key blob AddonInfo sends when a Blizzard addon's modulus CRC mismatches (vmangos AddonHandler.cpp).
    private const string PublicKeyHex =
        "C35B5084B93E32428CD0C748FA0E5D545AA30E14BA9E0DB95D8BEEB684934575FF31FE2F643F3D6D07D9449B408559344E10E1E74369EF7C16FCB4ED1B9528A8"
        + "2376513157302B790850101C4A1A2CC88B8F052D223DDB5A247A0F1350378F5ACC9E04440E8701D4A315941634C6C2C3FB49FEE1F9DA8C503CBE2CBB57ED46B9"
        + "AD8BC6DF0ED60FBE80B38B1E77CFAD22CFB74BCFFBF06B11452D7A8118F2927E98565D5E69720A0D030A85A2859CCBFB566E8F44BB8F0222686397BC85BAA8F7"
        + "B540683C77866F4BD788CA8AD7CE36F0456ED564790F17FC64DD106FF3F5E0A6C3FB1B8C29EF8EE534CBD12ACE79C39A0D36EA01E0AA912054F072D81EC789D2";

    [Fact]
    public void NonZlibBlock_RaisesNoFirstChanceException_AndAnswersEmpty()
    {
        // LE uncompressed size 16, then 00 01 02 ...: CMF 0x00 has compression method 0, not 8, so it is not a zlib stream.
        byte[] block = new byte[4 + 16];
        BinaryPrimitives.WriteUInt32LittleEndian(block, 16);
        for (int i = 4; i < block.Length; i++)
        {
            block[i] = (byte)(i - 4);
        }

        var log = new CapturingLogger<AddonInfoFirstChanceTests>();
        (byte[] response, List<Exception> raised) = Record(() => AddonInfo.BuildResponse(block, log, "1.2.3.4:5"));

        Assert.Empty(raised);
        Assert.Empty(response);
        CapturedLog line = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Debug, line.Level);
        Assert.Equal("[1.2.3.4:5] addon block is not zlib (CMF 0x00, len 20); sending empty addon info", line.Message);
    }

    [Fact]
    public void BlockTooShortForAZlibHeader_LogsCmfNotAvailable()
    {
        byte[] block = [16, 0, 0, 0]; // size only, no CMF byte
        var log = new CapturingLogger<AddonInfoFirstChanceTests>();

        Assert.Empty(AddonInfo.BuildResponse(block, log, "e"));

        Assert.Equal("[e] addon block is not zlib (CMF n/a, len 4); sending empty addon info", Assert.Single(log.Entries).Message);
    }

    [Fact]
    public void ValidBlock_AnswersPerRecord_WithoutFirstChanceException()
    {
        var inner = new PacketWriter(128);
        WriteRecord(inner, "MyAddon", 0x12345678);                 // not Blizzard
        WriteRecord(inner, "Blizzard_AuthChallenge", 0x4C1C776D);  // correct modulus CRC
        WriteRecord(inner, "Blizzard_Wrong", 0xDEADBEEF);          // wrong modulus CRC
        byte[] uncompressed = inner.AsMemory().ToArray();

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(uncompressed);
        }

        byte[] body = compressed.ToArray();
        byte[] block = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(block, (uint)uncompressed.Length);
        body.CopyTo(block, 4);

        // Per AddonInfo's layout: non-Blizzard = status 1, info 0, url 0; Blizzard = status 2, info 1, key flag (+256-byte key if the
        // CRC mismatches), u32 revision 0, url 0.
        byte[] expected =
        [
            1, 0, 0,
            2, 1, 0, 0, 0, 0, 0, 0,
            2, 1, 1, .. Convert.FromHexString(PublicKeyHex), 0, 0, 0, 0, 0,
        ];

        (byte[] response, List<Exception> raised) = Record(() => AddonInfo.BuildResponse(block));

        Assert.Empty(raised);
        Assert.Equal(expected, response);
    }

    private static void WriteRecord(PacketWriter writer, string name, uint modulusCrc)
    {
        writer.WriteCString(name);
        writer.WriteByte(1);
        writer.WriteUInt32(modulusCrc);
        writer.WriteUInt32(0);
    }

    /// <summary>Runs <paramref name="call"/> and returns its result with every first-chance exception raised on this thread meanwhile (tests run in parallel).</summary>
    private static (byte[] Result, List<Exception> Raised) Record(Func<byte[]> call)
    {
        int thread = Environment.CurrentManagedThreadId;
        var raised = new List<Exception>();
        void Handler(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            if (Environment.CurrentManagedThreadId == thread)
            {
                raised.Add(e.Exception);
            }
        }

        AppDomain.CurrentDomain.FirstChanceException += Handler;
        try
        {
            return (call(), raised);
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= Handler;
        }
    }
}
