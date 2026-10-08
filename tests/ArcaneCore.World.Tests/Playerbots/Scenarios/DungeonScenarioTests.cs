using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Scenarios;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Scenarios;

/// <summary>
/// The <c>dungeon</c> bot scenario (<see cref="DungeonEntryScenario"/>) against the real world handlers on the manual clock: group, the
/// Deadmines entrance trigger, one shared instance bound to the group, party chat inside, the exit trigger. The content is synthetic
/// (<see cref="DeadminesTestContent"/>): the continents plus map 36 and its triggers 78 and 119 with classic-db's teleport rows.
/// </summary>
public sealed class DungeonScenarioTests
{
    [Fact]
    public async Task Dungeon_TwoGroupedBotsShareOneInstance_AndAMemberWhoRelogsInsideComesBackToIt()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(services =>
        {
            DeadminesTestContent.Register(services);
            services.AddSingleton<IOptions<PlayerbotOptions>>(Options.Create(new PlayerbotOptions
            {
                Enabled = true, MaxBots = 8, AllowedMaps = [0, 1, DungeonEntryScenario.Deadmines], Scenarios = { Enabled = true },
            }));
        });

        ScenarioReport report = await world.RunPassingAsync(new DungeonEntryScenario());

        Assert.Contains("the member logs out inside and back in, into the same instance", report.ToString());
        AssertBothBack(world);
    }

    [Fact]
    public async Task Dungeon_WithTheContinentsOnlyAllowed_StillEntersTogether_ButSkipsTheRelogInside()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(DeadminesTestContent.Register);

        ScenarioReport report = await world.RunPassingAsync(new DungeonEntryScenario());

        Assert.DoesNotContain("logs out inside", report.ToString());
        AssertBothBack(world);
    }

    [Fact]
    public async Task Dungeon_WithoutTheDungeonContent_FailsAtTheNamedContentStep()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(DeadminesTestContent.RegisterContinentsOnly);

        ScenarioReport report = await world.RunAsync(new DungeonEntryScenario());

        Assert.False(report.Passed);
        Assert.Contains("the Deadmines content is there", report.ToString());
        Assert.True(report.ToString().Contains("map 36 is not a known dungeon", StringComparison.Ordinal), report.ToString());
    }

    /// <summary>
    /// A step failing inside the instance must not leave the shared scenario bots there: under the default AllowedMaps [0, 1] a bot saved
    /// on map 36 is refused at its next login ('login-refused'), and every pair scenario (Scnalpha and Scnbeta) would stop working. Here
    /// the dungeon admits one player (map_template player_limit 1), so the member is refused at the entrance (vmangos
    /// DungeonMap::CanEnter, TRANSFER_ABORT_MAX_PLAYERS) while the leader is already inside. The run fails at that step, both bots end on
    /// Eastern Kingdoms with no group, and the next pair scenario runs.
    /// </summary>
    [Fact]
    public async Task Dungeon_AStepFailingInside_BringsBothBotsOut_AndThePairScenariosStillRun()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(DeadminesTestContent.RegisterOnePlayerOnly);

        ScenarioReport report = await world.RunAsync(new DungeonEntryScenario());

        Assert.False(report.Passed, report.ToString());
        Assert.Equal($"{PlayerbotScenarioCatalog.BotB} takes area trigger {DungeonEntryScenario.EntranceTrigger} to map {DungeonEntryScenario.Deadmines}",
            report.FailedStep);
        Assert.Contains(report.Steps, s => s is { Name: $"cleanup: bring {PlayerbotScenarioCatalog.BotA} out of the dungeon", Passed: true });
        Assert.Contains(report.Steps, s => s is { Name: "cleanup: disband the group", Passed: true });
        AssertBothBack(world, report);
        ArcaneCore.World.Social.SocialFeature social = world.Services.GetRequiredService<ArcaneCore.World.Social.SocialFeature>();
        Assert.Equal(0, await world.Host.World.InvokeAsync(() => social.Context.Groups.Groups.Count));

        await world.RunPassingAsync(new GroupChatScenario());
    }

    // Both bots left through the exit onto Eastern Kingdoms (the scenario also disbanded the group).
    private static void AssertBothBack(ScenarioTestWorld world, ScenarioReport? report = null)
    {
        foreach (string name in new[] { PlayerbotScenarioCatalog.BotA, PlayerbotScenarioCatalog.BotB })
        {
            PlayerbotStatus status = world.Bots.Snapshot().Single(s => s.Name == name);
            Assert.True(status.MapId == 0u, $"{name} is on map {status.MapId}\n{report}");
        }
    }
}

/// <summary>
/// Synthetic Deadmines content: map 36 (a five-player dungeon whose ghost entrance is the Westfall entrance), the entrance trigger 78 on
/// Eastern Kingdoms (the box of classic-db's areatrigger row as the map data tests use it) and the exit trigger 119 inside, with
/// classic-db's teleports (78: level 10, to (-16.4, -383.07, 61.78); 119: back to (-11208.7, 1675.9, 24.57)).
/// </summary>
internal static class DeadminesTestContent
{
    public static void Register(IServiceCollection services) => services.AddSingleton<IMapDataStore>(new Maps(playerLimit: 10));

    /// <summary>The same content with a dungeon that admits one player: the second bot is refused at the entrance.</summary>
    public static void RegisterOnePlayerOnly(IServiceCollection services) => services.AddSingleton<IMapDataStore>(new Maps(playerLimit: 1));

    /// <summary>A world without the dungeon: only the two continents, no triggers.</summary>
    public static void RegisterContinentsOnly(IServiceCollection services) => services.AddSingleton<IMapDataStore>(new ContinentsOnly());

    private static readonly MapTemplate[] Continents =
    [
        new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""),
        new MapTemplate(1, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Kalimdor", ""),
    ];

    private sealed class ContinentsOnly : IMapDataStore
    {
        public Task<MapContent> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new MapContent(Continents, [], [], [], []));
    }

    private sealed class Maps(uint playerLimit) : IMapDataStore
    {
        public Task<MapContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new MapContent(
            [
                .. Continents,
                new MapTemplate(DungeonEntryScenario.Deadmines, 0, MapType.Instance, 1581, playerLimit, 0, 0, -11208.4f, 1672.3f, "The Deadmines", ""),
            ],
            [],
            [
                new AreaTriggerTemplate(DungeonEntryScenario.EntranceTrigger, 0, -11208.6f, 1679.6f, 24.6f, 0f, 5f, 10f, 8f, 1.5f, "Deadmines Entrance"),
                new AreaTriggerTemplate(DungeonEntryScenario.ExitTrigger, DungeonEntryScenario.Deadmines, -14.6f, -390.5f, 62.4f, 5f, 0, 0, 0, 0, "Deadmines Exit"),
            ],
            [
                new AreaTriggerTeleport(DungeonEntryScenario.EntranceTrigger, "Deadmines - Entering", "You must be at least level 10 to enter.", 10,
                    DungeonEntryScenario.Deadmines, -16.4f, -383.07f, 61.78f, 1.9f),
                new AreaTriggerTeleport(DungeonEntryScenario.ExitTrigger, "Deadmines - Exiting", "", 0, 0, -11208.7f, 1675.9f, 24.5733f, 4.71239f),
            ],
            []));
    }
}
