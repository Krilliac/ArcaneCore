using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Kernel.Reload;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.World.Net;
using ArcaneCore.World.Reload;
using ArcaneCore.World.Reputation;
using ArcaneCore.World.Tests.Reputation;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Reload;

/// <summary>
/// <c>.reload reputation_spillover_template</c>, <c>reputation_reward_rate</c> and <c>creature_onkill_reputation</c> (vmangos Chat.cpp:826,
/// 886-887): the tables are re-read, validated against Faction.dbc and swapped in whole.
/// </summary>
public sealed class ReputationReloadTests
{
    private const uint Stormwind = 72;
    private const uint BootyBay = 21;

    private static WorldTestHost Start(MutableReputationContent content)
    {
        ReputationTestServices.Current.Value = new MemoryReputationStore();
        ReputationTestServices.Mutable.Value = content;
        try { return WorldTestHost.Start(); }
        finally
        {
            ReputationTestServices.Current.Value = null;
            ReputationTestServices.Mutable.Value = null;
        }
    }

    private static ReloadCoordinator Coordinator(WorldTestHost host) => host.WorldServices.GetRequiredService<ReloadFeature>().Coordinator;

    private static ReputationService ServiceOf(WorldTestHost host) => host.WorldServices.GetRequiredService<ReputationFeature>().Service;

    [Theory]
    [InlineData("reputation_spillover_template")]
    [InlineData("reputation_reward_rate")]
    public async Task TemplateTables_AreReloadedWhole_AndTheNewSpilloverApplies(string table)
    {
        var content = new MutableReputationContent();
        await using WorldTestHost host = Start(content);
        await using WorldTestClient client = await host.EnterWorldAsync("REPRELOAD", "Repreload");
        ReputationService service = ServiceOf(host);
        Assert.Equal(0, service.Content.SpilloverCount);

        content.Rows = new ReputationContentRows(
            [new ReputationSpilloverTemplate(BootyBay, [new ReputationSpillover(Stormwind, 0.5f, 7)]), new ReputationSpilloverTemplate(9999, [])],
            [new ReputationRewardRate(BootyBay, 1f, 1f, 1f)]);
        ReloadResult result = await Coordinator(host).ReloadAsync(table);

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Contains("1 spillover templates and 1 reward rates", result.Message); // the row for the unknown faction 9999 was dropped
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Repreload")!;
            Assert.True(service.ModifyReputation(player, BootyBay, 1000));
        });
        Assert.Equal(500, await host.PlayerStateAsync("Repreload", p => service.GetReputation(p, Stormwind)));
    }

    [Fact]
    public async Task OnKillRows_AreReloaded_AndARowWithAnUnknownFactionIsSkipped()
    {
        var content = new MutableReputationContent();
        await using WorldTestHost host = Start(content);
        ReputationService service = ServiceOf(host);
        Assert.Equal(0, service.OnKillCount);

        content.OnKill.Add(new ReputationOnKillEntry(1001, BootyBay, 0, 7, false, 10, 0, false, 0, TeamDependent: false));
        content.OnKill.Add(new ReputationOnKillEntry(1002, 4242, 0, 7, false, 10, 0, false, 0, TeamDependent: false)); // faction 4242 is not in Faction.dbc
        ReloadResult result = await Coordinator(host).ReloadAsync("creature_onkill_reputation");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Equal(1, service.OnKillCount);
        Assert.Equal([1001u], service.OnKillEntries.Select(e => e.CreatureEntry));
        Assert.Contains("skipped", result.Message);
    }
}
