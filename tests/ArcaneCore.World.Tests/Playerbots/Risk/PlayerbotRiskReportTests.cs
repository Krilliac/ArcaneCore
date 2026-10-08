using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Risk;

/// <summary>
/// The risk decision is where an operator looks: the end of a bot's <c>.playerbot status</c> / <c>list</c> line and a
/// <c>BOTINSPECT</c> line of <c>.playerbot inspect</c>. The scenario world's wolf stands 60 yards from the start.
/// </summary>
public sealed class PlayerbotRiskReportTests
{
    [Fact]
    public async Task TheRiskDecision_ShowsInStatusAndInspect()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(services =>
            services.AddSingleton<IPlayerbotService>(sp => sp.GetRequiredService<ManagedPlayerbotFeature>()));
        Guid id = Assert.IsType<Guid>((await world.Bots.CreateAsync("Riskreport", 1, 1)).BotId);
        Assert.True((await world.Bots.StartAsync(id.ToString())).Success);
        Assert.True(await world.Host.World.AdvanceClockUntilAsync(60_000,
            () => world.Bots.Snapshot().Any(s => s.BotId == id && s.Risk?.Contains("decision=", StringComparison.Ordinal) == true
                && s.Risk != "decision=none")), "no risk decision in the bot's status");

        PlayerbotInspection inspection = (await world.Bots.InspectAsync("Riskreport"))!;
        Assert.Contains("decision=", inspection.Risk);

        await using WorldTestClient admin = await world.EnterWorldAsync("RISKADMIN", "Riskadmin", AccountSecurity.Administrator);
        await admin.CollectAsync();
        await admin.SendChatAsync(ChatType.Say, Language.Common, ".playerbot list");
        string line;
        do line = (await admin.ReadChatAsync()).Text;
        while (!line.Contains("Riskreport", StringComparison.Ordinal));
        Assert.Matches(@"error=\S+ .*decision=", line);

        await admin.SendChatAsync(ChatType.Say, Language.Common, ".playerbot inspect Riskreport");
        do line = (await admin.ReadChatAsync()).Text;
        while (!line.Contains("decision=", StringComparison.Ordinal));
        Assert.StartsWith("BOTINSPECT ", line);
    }
}
