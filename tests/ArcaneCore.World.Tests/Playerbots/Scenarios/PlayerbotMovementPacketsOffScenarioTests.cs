using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Scenarios;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;

namespace ArcaneCore.World.Tests.Playerbots.Scenarios;

/// <summary>
/// The bot scenarios that lean hardest on movement — dungeon corpse runs and stall recovery, class combat with ranged positioning,
/// a quest walked from the giver to the mobs and back — with <c>World:Playerbots:MovementPackets</c> off: the server moves the bots
/// itself and they must still pass.
/// </summary>
public sealed class PlayerbotMovementPacketsOffScenarioTests
{
    /// <summary>The scenario world's own playerbot options (<see cref="ScenarioTestWorld.StartAsync"/>) with the movement packets off.</summary>
    private static void PacketsOff(IServiceCollection services)
        => services.AddSingleton<IOptions<PlayerbotOptions>>(Options.Create(new PlayerbotOptions
        {
            Enabled = true, MaxBots = 8, AllowedMaps = [0, 1], Scenarios = { Enabled = true }, MovementPackets = false,
        }));

    private static async Task<ScenarioTestWorld> StartAsync(Action<IServiceCollection>? content = null)
    {
        ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(services =>
        {
            content?.Invoke(services);
            PacketsOff(services);
        });
        Assert.False(world.Services.GetRequiredService<IOptions<PlayerbotOptions>>().Value.MovementPackets);
        return world;
    }

    [Fact]
    public async Task DungeonGhostRun_AndStalledCorpseRun_Recover()
    {
        await using ScenarioTestWorld world = await StartAsync(DeadminesTestContent.RegisterForBots);
        await world.Host.World.InvokeAsync(() =>
        {
            DeadminesTestContent.InstallCollision(world.Host.World);
            return true;
        });

        ScenarioReport ghost = await world.RunPassingAsync(new DungeonBotGhostEntranceScenario());
        Assert.Contains($"{ScenarioDungeonBots.BotName} has no fault", ghost.ToString());
        ScenarioReport healer = await world.RunPassingAsync(new DungeonBotSpiritHealerScenario());
        Assert.Contains($"{ScenarioDungeonBots.BotName} came back beside the spirit healer, not at its body", healer.ToString());
        await world.RunPassingAsync(new DungeonBotWalkIntoEntranceScenario());
    }

    [Theory]
    [InlineData("warrior")]
    [InlineData("hunter")]
    [InlineData("mage")]
    public async Task ClassCombat_KillsTheWolf(string className)
    {
        await using ScenarioTestWorld world = await StartAsync(ClassCombatScenarioTests.WithoutQuests);
        await world.UseFlatFloorAsync();
        IPlayerbotScenario scenario = PlayerbotScenarioCatalog.Find(world.Services, "combat-" + className)
            ?? throw new Xunit.Sdk.XunitException("combat-" + className + " is not in the catalog");
        await world.RunPassingAsync(scenario);
    }

    [Fact]
    public async Task AFreshBot_CompletesTheKillQuest()
    {
        await using ScenarioTestWorld world = await StartAsync();
        world.Services.GetRequiredService<QuestNpcFeature>().Options.OrdinaryRewardQuestIds = [];
        await world.Host.OnWorldAsync(() =>
        {
            WorldCollision.Of(world.Host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new OpenPathfinder());
            return true;
        });

        ScenarioReport report = await world.RunPassingAsync(new ProgressionQuestScenario(KillQuest, GiverEntry));
        Assert.Contains(report.Steps, step => step.Name == "the bot completes and turns it in" && step.Passed);
    }
}
