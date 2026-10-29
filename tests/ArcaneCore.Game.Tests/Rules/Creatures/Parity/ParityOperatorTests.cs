using System.Globalization;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.World.Rules;
using ArcaneCore.Kernel.WorldData.Creatures;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;
using static ArcaneCore.Game.Tests.Rules.Creatures.Parity.ParityFixtures;

namespace ArcaneCore.Game.Tests.Rules.Creatures.Parity;

/// <summary>
/// Tier (b) of the EventAI parity check (inventory 14), the operator suite: every creature entry of a real database that has EventAI rows runs the
/// same scripted fight through the EventAI executor and the rule host. It needs a classic-db dump, which is not committed. With
/// <c>ARCANECORE_CLASSICDB_DUMP</c> unset the test is reported as skipped ("not run"), never as passed; with it set to a missing file it fails.
/// </summary>
public sealed class ParityOperatorTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcanecore-parity-b-" + Guid.NewGuid().ToString("N"));

    [OperatorDumpFact]
    public async Task EveryRealCreatureEntry_RunsTheSameThroughBothExecutors()
    {
        string dump = Environment.GetEnvironmentVariable(OperatorDumpFactAttribute.Variable)!;
        Assert.True(File.Exists(dump), $"{OperatorDumpFactAttribute.Variable} is set but '{dump}' does not exist");

        Directory.CreateDirectory(_directory);
        string database = Path.Combine(_directory, "world.db");
        var text = new StringWriter(CultureInfo.InvariantCulture);
        int exit = await ContentImporterCli.RunAsync(["import", dump, "--database", database], text, text, CancellationToken.None);
        Assert.True(exit == 0, text.ToString());

        CreatureAiContent source;
        var options = new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={database};Pooling=False").Options;
        await using (var db = new WorldDbContext(options))
        {
            source = await EventAiRuleImporter.LoadSourceAsync(db);
        }

        uint[] entries = [.. source.AllEvents.Where(e => e.CreatureId != 0).Select(e => e.CreatureId).Distinct().Order()];
        Assert.NotEmpty(entries);
        var failures = new List<string>();
        int compared = 0;
        foreach (uint entry in entries)
        {
            ParityScenario scenario = ScenarioFor(source, entry);
            ParityRun old = ParityRunner.Run(scenario, ParitySide.Old);
            ParityRun engine = ParityRunner.Run(scenario, ParitySide.Engine);
            IReadOnlyList<ParityDifference> differences = ParityDiff.Compare(old.Log, engine.Log);
            (IReadOnlyList<ParityDifference> unexplained, _) = ParityDiff.Apply(scenario.Name, differences, ParityExceptions.Approved);
            compared++;
            if (unexplained.Count > 0)
            {
                failures.Add($"{scenario.Name}: {unexplained.Count} difference(s), first {unexplained[0]}");
            }
        }

        output.WriteLine($"{compared} creature entr(ies) compared, {failures.Count} differ");
        foreach (string failure in failures)
        {
            output.WriteLine(failure);
        }

        Assert.True(failures.Count == 0, $"{failures.Count} of {compared} entries differ; first: {string.Join("; ", failures.Take(10))}");
    }

    /// <summary>The scripted fight of one real entry: the entry's own rows on the fixture creature, a pull, and the creature wounded in steps.</summary>
    private static ParityScenario ScenarioFor(CreatureAiContent source, uint entry)
    {
        CreatureAiEvent[] rows = [.. source.GetEvents(entry).Select(r => r with { CreatureId = ParityScenario.Entry })];
        var texts = new Dictionary<int, CreatureAiText>();
        var broadcast = new Dictionary<uint, BroadcastText>();
        var summons = new Dictionary<uint, CreatureAiSummon>();
        foreach (CreatureAiAction action in rows.SelectMany(r => r.Actions).Where(a => !a.IsEmpty))
        {
            switch (action.Type)
            {
                case 1:
                    foreach (int id in new[] { action.Param1, action.Param2, action.Param3 })
                    {
                        Collect(source, id, texts, broadcast);
                    }

                    break;
                case 54:
                    Collect(source, action.Param1, texts, broadcast);
                    break;
                case 32 when source.FindSummon((uint)action.Param3) is { } summon:
                    summons[summon.Id] = summon;
                    break;
            }
        }

        return new ParityScenario
        {
            Name = $"real-{entry}",
            Rows = rows,
            Texts = [.. texts.Values],
            Broadcast = [.. broadcast.Values],
            Summons = [.. summons.Values],
            Steps = [Wait(1000), Pull(), Wait(4000), HealthTo(60), Wait(3000), HealthTo(30), Wait(3000), HealthTo(10), Wait(3000)],
        };
    }

    private static void Collect(CreatureAiContent source, int id, Dictionary<int, CreatureAiText> texts, Dictionary<uint, BroadcastText> broadcast)
    {
        if (id < 0 && source.FindText(id) is { } text)
        {
            texts[id] = text;
        }
        else if (id > 0 && source.BroadcastTexts.Find((uint)id) is { } line)
        {
            broadcast[(uint)id] = line;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class OperatorDumpFactAttribute : FactAttribute
    {
        public const string Variable = "ARCANECORE_CLASSICDB_DUMP";

        public OperatorDumpFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
            {
                Skip = $"Not run: set {Variable} to the classic-db z2815 dump (.sql or .sql.gz) to compare every real creature entry.";
            }
        }
    }
}
