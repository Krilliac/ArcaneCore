using System.IO.Compression;
using System.Security.Cryptography;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Packets;
using Xunit;

namespace ArcaneCore.World.Tests;

/// <summary>Unit tests for the M6 building blocks that need no running world.</summary>
public sealed class M6UnitTests
{
    [Theory]
    [InlineData("thrall", "Thrall")]
    [InlineData("THRALL", "Thrall")]
    [InlineData("éLUNE", "Élune")]
    [InlineData("жЕНЯ", "Женя")]
    [InlineData("", "")]
    public void Names_AreNormalizedLikeVmangos(string input, string expected)
        => Assert.Equal(expected, CharacterNames.Normalize(input));

    [Theory]
    [InlineData("Ab", null)]
    [InlineData("Abcdefghijkl", null)] // 12
    [InlineData("Þórr", null)]
    [InlineData("Ŋ", CharResult.CharNameTooShort)] // U+014A: past vmangos' extended Latin range, but length is checked first
    [InlineData("Ŋorth", CharResult.CharNameMixedLanguages)]
    [InlineData("Ab cd", CharResult.CharNameMixedLanguages)]
    [InlineData("Ab'cd", CharResult.CharNameMixedLanguages)]
    [InlineData("Αλφα", CharResult.CharNameMixedLanguages)] // Greek is not an accepted script
    [InlineData("한글이름", null)]
    [InlineData("Abcdefghijklm", CharResult.CharNameTooLong)]
    [InlineData("Abcdefghijklmnop", CharResult.CharNameNoName)]
    public void Names_AreValidatedLikeVmangos(string name, CharResult? expected)
        => Assert.Equal(expected, CharacterNames.Validate(name));

    [Fact]
    public void AccountData_RoundTrips_WithAndWithoutTheAdlerTrailer()
    {
        byte[] data = RandomNumberGenerator.GetBytes(5000);
        byte[] full = AccountDataCompression.Compress(data);
        Assert.Equal(0x78, full[0]); // zlib header

        Assert.True(AccountDataCompression.TryDecompress(full, data.Length, out byte[] inflated));
        Assert.Equal(data, inflated);
        Assert.True(AccountDataCompression.TryDecompress(full.AsSpan(0, full.Length - 4), data.Length, out inflated));
        Assert.Equal(data, inflated);
    }

    [Fact]
    public void AccountData_RejectsWrongSizesHeadersAndGarbage()
    {
        byte[] data = "SET uiScale \"0.9\"\n"u8.ToArray();
        byte[] zlib = AccountDataCompression.Compress(data);

        Assert.False(AccountDataCompression.TryDecompress(zlib, data.Length + 1, out _)); // shorter than declared
        Assert.False(AccountDataCompression.TryDecompress(zlib, data.Length - 1, out _)); // longer than declared
        Assert.False(AccountDataCompression.TryDecompress(zlib, 0, out _));
        Assert.False(AccountDataCompression.TryDecompress(zlib, AccountDataCompression.MaxDecompressedSize + 1, out _));

        byte[] badCheck = (byte[])zlib.Clone();
        badCheck[1] ^= 0x01; // FCHECK no longer makes the header a multiple of 31
        Assert.False(AccountDataCompression.TryDecompress(badCheck, data.Length, out _));

        Assert.False(AccountDataCompression.TryDecompress([0x78, 0x9C, 0xFF, 0xFF, 0xFF], data.Length, out _));
        Assert.False(AccountDataCompression.TryDecompress([0x78], data.Length, out _));

        using var raw = new MemoryStream();
        using (var deflate = new DeflateStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(data);
        }

        Assert.False(AccountDataCompression.TryDecompress(raw.ToArray(), data.Length, out _)); // raw deflate: no zlib header
    }

    [Fact]
    public void AccountDataHashes_AreMd5PerType_ZeroWhenEmpty()
    {
        var settings = new AccountSettings();
        settings.Data[1] = new AccountDataEntry(1, "abc"u8.ToArray());
        settings.Data[3] = new AccountDataEntry(1, []);

        byte[] packet = LoginPackets.BuildAccountDataMd5(settings);
        Assert.Equal(128, packet.Length);
        Assert.Equal(new byte[16], packet[..16]);
        Assert.Equal(Convert.FromHexString("900150983CD24FB0D6963F7D28E17F72"), packet[16..32]); // RFC 1321: MD5("abc")
        Assert.Equal(new byte[96], packet[32..]);
    }

    [Theory]
    [InlineData(".help", true, "help")]
    [InlineData("!gm on", true, "gm on")]
    [InlineData(".", false, "")]
    [InlineData("!", false, "")]
    [InlineData("..", false, "")]
    [InlineData(".!x", false, "")]
    [InlineData("!!", false, "")]
    [InlineData("hello", false, "")]
    [InlineData(" .help", false, "")]
    public void CommandPrefix_FollowsVmangosParseCommands(string message, bool isCommand, string text)
    {
        Assert.Equal(isCommand, CommandTable.TryGetCommandText(message, out string commandText));
        Assert.Equal(text, commandText);
    }

    [Fact]
    public void CommandResolution_PrefersExactNames_AndHidesCommandsAboveTheCaller()
    {
        CommandTable commands = BuiltinCommands.Create();
        Assert.Equal("save", commands.Resolve("save", AccountSecurity.Moderator)!.Name);
        Assert.Equal("saveall", commands.Resolve("savea", AccountSecurity.Moderator)!.Name);
        Assert.Null(commands.Resolve("savea", AccountSecurity.Player));
        Assert.Equal("money", commands.Resolve("mod mo", AccountSecurity.Moderator)!.Name);
        Assert.Null(commands.Resolve("kick", AccountSecurity.Moderator));
        Assert.Equal("kick", commands.Resolve("KICK", AccountSecurity.GameMaster)!.Name);
        Assert.Null(commands.Resolve("server nothing", AccountSecurity.Administrator));
    }
}
