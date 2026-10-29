using ArcaneCore.Data.World.Rules;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Game.Tests.Rules.Creatures.Parity;

/// <summary>
/// Tier (a) of the EventAI to rule engine parity check (inventory 14): every fixture scenario runs through the EventAI executor and, on the
/// rows the converter made from the same scenario, through the creature rule host, with the same seed and the same stimuli. The effect logs
/// (casts, texts, emotes, sounds, movement starts, phase and state changes, summons, despawns, in order and with the world time) must match
/// except for the approved <see cref="ParityExceptions"/>. The cost of each world tick is recorded for both sides.
/// </summary>
public sealed class ParityTests(ITestOutputHelper output)
{
    public static TheoryData<string> ScenarioNames()
    {
        var data = new TheoryData<string>();
        foreach (ParityScenario scenario in ParityFixtures.All)
        {
            data.Add(scenario.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public void Scenario_EffectLogs_MatchExceptForApprovedDifferences(string name)
    {
        ParityScenario scenario = ParityFixtures.All.Single(s => s.Name == name);

        ParityRun old = ParityRunner.Run(scenario, ParitySide.Old);
        ParityRun engine = ParityRunner.Run(scenario, ParitySide.Engine);

        output.WriteLine($"{name}: {old.Log.Count} effect line(s) old, {engine.Log.Count} engine; {Cost(old)} / {Cost(engine)}");
        Assert.NotEmpty(old.Log);
        EventAiConversion conversion = engine.Conversion!;
        Assert.Equal(scenario.Unconvertible.Order(), conversion.Issues.Where(i => i.Outcome == EventAiConversionOutcome.Unconvertible).Select(i => i.SourceId).Order());
        Assert.Equal(scenario.Disabled.Order(), conversion.Issues.Where(i => i.Outcome == EventAiConversionOutcome.Disabled).Select(i => i.SourceId).Order());

        IReadOnlyList<ParityDifference> differences = ParityDiff.Compare(old.Log, engine.Log);
        (IReadOnlyList<ParityDifference> unexplained, IReadOnlyList<ParityException> unused) = ParityDiff.Apply(name, differences, ParityExceptions.Approved);
        foreach (ParityDifference difference in differences)
        {
            output.WriteLine(difference.ToString());
        }

        Assert.True(unexplained.Count == 0,
            $"{name}: {unexplained.Count} unexplained difference(s):\n{string.Join('\n', unexplained)}\n--- old log ---\n{string.Join('\n', old.Log)}");
        Assert.True(unused.Count == 0, $"{name}: approved exception(s) that no longer match a difference: {string.Join("; ", unused.Select(u => u.Contains))}");
    }

    private static string Cost(ParityRun run)
        => run.TickMicroseconds.Count == 0
            ? "no ticks"
            : $"{run.TickMicroseconds.Count} tick(s), mean {run.TickMicroseconds.Average():0.0} us, max {run.TickMicroseconds.Max():0.0} us";

    [Fact]
    public void ScenarioNames_AreUnique()
    {
        string[] names = [.. ParityFixtures.All.Select(s => s.Name)];

        Assert.Equal(names.Length, names.Distinct().Count());
        Assert.NotEmpty(names);
    }

    [Fact]
    public void ApprovedExceptions_NameAKnownScenarioAndGiveAReason()
    {
        string[] names = [.. ParityFixtures.All.Select(s => s.Name)];

        Assert.All(ParityExceptions.Approved, e =>
        {
            Assert.Contains(e.Scenario, names);
            Assert.False(string.IsNullOrWhiteSpace(e.Reason), e.Contains);
            Assert.False(string.IsNullOrWhiteSpace(e.Contains));
        });
    }

    [Fact]
    public void Fixtures_CoverEveryConvertibleEventActionAndTargetType()
    {
        CreatureAiEvent[] rows = [.. ParityFixtures.All.SelectMany(s => s.Rows)];
        CreatureAiAction[] actions = [.. rows.SelectMany(r => r.Actions).Where(a => !a.IsEmpty)];

        byte[] events = [.. rows.Select(r => r.EventType).Distinct()];
        byte[] actionTypes = [.. actions.Select(a => a.Type).Distinct()];
        var targets = new HashSet<int>();
        foreach (CreatureAiAction action in actions)
        {
            if (EventAiRuleConverter.Actions.TryGetValue(action.Type, out EventAiRuleConverter.ActionMap? map) && map.TargetParam > 0)
            {
                targets.Add(map.TargetParam switch { 1 => action.Param1, 2 => action.Param2, _ => action.Param3 });
            }
        }

        Assert.Empty(EventAiRuleConverter.Events.Keys.Except(events).Order());
        Assert.Empty(EventAiRuleConverter.Actions.Keys.Except(actionTypes).Order());
        Assert.Empty(EventAiRuleConverter.Targets.Keys.Except(targets).Order());
    }

    [Fact]
    public void EvaluationCost_IsRecordedPerTick_ForBothSides()
    {
        ParityScenario scenario = ParityFixtures.All[0];

        ParityRun old = ParityRunner.Run(scenario, ParitySide.Old);
        ParityRun engine = ParityRunner.Run(scenario, ParitySide.Engine);

        Assert.True(old.TickMicroseconds.Count > 0 && old.TickMicroseconds.Count == engine.TickMicroseconds.Count);
        Assert.All(old.TickMicroseconds.Concat(engine.TickMicroseconds), us => Assert.True(us >= 0));
        output.WriteLine($"{scenario.Name}: old {Cost(old)}; engine {Cost(engine)}");
    }
}
