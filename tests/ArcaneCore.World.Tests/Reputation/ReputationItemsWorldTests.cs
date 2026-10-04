using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Reputation;
using ArcaneCore.World.Net;
using ArcaneCore.World.Reputation;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Reputation;

/// <summary>The item reputation requirement answers from the live standing after a real login.</summary>
public sealed class ReputationItemsWorldTests
{
    private const uint BootyBay = 21;

    [Fact]
    public async Task ItemRequirement_FollowsTheLiveRank_WithoutARelog()
    {
        ReputationTestServices.Current.Value = new MemoryReputationStore();
        WorldTestHost host;
        try { host = WorldTestHost.Start(); }
        finally { ReputationTestServices.Current.Value = null; }

        await using (host)
        {
            await using WorldTestClient client = await host.EnterWorldAsync("REPITEM", "Repitem");
            Assert.Equal((uint)ReputationRank.Neutral, await Rank(host));

            await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Repitem")!;
                var feature = ((WorldSession)player.Session).Services.GetRequiredService<ReputationFeature>();
                Assert.True(feature.Service.ModifyReputation(player, BootyBay, 21000)); // Revered starts at 21000
            });
            Assert.Equal((uint)ReputationRank.Revered, await Rank(host));
        }
    }

    private static Task<uint> Rank(WorldTestHost host)
        => host.PlayerStateAsync("Repitem", p => p.Inventory.Requirements.ReputationRank(p.Inventory, BootyBay));

    [Fact]
    public async Task WithoutFactionData_TheFailClosedDefaultStays()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("REPITEM2", "Repitemtwo");
        Assert.IsNotType<ReputationItemRequirements>(await host.PlayerStateAsync("Repitemtwo", p => p.Inventory.Requirements));
        Assert.Equal(3u, await host.PlayerStateAsync("Repitemtwo", p => p.Inventory.Requirements.ReputationRank(p.Inventory, BootyBay)));
    }
}
