using ArcaneCore.Data.Characters.Talents;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Talents;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots.Scenarios;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Scenarios;

/// <summary>
/// The <c>talent-lifecycle</c> bot scenario (<see cref="TalentLifecycleScenario"/>) against the real world handlers on the manual clock,
/// with the SQLite characters database, a synthetic talent catalog (the shape of the talent world tests' fixture) and a warrior class
/// trainer spawned beside the human start, whose gossip menu offers GOSSIP_OPTION_UNLEARNTALENTS.
/// </summary>
public sealed class TalentScenarioTests
{
    public const uint T1R1 = 20001, T1R2 = 20002, T1R3 = 20003;
    public const uint Row1R1 = 20011;
    public const uint MageR1 = 20901;
    public const uint TrainerEntry = 990030;
    public const uint TrainerSpawn = 990030;

    /// <summary>Talent 1: a warrior first-row talent of three ranks; talent 2: a second-row talent of its tree; talent 9: another class.</summary>
    public static TalentCatalog Catalog() => new(
        [new TalentTabRecord(1, 1u << 0, 0), new TalentTabRecord(2, 1u << 7, 1)],
        [
            new TalentRecord(1, 1, 0, 0, [T1R1, T1R2, T1R3, 0, 0], 0, 0, 0),
            new TalentRecord(2, 1, 1, 0, [Row1R1, 0, 0, 0, 0], 0, 0, 0),
            new TalentRecord(9, 2, 0, 0, [MageR1, 0, 0, 0, 0], 0, 0, 0),
        ]);

    /// <summary>The catalog, the trainer and its unlearn gossip line.</summary>
    internal static void Register(IServiceCollection services)
    {
        services.AddSingleton(Catalog());
        RegisterTrainer(services);
    }

    /// <summary>The warrior trainer three yards west of the start, without a talent catalog.</summary>
    internal static void RegisterTrainer(IServiceCollection services)
    {
        ICreatureDataStore inner = (ICreatureDataStore)services.Last(d => d.ServiceType == typeof(ICreatureDataStore)).ImplementationInstance!;
        services.AddSingleton<ICreatureDataStore>(new WithTrainer(inner));
        services.AddSingleton<INpcContentStore>(new UnlearnGossip());
    }

    [Fact]
    public async Task TalentLifecycle_LearnTierGateRespecAndRelog_Pass()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(Register);

        ScenarioReport report = await world.RunPassingAsync(new TalentLifecycleScenario());

        Assert.Contains(report.Steps, s => s.Name == $"reach level {TalentLifecycleScenario.Level} through the ordinary level-up and get its points" && s.Passed);
        Assert.Contains(report.Steps, s => s.Name.StartsWith("second-row talent 2 is refused", StringComparison.Ordinal) && s.Passed);
        Assert.Contains(report.Steps, s => s is { Name: "after a relog the talent, the points and the respec price are kept", Passed: true });

        // The paid respec reached the characters database: the first price (1 gold) moves the multiplier to 1.
        int id = (await world.WithScopeAsync(sp => sp.GetRequiredService<ICharacterStore>().GetAllIdentitiesAsync()))
            .Single(c => c.Name.Equals(TalentLifecycleScenario.Bot, StringComparison.OrdinalIgnoreCase)).Id;
        CharacterTalentState? stored = await world.WithScopeAsync(sp => sp.GetRequiredService<ICharacterTalentStore>().GetAsync(id));
        Assert.Equal(1u, stored?.ResetMultiplier);
    }

    [Fact]
    public async Task TalentLifecycle_RunTwice_StartsCleanAndPassesAgain()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(Register);

        await world.RunPassingAsync(new TalentLifecycleScenario());
        ScenarioReport again = await world.RunPassingAsync(new TalentLifecycleScenario());

        Assert.True(again.Passed);
    }

    [Fact]
    public async Task WithoutATalentCatalog_FailsAtTheNamedContentStep()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(RegisterTrainer);

        ScenarioReport report = await world.RunAsync(new TalentLifecycleScenario());

        Assert.False(report.Passed);
        Assert.Equal(TalentLifecycleScenario.CatalogStep, report.FailedStep);
    }

    [Fact]
    public async Task WithoutATrainer_FailsAtTheNamedContentStep_BeforeTheBotChanges()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(services => services.AddSingleton(Catalog()));

        ScenarioReport report = await world.RunAsync(new TalentLifecycleScenario());

        Assert.False(report.Passed);
        Assert.Equal(TalentLifecycleScenario.TrainerStep, report.FailedStep);
        Assert.DoesNotContain(report.Steps, s => s.Name.StartsWith("reach level", StringComparison.Ordinal));
    }

    [Fact]
    public void TheScenario_IsShipped_UnderItsName()
    {
        Assert.Contains(PlayerbotScenarioCatalog.Shipped, s => s is TalentLifecycleScenario { Name: "talent-lifecycle" });
    }

    private sealed class WithTrainer(ICreatureDataStore inner) : ICreatureDataStore
    {
        public async Task<CreatureContent> LoadAsync(CancellationToken cancellationToken = default)
        {
            CreatureContent content = await inner.LoadAsync(cancellationToken);
            var trainer = new CreatureTemplate
            {
                Entry = TrainerEntry, Name = "Scenario Drill Sergeant", Faction = 12, DisplayIds = [49],
                NpcFlags = (uint)(NpcFlags.Gossip | NpcFlags.Trainer), TrainerType = (uint)TrainerType.Class, TrainerClass = 1,
                MinLevelHealth = 100, MaxLevelHealth = 100, ExtraFlags = Game.Creatures.Creature.ExtraFlagNoAggro,
            };
            var spawn = new CreatureSpawn
            {
                Guid = TrainerSpawn, Entry = TrainerEntry, MapId = 0,
                X = ScenarioTestContent.StartX - 3f, Y = ScenarioTestContent.StartY, Z = ScenarioTestContent.StartZ,
            };

            // The scenario content has templates and spawns only (no waypoints, models or addons).
            return new CreatureContent(
                [.. content.Templates, trainer],
                [.. content.MapsWithSpawns.SelectMany(content.GetSpawns), spawn],
                [], [], []);
        }
    }

    private sealed class UnlearnGossip : INpcContentStore
    {
        public Task<NpcContent> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(NpcContent.Empty with
        {
            GossipMenuOptions =
            [
                new GossipMenuOption
                {
                    MenuId = 0, Id = 0, OptionId = (byte)GossipOption.UnlearnTalents, NpcOptionNpcFlag = (uint)NpcFlags.Trainer,
                    OptionText = "I wish to unlearn my talents.",
                },
            ],
        });
    }
}
