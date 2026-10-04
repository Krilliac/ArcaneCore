using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.World.Net;
using ArcaneCore.World.Reputation;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Reputation;

/// <summary>The reputation feature reads the spillover and reward-rate source at startup and applies it (ReputationMgr.cpp:211-243).</summary>
public sealed class ReputationSpilloverWorldTests
{
    private const uint Stormwind = 72;
    private const uint BootyBay = 21;

    [Fact]
    public async Task ASpilloverRowFromTheSource_ReachesTheSpilledFaction_InAWorldProcess()
    {
        ReputationTestServices.Current.Value = new MemoryReputationStore();
        ReputationTestServices.ContentRows.Value = new ReputationContentRows(
            [new ReputationSpilloverTemplate(BootyBay, [new ReputationSpillover(Stormwind, 0.25f, 7)])],
            [new ReputationRewardRate(BootyBay, 1f, 1f, 1f)]);
        WorldTestHost host;
        try { host = WorldTestHost.Start(); }
        finally
        {
            ReputationTestServices.Current.Value = null;
            ReputationTestServices.ContentRows.Value = null;
        }

        await using (host)
        {
            await using WorldTestClient client = await host.EnterWorldAsync("REPSPILL", "Repspill");
            Assert.Equal((1, 1), await host.PlayerStateAsync("Repspill", p => FeatureOf(p).Service.Content is { } c ? (c.SpilloverCount, c.RateCount) : (0, 0)));

            await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Repspill")!;
                Assert.True(FeatureOf(player).Service.ModifyReputation(player, BootyBay, 1000));
            });
            Assert.Equal(250, await host.PlayerStateAsync("Repspill", p => FeatureOf(p).Service.GetReputation(p, Stormwind)));
        }
    }

    [Fact]
    public async Task WithoutASource_NothingSpills()
    {
        ReputationTestServices.Current.Value = new MemoryReputationStore();
        WorldTestHost host;
        try { host = WorldTestHost.Start(); }
        finally { ReputationTestServices.Current.Value = null; }

        await using (host)
        {
            await using WorldTestClient client = await host.EnterWorldAsync("REPSPILL2", "Repspilltwo");
            await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Repspilltwo")!;
                Assert.True(FeatureOf(player).Service.ModifyReputation(player, BootyBay, 1000));
            });
            Assert.Equal(0, await host.PlayerStateAsync("Repspilltwo", p => FeatureOf(p).Service.GetReputation(p, Stormwind)));
        }
    }

    private static ReputationFeature FeatureOf(Player player)
        => ((WorldSession)player.Session).Services.GetRequiredService<ReputationFeature>();
}
