using ArcaneCore.Kernel;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using Xunit;

namespace ArcaneCore.World.Tests.Net;

/// <summary>The CMSG_AUTH_SESSION reader: bounded, non-throwing, windows instead of copies.</summary>
public sealed class AuthSessionRequestTests
{
    private static byte[] Build(string account, byte[] digest, byte[] addon)
    {
        var writer = new PacketWriter(128);
        writer.WriteUInt32(ClientBuild.Vanilla1121);
        writer.WriteUInt32(7);
        writer.WriteCString(account);
        writer.WriteUInt32(0x1BADD00D);
        writer.WriteBytes(digest);
        writer.WriteBytes(addon);
        return writer.ToArray();
    }

    [Fact]
    public void Parses_TheBuild5875Layout_AndUppercasesTheAccount()
    {
        byte[] digest = [.. Enumerable.Range(1, 20).Select(i => (byte)i)];
        byte[] addon = [9, 9, 9];
        byte[] payload = Build("tester", digest, addon);

        Assert.True(AuthSessionRequest.TryParse(payload, out AuthSessionRequest request));
        Assert.Equal(ClientBuild.Vanilla1121, request.Build);
        Assert.Equal(7u, request.ServerId);
        Assert.Equal("TESTER", request.Account);
        Assert.Equal(0x1BADD00Du, request.ClientSeed);
        Assert.Equal(digest, request.ClientDigest.ToArray());
        Assert.Equal(addon, request.AddonBlock.ToArray());
    }

    [Fact]
    public void EmptyAddonBlock_IsAnEmptyWindow()
    {
        Assert.True(AuthSessionRequest.TryParse(Build("A", new byte[20], []), out AuthSessionRequest request));
        Assert.Equal(0, request.AddonBlock.Length);
    }

    [Fact]
    public void TooLongAccountName_IsMalformed_BeforeAnyLookup()
    {
        string tooLong = new('A', AuthSessionRequest.MaxAccountNameBytes + 1);
        Assert.False(AuthSessionRequest.TryParse(Build(tooLong, new byte[20], []), out _));
        string atTheBound = new('A', AuthSessionRequest.MaxAccountNameBytes);
        Assert.True(AuthSessionRequest.TryParse(Build(atTheBound, new byte[20], []), out AuthSessionRequest ok));
        Assert.Equal(atTheBound, ok.Account);
    }

    [Fact]
    public void TruncatedAnywhere_IsMalformed_WithoutAnException()
    {
        byte[] payload = Build("TESTER", new byte[20], [1, 2, 3, 4]);
        int addonStart = payload.Length - 4;
        for (int cut = 0; cut < addonStart; cut++)
        {
            Assert.False(AuthSessionRequest.TryParse(payload.AsMemory(0, cut), out _));
        }

        Assert.True(AuthSessionRequest.TryParse(payload.AsMemory(0, addonStart), out AuthSessionRequest exact)); // the addon block may be absent
        Assert.Equal(0, exact.AddonBlock.Length);
    }
}
