using System.Diagnostics.Metrics;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Data.World.Instances;
using ArcaneCore.Kernel.Instances;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Instances;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Playerbots.Qa;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.GroupTestWorld;
using static ArcaneCore.World.Tests.Playerbots.Groups.RunFixtures;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// Raid role quotas and a raid without its healers. Split from the former <c>PlayerbotRunWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotRunWorldTestBase"/>.
/// </summary>
public sealed class PlayerbotRunRaidQuotasWorldTests : PlayerbotRunWorldTestBase
{
    [Fact]
    public async Task ARaid_TakesItsRoleQuotas_OneTankThreeHealers_FromElevenBots()
    {
        await using GroupTestWorld world = await StartAsync(options => options.MaxBots = 12);
        var raid = await AddRaidAsync(world, "Rq", warriors: 1, priests: 3, mages: 7);
        await GiveAmuletAsync(world, raid);
        world.Coordinator.RunRoll = () => 0d;

        Assert.True(await world.RunUntilAsync(30_000, () => world.Coordinator.Groups.Any(g => g.Run is not null)), await world.TraceAsync());

        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single();
        Assert.Equal(10, group.Members.Count);
        Assert.Equal(1, group.Members.Count(m => m.Role == PlayerbotGroupRole.Tank));
        Assert.Equal(3, group.Members.Count(m => m.Role == PlayerbotGroupRole.Healer));
        Assert.Equal(6, group.Members.Count(m => m.Role == PlayerbotGroupRole.Damage));
        Assert.Equal(2, group.Members.Select(m => m.SubGroup).Distinct().Count());
        Assert.Equal(raid[0].Id, group.LeaderBotId); // the warrior tanks and leads
        Assert.Equal(PlayerbotGroupRole.Tank, group.Find(raid[0].Id)!.Role);
        Assert.Single(raid, r => group.Find(r.Id) is null); // the seventh mage stays free
    }

    [Fact]
    public async Task ARaid_WithoutItsHealers_IsNotFormed_EvenWhenEveryBotMeetsTheRequirements()
    {
        await using GroupTestWorld world = await StartAsync();
        var raid = await AddRaidAsync(world, "Rh", warriors: 1, priests: 2, mages: 7);
        await GiveAmuletAsync(world, raid);
        world.Coordinator.RunRoll = () => 0d;

        await world.RunAsync(30_000);

        Assert.Empty(world.Coordinator.Groups);
        Assert.Equal(0, world.Coordinator.Totals.Formed);
    }
}
