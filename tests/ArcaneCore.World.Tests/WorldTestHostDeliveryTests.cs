using System.Diagnostics;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.World.Tests;

/// <summary>
/// The manual-clock delivery barrier (<see cref="WorldTestHost.SettleAsync"/>) waits until every packet the clients sent has been dispatched by a session.
/// A client whose session was kicked or closed can never have its packets dispatched: the barrier names that client at once instead of
/// waiting out the whole hang guard (120 s by default) and reporting only totals.
/// </summary>
[Collection("World tick load")]
public sealed class WorldTestHostDeliveryTests
{
    [Fact]
    public async Task APacketSentAfterTheSessionWasKicked_FailsTheBarrierAtOnce_NamingTheClient()
    {
        await using WorldTestHost host = WorldTestHost.Start(clock: TestClock.Manual());
        await using WorldTestClient kicked = await host.EnterWorldAsync("KICKEDACCT", "Kickedone");
        await host.SettleAsync();
        await KickAsync(host, kicked, "KICKEDACCT");

        await SendIgnoringAClosedSocketAsync(kicked);
        var wall = Stopwatch.StartNew();
        WorldHangException ex = await Assert.ThrowsAsync<WorldHangException>(() => host.StepAsync(1, hangGuard: TimeSpan.FromSeconds(30)));

        Assert.True(wall.Elapsed < TimeSpan.FromSeconds(10), $"the barrier waited {wall.Elapsed.TotalSeconds:F1} s");
        Assert.Contains(kicked.LocalEndpoint!, ex.Message);
        Assert.Contains("KICKEDACCT", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("session ended", ex.Message);
    }

    [Fact]
    public async Task AKickedClientThatSentNothingMore_DoesNotFailTheBarrier_AndAHealthyClientStillDelivers()
    {
        await using WorldTestHost host = WorldTestHost.Start(clock: TestClock.Manual());
        await using WorldTestClient kicked = await host.EnterWorldAsync("KICKEDTWO", "Kickedtwo");
        await using WorldTestClient healthy = await host.EnterWorldAsync("HEALTHYACCT", "Healthy");
        await kicked.SendAsync(WorldOpcode.CmsgPlayedTime, []);
        await host.SettleAsync(); // the kicked client's packet is dispatched before the kick
        await KickAsync(host, kicked, "KICKEDTWO");

        await healthy.SendAsync(WorldOpcode.CmsgPlayedTime, []);
        await host.StepAsync(1, hangGuard: TimeSpan.FromSeconds(30));

        await healthy.ReadUntilAsync(WorldOpcode.SmsgPlayedTime);
    }

    private static async Task KickAsync(WorldTestHost host, WorldTestClient client, string account)
    {
        int id = (await host.Accounts.FindByUsernameAsync(account))!.Id;
        host.Registry.Find(id)!.Kick();
        Assert.True(await client.IsClosedByServerAsync());
        await WorldTestHost.WaitForAsync(() => host.Registry.Find(id) is null, "the kicked session to be unregistered");
    }

    /// <summary>The server has closed the socket; the write may or may not fail locally, and the sent count is taken before it either way.</summary>
    private static async Task SendIgnoringAClosedSocketAsync(WorldTestClient client)
    {
        try
        {
            await client.SendAsync(WorldOpcode.CmsgPlayedTime, []);
        }
        catch (IOException)
        {
        }
    }
}
