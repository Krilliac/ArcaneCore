using System.Buffers.Binary;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Bans;
using Xunit;

namespace ArcaneCore.World.Tests.Bans;

/// <summary>
/// The session address and the banned address are compared in one canonical form on both sides: an address
/// a store returns (or an event carries) in a stored, padded or IPv4-mapped spelling still matches the session.
/// </summary>
public sealed class BanAddressNormalizationTests
{
    private static readonly long RealNow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    [Fact]
    public async Task Recheck_StoredRowInANonCanonicalForm_StillKicksTheSession()
    {
        await using var host = WorldTestHost.Start(banOptions: new BanOptions { RecheckIntervalSeconds = 0.05 });
        await using WorldTestClient client = await host.EnterWorldAsync("PADDED", "Padded");

        host.Bans.AddIpRow("127.0.0.1 ", RealNow - 1, RealNow - 1); // written by another tool, trailing space kept

        Assert.True(await client.IsClosedByServerAsync());
    }

    [Theory]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData(" 127.0.0.1 ")]
    public async Task LiveKick_EventAddressInANonCanonicalForm_StillKicksTheSession(string published)
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("MAPPED", "Mapped");
        int id = (await host.Accounts.FindByUsernameAsync("MAPPED"))!.Id;

        host.StatusEvents.Publish(new IpBanChange(published));

        Assert.True(await client.IsClosedByServerAsync());
        await WorldTestHost.WaitForAsync(() => host.Registry.Find(id) is null, "the session to unregister");
    }

    [Fact]
    public async Task LiveKick_AnotherAddress_KeepsTheSession()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("OTHERIP", "Otherip");
        int id = (await host.Accounts.FindByUsernameAsync("OTHERIP"))!.Id;

        host.StatusEvents.Publish(new IpBanChange("::ffff:10.0.0.9"));

        await client.SendAsync(WorldOpcode.CmsgPing, [9, 0, 0, 0, 0, 0, 0, 0]);
        byte[] pong = await client.ReadUntilAsync(WorldOpcode.SmsgPong);
        Assert.Equal(9u, BinaryPrimitives.ReadUInt32LittleEndian(pong));
        Assert.NotNull(host.Registry.Find(id));
    }
}
