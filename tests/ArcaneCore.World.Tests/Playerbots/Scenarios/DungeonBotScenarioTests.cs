using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Scenarios;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Scenarios;

/// <summary>
/// The <c>dungeon-bot-*</c> scenarios (<see cref="ScenarioDungeonBots"/>) on the manual clock against <see cref="DeadminesTestContent"/>
/// (map 36, entrance trigger 78, a graveyard for its zone outside with a spirit healer) and a flat floor, with the DEFAULT
/// AllowedMaps [0, 1]: the dungeon is never listed, so everything the bot does inside rests on <see cref="PlayerbotMapPolicy"/>.
/// Every scenario also leaves the bot alive where it logged in, so the next one can run.
/// </summary>
public sealed class DungeonBotScenarioTests
{
    private static async Task<ScenarioTestWorld> StartAsync()
    {
        ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(DeadminesTestContent.RegisterForBots);
        Assert.Equal([0u, 1u], world.Services.GetRequiredService<IOptions<PlayerbotOptions>>().Value.AllowedMaps);
        await world.Host.World.InvokeAsync(() =>
        {
            DeadminesTestContent.InstallCollision(world.Host.World);
            return true;
        });
        return world;
    }

    [Fact]
    public async Task ABotInsideTheDeadmines_PlansAndWalksARouteThere()
    {
        await using ScenarioTestWorld world = await StartAsync();

        ScenarioReport report = await world.RunPassingAsync(new DungeonBotWalkInsideScenario());

        Assert.Contains($"{ScenarioDungeonBots.BotName} arrived inside the dungeon", report.ToString());
        AssertBack(world, report);
    }

    [Fact]
    public async Task ABotWalkingAcrossTheEntranceTrigger_IsTeleportedIn()
    {
        await using ScenarioTestWorld world = await StartAsync();

        ScenarioReport report = await world.RunPassingAsync(new DungeonBotWalkIntoEntranceScenario());

        Assert.Contains($"{ScenarioDungeonBots.BotName} is teleported into map {DungeonEntryScenario.Deadmines}", report.ToString());
        AssertBack(world, report);
    }

    [Fact]
    public async Task ABotKilledInside_ReleasesOutside_WalksInAsAGhost_AndIsResurrectedAtTheEntrance()
    {
        await using ScenarioTestWorld world = await StartAsync();

        ScenarioReport report = await world.RunPassingAsync(new DungeonBotGhostEntranceScenario());

        Assert.Contains($"{ScenarioDungeonBots.BotName} releases and appears outside as a ghost", report.ToString());
        Assert.Contains($"{ScenarioDungeonBots.BotName} has no fault", report.ToString());
        AssertBack(world, report);
    }

    [Fact]
    public async Task ABotWhoseCorpseRunStalls_UsesTheSpiritHealer_AndIsNotQuarantined()
    {
        await using ScenarioTestWorld world = await StartAsync();

        ScenarioReport report = await world.RunPassingAsync(new DungeonBotSpiritHealerScenario());

        Assert.Contains($"{ScenarioDungeonBots.BotName} came back at the spirit healer, not at its body", report.ToString());
        AssertBack(world, report);
    }

    [Fact]
    public async Task TheDungeonBotScenarios_RunOneAfterAnother_OnTheSameBot()
    {
        await using ScenarioTestWorld world = await StartAsync();

        foreach (IPlayerbotScenario scenario in new IPlayerbotScenario[]
        {
            new DungeonBotGhostEntranceScenario(), new DungeonBotSpiritHealerScenario(), new DungeonBotWalkInsideScenario(),
        })
        {
            ScenarioReport report = await world.RunPassingAsync(scenario);
            AssertBack(world, report);
        }
    }

    [Fact]
    public void TheDungeonBotScenarios_AreListedByName()
    {
        IServiceProvider none = new ServiceCollection().BuildServiceProvider();
        foreach (string name in new[] { "dungeon-bot-walk-inside", "dungeon-bot-walk-into-entrance", "dungeon-bot-ghost-entrance", "dungeon-bot-spirit-healer" })
            Assert.NotNull(PlayerbotScenarioCatalog.Find(none, name));
    }

    // The bot is stopped after the run (StopBotsAfterRun) on an allowed map, not left in the dungeon, and not faulted.
    private static void AssertBack(ScenarioTestWorld world, ScenarioReport report)
    {
        PlayerbotStatus status = world.Bots.Snapshot().Single(s => s.Name == ScenarioDungeonBots.BotName);
        Assert.True(status.MapId == 0u, $"{status.Name} is on map {status.MapId}\n{report}");
        Assert.True(status.ErrorCode is null, $"{status.Name}: {status.ErrorCode}\n{report}");
    }
}
