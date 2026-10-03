using System.Buffers.Binary;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Bans;
using ArcaneCore.World.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Bans;

/// <summary>
/// The optional periodic re-check enforces bans written outside the process (no event is published for them).
/// Off by default: retail never kicks for an externally written row (AccountMgr.cpp:317-327).
/// </summary>
public sealed class BanRecheckTests
{
    private static readonly long RealNow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static BanOptions Fast => new() { RecheckIntervalSeconds = 0.05 };

    [Fact]
    public async Task ExternalBanRow_IsEnforced_WithinAFewIntervals()
    {
        await using var host = WorldTestHost.Start(banOptions: Fast);
        await using WorldTestClient client = await host.EnterWorldAsync("EXTERNAL", "Extern");
        int id = (await host.Accounts.FindByUsernameAsync("EXTERNAL"))!.Id;

        host.Bans.AddAccountRow(id, RealNow - 1, RealNow - 1); // SQL / AccountTool in another process: no event

        Assert.True(await client.IsClosedByServerAsync());
        await WorldTestHost.WaitForAsync(() => host.Registry.Find(id) is null, "the session to unregister");
    }

    [Fact]
    public async Task WithTheDefaultOff_AnExternalBanIsNotEnforced_AsInRetail()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("RETAIL", "Retailer");
        int id = (await host.Accounts.FindByUsernameAsync("RETAIL"))!.Id;

        host.Bans.AddAccountRow(id, RealNow - 1, RealNow - 1);
        await Task.Delay(500);

        await PingAsync(client);
        Assert.NotNull(host.Registry.Find(id));
        Assert.Equal(0, host.Bans.FindBannedAccountsCalls);
    }

    [Fact]
    public async Task StatusColumnFlipWithoutAnEvent_IsEnforced()
    {
        await using var host = WorldTestHost.Start(banOptions: Fast);
        await using WorldTestClient client = await host.EnterWorldAsync("FLIPPED", "Flipper");

        (await host.Accounts.FindByUsernameAsync("FLIPPED"))!.Status = AccountStatus.Banned; // raw write, no event

        Assert.True(await client.IsClosedByServerAsync());
    }

    [Fact]
    public async Task ExternalIpBan_IsEnforced()
    {
        await using var host = WorldTestHost.Start(banOptions: Fast);
        await using WorldTestClient client = await host.EnterWorldAsync("IPEXT", "Ipext");

        host.Bans.AddIpRow("127.0.0.1", RealNow - 1, RealNow - 1);

        Assert.True(await client.IsClosedByServerAsync());
    }

    [Fact]
    public async Task ExpiredAndInactiveRows_KickNobody()
    {
        await using var host = WorldTestHost.Start(banOptions: Fast);
        await using WorldTestClient client = await host.EnterWorldAsync("EXPIRED", "Expiry");
        int id = (await host.Accounts.FindByUsernameAsync("EXPIRED"))!.Id;
        host.Bans.AddAccountRow(id, RealNow - 7200, RealNow - 3600);
        host.Bans.AddAccountRow(id, RealNow - 5, RealNow - 5, active: false);
        host.Bans.AddIpRow("127.0.0.1", RealNow - 7200, RealNow - 3600);

        int before = host.Bans.FindBannedAccountsCalls;
        await WorldTestHost.WaitForAsync(() => host.Bans.FindBannedAccountsCalls >= before + 3, "a few passes to run");

        await PingAsync(client);
        Assert.NotNull(host.Registry.Find(id));
    }

    [Fact]
    public async Task StoreFailure_KicksNobody_LogsAndTheNextPassWorks()
    {
        await using var host = WorldTestHost.Start(banOptions: Fast);
        await using WorldTestClient client = await host.EnterWorldAsync("BLIP", "Blipper");
        int id = (await host.Accounts.FindByUsernameAsync("BLIP"))!.Id;
        host.Bans.AddAccountRow(id, RealNow - 1, RealNow - 1);
        host.Bans.FailWith = new InvalidOperationException("database blip");
        // The ban row exists but every read fails: fail open, nobody is disconnected.
        await Task.Delay(400);
        Assert.NotNull(host.Registry.Find(id));

        host.Bans.FailWith = null; // the database is back: the timer survived the errors
        Assert.True(await client.IsClosedByServerAsync());
    }

    [Fact]
    public async Task StopAsync_EndsTheTimer()
    {
        await using var host = WorldTestHost.Start(banOptions: Fast);
        await using WorldTestClient client = await host.EnterWorldAsync("STOPPED", "Stopper");
        int id = (await host.Accounts.FindByUsernameAsync("STOPPED"))!.Id;

        BanRecheckFeature feature = host.WorldServices.GetServices<IWorldFeature>().OfType<BanRecheckFeature>().Single();
        await feature.StopAsync();
        host.Bans.AddAccountRow(id, RealNow - 1, RealNow - 1);
        await Task.Delay(500);

        await PingAsync(client);
        Assert.NotNull(host.Registry.Find(id));
    }

    [Fact]
    public async Task ExpiredBansArePurged_OnTheFirstPass_NotOnEveryPass()
    {
        await using var host = WorldTestHost.Start(banOptions: Fast);
        await using WorldTestClient client = await host.EnterWorldAsync("PURGER", "Purger");
        int before = host.Bans.FindBannedAccountsCalls;
        await WorldTestHost.WaitForAsync(() => host.Bans.FindBannedAccountsCalls >= before + 4, "several passes");

        Assert.Equal(1, host.Bans.PurgeCalls); // hourly, not per tick
        await PingAsync(client);
    }

    private static async Task PingAsync(WorldTestClient client)
    {
        await client.SendAsync(WorldOpcode.CmsgPing, [5, 0, 0, 0, 0, 0, 0, 0]);
        byte[] pong = await client.ReadUntilAsync(WorldOpcode.SmsgPong);
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(pong));
    }
}
