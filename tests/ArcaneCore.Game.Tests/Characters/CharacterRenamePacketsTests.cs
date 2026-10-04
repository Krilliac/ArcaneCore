using System.Text;
using ArcaneCore.Game.Characters;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Characters;

/// <summary>CMSG_CHAR_RENAME and SMSG_CHAR_RENAME layouts (mangos CharacterHandlerCustomize.cpp:86-190).</summary>
public sealed class CharacterRenamePacketsTests
{
    [Fact]
    public void Request_IsAGuidAndACString_AndKeepsTheRawNameBytes()
    {
        byte[] name = [0xC3, 0xA9, (byte)'x']; // "éx" in UTF-8
        var payload = new PacketWriter();
        payload.WriteUInt64(7);
        payload.WriteBytes(name);
        payload.WriteByte(0);

        CharacterRenameRequest request = CharacterRenamePackets.ReadRequest(payload.AsSpan())!.Value;
        Assert.Equal(7ul, request.Guid);
        Assert.Equal(name, request.RawName);
    }

    [Fact]
    public void Request_WithoutATerminator_ReadsToTheEnd_AndAnEmptyNameIsEmpty()
    {
        var payload = new PacketWriter();
        payload.WriteUInt64(3);
        payload.WriteBytes("Thrall"u8);
        Assert.Equal("Thrall", Encoding.UTF8.GetString(CharacterRenamePackets.ReadRequest(payload.AsSpan())!.Value.RawName));

        var bare = new PacketWriter();
        bare.WriteUInt64(3);
        Assert.Empty(CharacterRenamePackets.ReadRequest(bare.AsSpan())!.Value.RawName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void ARequestShorterThanAGuid_IsRefusedInsteadOfReadingGarbage(int length)
        => Assert.Null(CharacterRenamePackets.ReadRequest(new byte[length]));

    [Fact]
    public void Failure_IsTheResultByteAlone()
    {
        Assert.Equal([(byte)CharResult.CharNameNoName], CharacterRenamePackets.BuildFailure(CharResult.CharNameNoName));
        Assert.Equal([(byte)0x2F], CharacterRenamePackets.BuildFailure(CharResult.CharCreateError));
    }

    [Fact]
    public void Success_IsZero_TheGuidAndTheNewName()
    {
        byte[] packet = CharacterRenamePackets.BuildSuccess(0x1122, "Jaina");
        var reader = new PacketReader(packet);
        Assert.Equal(0, reader.ReadByte());
        Assert.Equal(0x1122ul, reader.ReadUInt64());
        Assert.Equal("Jaina", reader.ReadCString());
        Assert.Equal(0, reader.Remaining);
        Assert.Equal(1 + 8 + 5 + 1, packet.Length);
        Assert.Throws<ArgumentException>(() => CharacterRenamePackets.BuildSuccess(1, string.Empty));
    }

    [Fact]
    public void InvalidatePlayer_IsTheGuid()
        => Assert.Equal([0x22, 0x11, 0, 0, 0, 0, 0, 0], CharacterRenamePackets.BuildInvalidatePlayer(0x1122));

    [Fact]
    public void TheRenameFlagIsBit0x4000OfTheCharacterListFlags()
        => Assert.Equal(0x4000u, CharacterRenamePackets.CharacterFlagRename);
}
