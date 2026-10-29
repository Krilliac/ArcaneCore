using ArcaneCore.Game.Conditions;
using ArcaneCore.Game.Rules;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Reload;
using ArcaneCore.Kernel.Rules;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Reload;
using ArcaneCore.World.Rules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Rules;

/// <summary>
/// The rule engine in the world daemon (design 5.1): <see cref="RuleFeature"/> loads the tables at start, <c>.reload rules</c> swaps a
/// valid set in and rejects a set with any bad row (the current rules stay), and <c>.reload conditions</c> (now reloadable) swaps the
/// conditions table unless a loaded rule names a condition the new table lacks.
/// </summary>
public sealed class RuleWorldTests
{
    private const RuleHostKind WorldTestHostKind = (RuleHostKind)210;

    private static RuleContent Valid(int texts) => new()
    {
        Texts = [.. Enumerable.Range(1, texts).Select(i => new RuleTextRecord { Id = i, ContentDefault = $"text {i}" })],
        Filters = [new RuleSubjectFilterRecord { Id = 1, SharePct = 30 }],
        Goals = [new PlayerbotGoalRecord { GoalId = 1, Name = "quest" }],
    };

    private static RuleContent Broken() => Valid(1) with
    {
        Rules = [new RuleRecord { Id = 5, Host = (byte)RuleHostKind.Creature, EventType = 1 }],
    };

    private sealed class MutableRuleStore(RuleContent content) : IRuleContentStore
    {
        public RuleContent Content { get; set; } = content;

        public Task<RuleContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Content);
    }

    private sealed class MutableConditionStore(params ConditionRecord[] rows) : IConditionContentStore
    {
        public IReadOnlyList<ConditionRecord> Rows { get; set; } = rows;

        public Task<IReadOnlyList<ConditionRecord>> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Rows);
    }

    private static ConditionRecord Event(uint entry) => new(entry, (int)ConditionType.ActiveGameEvent, 1, 0, 0, 0, 0);

    private sealed record Fixture(ReloadCoordinator Coordinator, RuleFeature Rules, ConditionFeature Conditions, MutableRuleStore RuleStore, MutableConditionStore ConditionStore);

    private static Fixture Start(WorldTestHost host, RuleContent rules, params ConditionRecord[] conditions)
    {
        var ruleStore = new MutableRuleStore(rules);
        var conditionStore = new MutableConditionStore(conditions);
        var services = new ServiceCollection().AddSingleton<IRuleContentStore>(ruleStore).AddSingleton<IConditionContentStore>(conditionStore);
        ServiceProvider provider = services.BuildServiceProvider();
        var conditionFeature = new ConditionFeature(provider, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<ConditionFeature>.Instance);
        services.AddSingleton(conditionFeature);
        provider = services.BuildServiceProvider();
        conditionFeature.Attach(host.World);
        var ruleFeature = new RuleFeature(provider, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<RuleFeature>.Instance);
        ruleFeature.Attach(host.World);
        ServiceProvider withFeatures = services.AddSingleton(ruleFeature).BuildServiceProvider();
        var coordinator = new ReloadCoordinator(NullLogger.Instance);
        coordinator.Attach(host.World);
        coordinator.Register(new RuleContentReloadable(withFeatures));
        coordinator.Register(new ConditionContentReloadable(withFeatures));
        return new Fixture(coordinator, ruleFeature, conditionFeature, ruleStore, conditionStore);
    }

    [Fact]
    public async Task Attach_LoadsTheTables_AndRecordsRejectedRows()
    {
        await using var host = WorldTestHost.Start();
        Fixture f = Start(host, Broken());

        Assert.Single(f.Rules.Catalog.Texts);
        RuleRejection rejection = Assert.Single(f.Rules.Catalog.Rejected);
        Assert.Equal("rule 5: host 2 is not registered (registered: 5 world_event)", rejection.ToString());
    }

    [Fact]
    public async Task ReloadRules_ReplacesTheCatalog()
    {
        await using var host = WorldTestHost.Start();
        Fixture f = Start(host, Valid(1));
        f.RuleStore.Content = Valid(3);

        ReloadResult result = await f.Coordinator.ReloadAsync("rules");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Equal(3, f.Rules.Catalog.Texts.Count);
        Assert.Contains("3 texts", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReloadRules_WithABadRow_IsRejected_AndKeepsTheCurrentRules()
    {
        await using var host = WorldTestHost.Start();
        Fixture f = Start(host, Valid(2));
        RuleCatalog before = f.Rules.Catalog;
        f.RuleStore.Content = Broken();

        ReloadResult result = await f.Coordinator.ReloadAsync("rules");

        Assert.Equal(ReloadStatus.Rejected, result.Status);
        Assert.Same(before, f.Rules.Catalog);
        Assert.Contains("rule 5: host 2 is not registered (registered: 5 world_event)", result.Notes);
    }

    [Fact]
    public async Task ReloadConditions_ReplacesTheTable_ForEveryReaderOfTheFeature()
    {
        await using var host = WorldTestHost.Start();
        Fixture f = Start(host, Valid(1), Event(10));
        f.ConditionStore.Rows = [Event(10), Event(11), new ConditionRecord(12, 999, 0, 0, 0, 0, 0)];

        ReloadResult result = await f.Coordinator.ReloadAsync("conditions");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Equal(2, f.Conditions.Table.Count);
        Assert.Same(f.Conditions.Table, f.Conditions.Current.Table);
        Assert.Contains("1 invalid rows were skipped", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReloadConditions_ThatDropsAConditionARuleUses_IsRejected()
    {
        await using var host = WorldTestHost.Start();
        Fixture f = Start(host, Valid(1), Event(10));
        RuleVocabulary vocabulary = RuleVocabulary.Default.WithModule([typeof(WorldRuleTestHost), typeof(WorldPingEvent)]);
        RuleCatalog withCondition = RuleCatalog.Build(new RuleContent
        {
            Rules = [new RuleRecord { Id = 1, Host = (byte)WorldTestHostKind, EventType = 1, ConditionId = 10 }],
            Actions = [new RuleActionRecord { OwnerId = 1, ActionType = 1, TargetType = 1 }],
        }, vocabulary, new RuleBuildOptions { Conditions = f.Conditions.Table });
        Assert.Empty(withCondition.Rejected);
        f.Rules.Replace(withCondition);
        ConditionTable before = f.Conditions.Table;
        f.ConditionStore.Rows = [Event(11)];

        ReloadResult result = await f.Coordinator.ReloadAsync("conditions");

        Assert.Equal(ReloadStatus.Rejected, result.Status);
        Assert.Same(before, f.Conditions.Table);
        Assert.Contains(result.Notes, n => n.Contains("loaded rules reference conditions the new table lacks: 10", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheReloadFeature_RegistersRulesAndConditions_ThroughDiscovery()
    {
        await using var host = WorldTestHost.Start();
        IReadOnlyList<string> names = host.WorldServices.GetRequiredService<ReloadFeature>().Coordinator.Names;

        Assert.Contains("rules", names);
        Assert.Contains("conditions", names);
        Assert.NotNull(host.WorldServices.GetRequiredService<RuleFeature>());
    }

    /// <summary>A host for the conditions test (a module type, so it never collides with a real host later).</summary>
    public sealed class WorldRuleTestHost : RuleHostDefinition
    {
        public override RuleHostKind Kind => WorldTestHostKind;

        public override string Name => "world_test";

        public override string Description => "Test host.";

        public override IReadOnlyList<RuleOwnerKind> Owners { get; } = [RuleOwnerKind.Global];

        public override RuleCapabilities Capabilities => RuleCapabilities.None;
    }

    [RulePrimitive(WorldTestHostKind, 1, "world_test.ping")]
    public sealed class WorldPingEvent : RuleEvent;
}
