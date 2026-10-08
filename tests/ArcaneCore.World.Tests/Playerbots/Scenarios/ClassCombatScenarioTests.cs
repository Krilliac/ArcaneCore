using ArcaneCore.Kernel.Quests;
using ArcaneCore.World.Playerbots.Scenarios;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Scenarios;

/// <summary>
/// The class combat scenarios (<see cref="ClassCombatScenario"/>, src/ArcaneCore.World/Playerbots/Scenarios/ScenarioCombat.cs):
/// one bot per class, created and taught its first spells through the ordinary paths (<see cref="ScenarioClassContent"/> makes
/// every race and class creatable), handed to its own brain beside the scenario wolf, kills it the way its class fights. The
/// world runs without the scenario quest so the alliance bots do not walk off to the quest giver first.
/// </summary>
public sealed class ClassCombatScenarioTests
{
    private static readonly string[] ClassNames = ["warrior", "paladin", "hunter", "rogue", "priest", "shaman", "mage", "warlock", "druid"];

    public static TheoryData<string> Classes => new(ClassNames);

    [Theory]
    [MemberData(nameof(Classes))]
    public async Task ABotOfEachClass_KillsTheWolf_WithItsClassSpells(string className)
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(WithoutQuests);
        await world.UseFlatFloorAsync();
        IPlayerbotScenario scenario = PlayerbotScenarioCatalog.Find(world.Services, "combat-" + className)
            ?? throw new Xunit.Sdk.XunitException("combat-" + className + " is not in the catalog");
        await world.RunPassingAsync(scenario);
    }

    [Fact]
    public async Task AWolfAnotherPlayerTagged_IsNotTakenFromThem()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(WithoutQuests);
        await world.UseFlatFloorAsync();
        await world.RunPassingAsync(new CombatTappedWolfScenario());
    }

    [Fact]
    public void EveryClassHasACombatScenario()
    {
        IReadOnlyList<IPlayerbotScenario> all = PlayerbotScenarioCatalog.All(new ServiceCollection().BuildServiceProvider());
        foreach (string className in ClassNames)
            Assert.Contains(all, s => s.Name == "combat-" + className && s is ClassCombatScenario);
        Assert.Contains(all, s => s.Name == "combat-tapped-wolf");
    }

    internal static void WithoutQuests(IServiceCollection services) => services.AddSingleton<IQuestContentStore>(new NoQuests());

    private sealed class NoQuests : IQuestContentStore
    {
        public Task<QuestContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new QuestContent([], [], []));
    }
}
