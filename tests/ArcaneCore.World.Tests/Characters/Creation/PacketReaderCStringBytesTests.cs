using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.World.Tests.Characters.Creation;

/// <summary>PacketReader.ReadCStringBytes: the raw bytes of a C string, for names that must be checked as UTF-8.</summary>
public sealed class PacketReaderCStringBytesTests
{
    [Fact]
    public void ReturnsTheRawSpanAndConsumesTheTerminator()
    {
        byte[] data = [0xFF, 0x41, 0x00, 0x07];
        var reader = new PacketReader(data);
        Assert.Equal(new byte[] { 0xFF, 0x41 }, reader.ReadCStringBytes().ToArray());
        Assert.Equal(3, reader.Position);
        Assert.Equal(0x07, reader.ReadByte());
    }

    [Fact]
    public void MissingTerminator_ReadsToTheEnd()
    {
        byte[] data = [0x41, 0x42];
        var reader = new PacketReader(data);
        Assert.Equal(data, reader.ReadCStringBytes().ToArray());
        Assert.Equal(0, reader.Remaining);
    }
}

/// <summary>CMSG_CHAR_CREATE over loopback: the name is checked as raw UTF-8 (ObjectMgr.cpp:64-80).</summary>
public sealed class CharCreateRawNameTests
{
    [Fact]
    public async Task InvalidUtf8Name_IsAnsweredWithNoName_NotMixedLanguages()
    {
        await using var host = WorldTestHost.Start();
        byte[] key = await host.AddAccountAsync("RAWNAME");
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("RAWNAME", key);

        byte[] payload = [0xFF, 0xFE, 0x00, 1, 1, 0, 0, 0, 0, 0, 0, 0];
        await client.SendAsync(WorldOpcode.CmsgCharCreate, payload);
        (WorldOpcode op, byte[] answer) = await client.ReadAsync();
        Assert.Equal(WorldOpcode.SmsgCharCreate, op);
        Assert.Equal((byte)CharResult.CharNameNoName, answer[0]);
    }
}
