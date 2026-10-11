using ArcaneCore.Data;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Playerbots.Qa;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>
/// The spell coverage of the bots against a real imported world (ARCANECORE_TEST_WORLD_DB: the spell table, the class trainers) and the full world's
/// handler registries. It prints the numbers and writes the coverage section to <c>%TEMP%/arcanecore-qa-spell-coverage.md</c>; it asserts only that the
/// coverage saw a real spell book, since the gaps are what the report is for.
/// </summary>
public sealed class PlayerbotQaSpellCoverageRealContentTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _directory = QaData.NewTempDirectory();

    public void Dispose() => QaData.DeleteQuietly(_directory);

    [RealWorldContentFact]
    public async Task TheBotsSpellCoverage_OnTheImportedWorld()
    {
        string world = Path.Combine(_directory, "world.db");
        File.Copy(Environment.GetEnvironmentVariable(RealWorldContentFactAttribute.Variable)!, world);
        var values = new Dictionary<string, string?>
        {
            ["Database:World:Provider"] = "Sqlite",
            ["Database:World:ConnectionString"] = $"Data Source={world};Pooling=False",
        };
        // Optional: a server profile's appsettings.json (ARCANECORE_QA_COVERAGE_CONFIG). Only the content sections are taken (the DBC paths the
        // skills, talents, enchanting and game object features need to install their spell handlers); never its databases, ports or bots.
        if (Environment.GetEnvironmentVariable("ARCANECORE_QA_COVERAGE_CONFIG") is { Length: > 0 } profile && File.Exists(profile))
        {
            string[] sections = ["Skills", "Stats", "Progression", "Creatures", "Quests", "GameObjects", "ItemSets", "ItemRandomProperties", "Enchanting", "ClientData", "Talents"];
            foreach (KeyValuePair<string, string?> pair in new ConfigurationBuilder().AddJsonFile(profile).Build().AsEnumerable())
            {
                if (sections.Any(section => pair.Key.StartsWith(section + ":", StringComparison.OrdinalIgnoreCase))) values[pair.Key] = pair.Value;
            }

            output.WriteLine($"content sections from {profile}");
        }

        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            ServiceDescriptor appearance = services.Last(d => d.ServiceType == typeof(IWorldDataStore));
            services.AddSingleton(config);
            services.AddWorldDatabase(config);
            services.Add(appearance);
            services.AddSingleton<IOptions<PlayerbotQaOptions>>(Options.Create(new PlayerbotQaOptions { Directory = Path.Combine(_directory, "qa") }));
            services.AddSingleton(new PlayerbotQaSignalBus(1024));
        });
        var feature = host.WorldServices.GetRequiredService<PlayerbotQaFeature>();
        for (int i = 0; i < 3; i++) await host.OnWorldAsync(() => 0); // let the installs features post to the world thread run (production computes minutes later)
        int spells = host.WorldServices.GetRequiredService<SpellFeature>().System.Store.Count;

        Assert.True(feature.TryComputeSpellCoverage());

        QaSpellCoverageReport coverage = feature.SpellCoverage!;
        output.WriteLine($"spell table: {spells} spells; trainer rows: {coverage.TrainerRows}");
        output.WriteLine(coverage.Describe());
        foreach (QaCoverageOffender o in coverage.TopOffenders(20))
        {
            output.WriteLine($"  {QaSpellGaps.Describe(o.Code, o.Detail)}: {o.Spells} spells ({o.RotationSpells} rotation)");
        }

        foreach (QaCoverageGap g in coverage.Gaps.Where(g => g.Rotation).OrderBy(g => g.Spell))
        {
            output.WriteLine($"  rotation gap: {g.Spell} {g.Name} {g.Rank} [{QaFindingView.ClassNamesOf(g.ClassMask)}] #{g.EffectIndex} {g.EffectName}: {QaSpellGaps.Describe(g.Code, g.Detail)}");
        }

        await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "arcanecore-qa-spell-coverage.md"), coverage.RenderMarkdown(1000));
        Assert.True(coverage.RotationCastable > 50, $"only {coverage.RotationCastable} rotation spells resolved");
        Assert.True(coverage.TrainerRows > 0);
    }
}
