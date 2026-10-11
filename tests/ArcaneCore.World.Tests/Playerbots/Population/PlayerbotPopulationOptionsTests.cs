using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Reload;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Population;
using ArcaneCore.World.Reload;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Population;

/// <summary><c>World:Playerbots:Population</c>: the defaults, the validation and the live <c>.reload config</c> keys.</summary>
public sealed class PlayerbotPopulationOptionsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcane-population-options-" + Guid.NewGuid().ToString("N"));
    private readonly WorldRuntime _world;

    public PlayerbotPopulationOptionsTests()
    {
        Directory.CreateDirectory(_directory);
        _world = new WorldRuntime(new WorldRuntimeOptions { TickIntervalMs = 5, AutosaveIntervalMs = 0 }, new NullSaveQueue(), NullLogger<WorldRuntime>.Instance);
    }

    public void Dispose()
    {
        _world.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private static PopulationBand Band(int min, int max, int weight) => new() { MinLevel = min, MaxLevel = max, Weight = weight };

    [Fact]
    public void Defaults_AreDisabledAndValid()
    {
        var options = new PlayerbotPopulationOptions();
        Assert.False(options.Enabled);
        Assert.Equal(0, options.TargetOnline);
        options.Validate();
        new PlayerbotOptions().Validate(); // the section is part of the playerbot options
        Assert.Equal(6, options.Bands.Length);
        Assert.Equal([1, 11, 21, 31, 41, 51], options.Bands.Select(b => b.MinLevel));
        Assert.Equal([10, 20, 30, 40, 50, 60], options.Bands.Select(b => b.MaxLevel));
        Assert.All(options.Bands, b => Assert.Equal(1, b.Weight));
        Assert.Equal(new uint[] { 1, 2, 3, 4, 5, 7, 8, 9, 11 }, options.Classes);
        Assert.Equal((60, 80, 200, true), (options.TickP95BudgetMs, options.ResumeBelowPct, options.WindowTicks, options.ParkWhenOverBudget));
        Assert.StartsWith("World:Playerbots:Population: ", Assert.Throws<InvalidOperationException>(new PlayerbotPopulationOptions { TargetOnline = -1 }.Validate).Message);
    }

    [Fact]
    public void Validate_RejectsOverlappingBands_BadRangesAndNoWeight()
    {
        // Overlap: touching ranges (10 and 10) overlap; adjacent ranges (10 and 11) do not.
        AssertRejected(o => o.Bands = [Band(1, 10, 1), Band(10, 20, 1)], "overlapping bands 1-10 and 10-20");
        AssertRejected(o => o.Bands = [Band(1, 30, 1), Band(11, 20, 1)], "overlapping");
        AssertRejected(o => o.Bands = [Band(21, 30, 1), Band(1, 25, 1)], "overlapping"); // order does not matter
        new PlayerbotPopulationOptions { Bands = [Band(1, 10, 1), Band(11, 20, 1)] }.Validate();
        new PlayerbotPopulationOptions { Bands = [Band(21, 30, 1), Band(1, 10, 1)] }.Validate();

        // Level ranges.
        AssertRejected(o => o.Bands = [Band(0, 10, 1)], "levels must satisfy");
        AssertRejected(o => o.Bands = [Band(1, 61, 1)], "levels must satisfy");
        AssertRejected(o => o.Bands = [Band(20, 10, 1)], "levels must satisfy");
        new PlayerbotPopulationOptions { Bands = [Band(1, 60, 1)] }.Validate();
        new PlayerbotPopulationOptions { Bands = [Band(60, 60, 1)] }.Validate();

        // Weights: the sum must be above 0; one weight is 0..1000.
        AssertRejected(o => o.Bands = [Band(1, 10, 0), Band(11, 20, 0)], "weight above 0");
        AssertRejected(o => o.Bands = [Band(1, 10, -1), Band(11, 20, 1)], "must be 0..1000");
        AssertRejected(o => o.Bands = [Band(1, 10, 1001)], "must be 0..1000");
        new PlayerbotPopulationOptions { Bands = [Band(1, 10, 0), Band(11, 20, 1000)] }.Validate();

        // The list itself.
        AssertRejected(o => o.Bands = [], "at least one band");
        AssertRejected(o => o.Bands = null!, "at least one band");
        AssertRejected(o => o.Bands = [.. Enumerable.Range(0, 21).Select(i => Band(i * 2 + 1, i * 2 + 2, 1))], "at most 20");
        new PlayerbotPopulationOptions { Bands = [.. Enumerable.Range(0, 20).Select(i => Band(i * 3 + 1, i * 3 + 2, 1))] }.Validate();
    }

    [Fact]
    public void Validate_RejectsEveryOtherOutOfRangeValue_AtItsBoundary()
    {
        (Action<PlayerbotPopulationOptions> Good, Action<PlayerbotPopulationOptions> Bad, string Key)[] cases =
        [
            (o => o.TargetOnline = PlayerbotOptions.MaxBotsCeiling, o => o.TargetOnline = PlayerbotOptions.MaxBotsCeiling + 1, "TargetOnline"),
            (o => o.TargetOnline = 0, o => o.TargetOnline = -1, "TargetOnline"),
            (o => o.IntervalSeconds = 2, o => o.IntervalSeconds = 1, "IntervalSeconds"),
            (o => o.IntervalSeconds = 600, o => o.IntervalSeconds = 601, "IntervalSeconds"),
            (o => o.TickP95BudgetMs = 5, o => o.TickP95BudgetMs = 4, "TickP95BudgetMs"),
            (o => o.TickP95BudgetMs = 1000, o => o.TickP95BudgetMs = 1001, "TickP95BudgetMs"),
            (o => o.ResumeBelowPct = 10, o => o.ResumeBelowPct = 9, "ResumeBelowPct"),
            (o => o.ResumeBelowPct = 99, o => o.ResumeBelowPct = 100, "ResumeBelowPct"), // 100 would leave no hysteresis band
            (o => o.WindowTicks = 20, o => o.WindowTicks = 19, "WindowTicks"),
            (o => o.WindowTicks = 4096, o => o.WindowTicks = 4097, "WindowTicks"), // the tick ring holds 4096
            (o => o.MaxCreatePerMinute = 0, o => o.MaxCreatePerMinute = -1, "MaxCreatePerMinute"),
            (o => o.MaxCreatePerMinute = 60, o => o.MaxCreatePerMinute = 61, "MaxCreatePerMinute"),
            (o => o.MaxStartsPerInterval = 1, o => o.MaxStartsPerInterval = 0, "MaxStartsPerInterval"),
            (o => o.MaxStartsPerInterval = 100, o => o.MaxStartsPerInterval = 101, "MaxStartsPerInterval"),
            (o => o.MaxParksPerInterval = 1, o => o.MaxParksPerInterval = 0, "MaxParksPerInterval"),
            (o => o.MaxParksPerInterval = 100, o => o.MaxParksPerInterval = 101, "MaxParksPerInterval"),
            (o => o.AllianceSharePct = 0, o => o.AllianceSharePct = -1, "AllianceSharePct"),
            (o => o.AllianceSharePct = 100, o => o.AllianceSharePct = 101, "AllianceSharePct"),
            (o => o.ZoneLevelSlack = 0, o => o.ZoneLevelSlack = -1, "ZoneLevelSlack"),
            (o => o.ZoneLevelSlack = 10, o => o.ZoneLevelSlack = 11, "ZoneLevelSlack"),
            (o => o.ZoneLevelSlackMaxFloor = 1, o => o.ZoneLevelSlackMaxFloor = 0, "ZoneLevelSlackMaxFloor"),
            (o => o.ZoneLevelSlackMaxFloor = 60, o => o.ZoneLevelSlackMaxFloor = 61, "ZoneLevelSlackMaxFloor"),
            (o => o.CreatureLevelMargin = 0, o => o.CreatureLevelMargin = -1, "CreatureLevelMargin"),
            (o => o.CreatureLevelMargin = 10, o => o.CreatureLevelMargin = 11, "CreatureLevelMargin"),
            (o => o.CreatureSurveyRadius = 0, o => o.CreatureSurveyRadius = -1, "CreatureSurveyRadius"),
            (o => o.CreatureSurveyRadius = 300, o => o.CreatureSurveyRadius = 301, "CreatureSurveyRadius"),
            (o => o.OutlevelMargin = 10, o => o.OutlevelMargin = 11, "OutlevelMargin"),
            (o => o.MaxRelocationsPerInterval = 50, o => o.MaxRelocationsPerInterval = 51, "MaxRelocationsPerInterval"),
            (o => o.MaxRelocationsPerInterval = 0, o => o.MaxRelocationsPerInterval = -1, "MaxRelocationsPerInterval"),
            (o => o.Factions = PopulationFactions.Horde, o => o.Factions = (PopulationFactions)9, "Factions"),
            (o => o.Relocation = PopulationRelocation.Off, o => o.Relocation = (PopulationRelocation)9, "Relocation"),
            (o => o.Classes = [11], o => o.Classes = [], "Classes"),
            (o => o.Classes = [1, 2], o => o.Classes = [1, 1], "Classes"), // a repeated class
            (o => o.Classes = [1], o => o.Classes = [6], "Classes"), // class 6 (death knight) does not exist in 1.12
            (o => o.Classes = [1], o => o.Classes = [0], "Classes"),
            (o => o.ExcludedZones = [1, 2], o => o.ExcludedZones = [1, 1], "ExcludedZones"),
            (o => o.ExcludedZones = [.. Enumerable.Range(1, 256).Select(i => (uint)i)], o => o.ExcludedZones = [.. Enumerable.Range(1, 257).Select(i => (uint)i)], "ExcludedZones"),
        ];
        foreach ((Action<PlayerbotPopulationOptions> good, Action<PlayerbotPopulationOptions> bad, string key) in cases)
        {
            var ok = new PlayerbotPopulationOptions();
            good(ok);
            ok.Validate();
            var broken = new PlayerbotPopulationOptions();
            bad(broken);
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(broken.Validate);
            Assert.Contains(key, error.Message, StringComparison.Ordinal);
            Assert.StartsWith("World:Playerbots:Population: ", error.Message, StringComparison.Ordinal);
        }

        // Through the playerbot options, which the daemon validates at start.
        var playerbots = new PlayerbotOptions();
        playerbots.Population.WindowTicks = 5;
        Assert.Throws<InvalidOperationException>(playerbots.Validate);
        playerbots = new PlayerbotOptions { Population = null! };
        Assert.Throws<InvalidOperationException>(playerbots.Validate);
    }

    [Fact]
    public void Binding_ReplacesTheDefaultLists_AndKeepsThemWhenNotConfigured()
    {
        IConfiguration configured = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["World:Playerbots:Population:Enabled"] = "true",
            ["World:Playerbots:Population:TargetOnline"] = "25",
            ["World:Playerbots:Population:Factions"] = "Horde",
            ["World:Playerbots:Population:Classes:0"] = "1",
            ["World:Playerbots:Population:Classes:1"] = "11",
            ["World:Playerbots:Population:Bands:0:MinLevel"] = "1",
            ["World:Playerbots:Population:Bands:0:MaxLevel"] = "30",
            ["World:Playerbots:Population:Bands:0:Weight"] = "3",
            ["World:Playerbots:Population:Bands:1:MinLevel"] = "31",
            ["World:Playerbots:Population:Bands:1:MaxLevel"] = "60",
            ["World:Playerbots:Population:Bands:1:Weight"] = "1",
            ["World:Playerbots:Population:ExcludedZones:0"] = "33",
        }).Build();
        PlayerbotPopulationOptions bound = PlayerbotOptions.Bind(configured).Population;
        bound.Validate();
        Assert.True(bound.Enabled);
        Assert.Equal(25, bound.TargetOnline);
        Assert.Equal(PopulationFactions.Horde, bound.Factions);
        Assert.Equal(new uint[] { 1, 11 }, bound.Classes); // not the nine defaults with two more appended
        Assert.Equal(["1-30x3", "31-60x1"], bound.Bands.Select(b => b.ToString()));
        Assert.Equal(new uint[] { 33 }, bound.ExcludedZones);

        PlayerbotPopulationOptions untouched = PlayerbotOptions.Bind(new ConfigurationBuilder().Build()).Population;
        Assert.Equal(9, untouched.Classes.Length);
        Assert.Equal(6, untouched.Bands.Length);
        Assert.Empty(untouched.ExcludedZones);
    }

    [Fact]
    public async Task ReloadKeys_AreLiveAndRangeChecked()
    {
        // Every public option has a key of the same name, and every one is live.
        string[] properties = [.. typeof(PlayerbotPopulationOptions).GetProperties().Select(p => "World:Playerbots:Population:" + p.Name)];
        string[] keys = [.. WorldConfigKeys.All.Where(k => k.Path.StartsWith("World:Playerbots:Population:", StringComparison.Ordinal)).Select(k => k.Path)];
        Assert.Equal(properties.Order(), keys.Order());
        Assert.All(WorldConfigKeys.All.Where(k => keys.Contains(k.Path)), k => Assert.True(k.Live, k.Path));

        string path = Write("""
            { "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5, "Playerbots": { "Enabled": true, "Population": {
                "Enabled": true, "TargetOnline": 40, "IntervalSeconds": 30, "TickP95BudgetMs": 45, "ResumeBelowPct": 70, "WindowTicks": 100,
                "ParkWhenOverBudget": false, "MaxCreatePerMinute": 9, "MaxStartsPerInterval": 7, "MaxParksPerInterval": 3, "Factions": "Alliance",
                "AllianceSharePct": 80, "Classes": [1, 8], "Bands": [ { "MinLevel": 1, "MaxLevel": 20, "Weight": 2 }, { "MinLevel": 21, "MaxLevel": 60, "Weight": 5 } ],
                "ZoneLevelSlack": 2, "OutlevelMargin": 3, "Relocation": "Off", "MaxRelocationsPerInterval": 5, "ExcludedZones": [33, 40] } } } }
            """);
        var playerbots = new PlayerbotOptions { Enabled = true };
        PlayerbotPopulationOptions population = playerbots.Population;
        ReloadCoordinator coordinator = Reloader(path, playerbots);

        Assert.Equal(ReloadStatus.Applied, (await coordinator.ReloadAsync("config")).Status);
        Assert.Same(population, playerbots.Population); // changed in place: the feature reads this object at every pass
        Assert.Equal((true, 40, 30, 45, 70, 100, false), (population.Enabled, population.TargetOnline, population.IntervalSeconds, population.TickP95BudgetMs,
            population.ResumeBelowPct, population.WindowTicks, population.ParkWhenOverBudget));
        Assert.Equal((9, 7, 3, PopulationFactions.Alliance, 80), (population.MaxCreatePerMinute, population.MaxStartsPerInterval, population.MaxParksPerInterval,
            population.Factions, population.AllianceSharePct));
        Assert.Equal(new uint[] { 1, 8 }, population.Classes);
        Assert.Equal(["1-20x2", "21-60x5"], population.Bands.Select(b => b.ToString()));
        Assert.Equal((2, 3, PopulationRelocation.Off, 5), (population.ZoneLevelSlack, population.OutlevelMargin, population.Relocation, population.MaxRelocationsPerInterval));
        Assert.Equal(new uint[] { 33, 40 }, population.ExcludedZones);
        Assert.Contains("no changes", (await coordinator.ReloadAsync("config")).Message);

        // A value outside its range (or an overlapping list) is refused and nothing changes.
        foreach (string bad in new[]
        {
            "\"TargetOnline\": 100000", "\"IntervalSeconds\": 1", "\"TickP95BudgetMs\": 4", "\"ResumeBelowPct\": 100", "\"WindowTicks\": 5000",
            "\"MaxCreatePerMinute\": 61", "\"MaxStartsPerInterval\": 0", "\"MaxParksPerInterval\": 101", "\"AllianceSharePct\": 101", "\"ZoneLevelSlack\": 11",
            "\"OutlevelMargin\": -1", "\"MaxRelocationsPerInterval\": 51", "\"Classes\": [6]", "\"Classes\": [1, 1]", "\"ExcludedZones\": [5, 5]",
            "\"Bands\": [ { \"MinLevel\": 1, \"MaxLevel\": 20, \"Weight\": 1 }, { \"MinLevel\": 20, \"MaxLevel\": 30, \"Weight\": 1 } ]",
            "\"Bands\": [ { \"MinLevel\": 1, \"MaxLevel\": 20, \"Weight\": 0 } ]",
        })
        {
            File.WriteAllText(path, $$"""{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5, "Playerbots": { "Population": { {{bad}} } } } }""");
            Assert.NotEqual(ReloadStatus.Applied, (await coordinator.ReloadAsync("config")).Status);
            Assert.Equal((40, 30, 45, 70, 100), (population.TargetOnline, population.IntervalSeconds, population.TickP95BudgetMs, population.ResumeBelowPct, population.WindowTicks));
            Assert.Equal(new uint[] { 1, 8 }, population.Classes);
            Assert.Equal(2, population.Bands.Length);
            Assert.Equal(new uint[] { 33, 40 }, population.ExcludedZones);
        }

        // Removed: the defaults come back, lists included.
        File.WriteAllText(path, """{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5 } }""");
        Assert.Equal(ReloadStatus.Applied, (await coordinator.ReloadAsync("config")).Status);
        Assert.False(population.Enabled);
        Assert.Equal((0, 10, 60, 80, 200, true), (population.TargetOnline, population.IntervalSeconds, population.TickP95BudgetMs, population.ResumeBelowPct,
            population.WindowTicks, population.ParkWhenOverBudget));
        Assert.Equal(9, population.Classes.Length);
        Assert.Equal(6, population.Bands.Length);
        Assert.Empty(population.ExcludedZones);
        Assert.Equal(PopulationRelocation.Teleport, population.Relocation);
    }

    private static void AssertRejected(Action<PlayerbotPopulationOptions> change, string fragment)
    {
        var options = new PlayerbotPopulationOptions();
        change(options);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains(fragment, error.Message, StringComparison.Ordinal);
    }

    private string Write(string json)
    {
        string path = Path.Combine(_directory, "appsettings.json");
        File.WriteAllText(path, json);
        return path;
    }

    private ReloadCoordinator Reloader(string path, PlayerbotOptions playerbots)
    {
        var configuration = new ConfigurationManager();
        configuration.AddJsonFile(path, optional: false, reloadOnChange: false);
        IServiceCollection services = new ServiceCollection().AddSingleton<IConfiguration>(configuration).AddSingleton(Options.Create(playerbots));
        var coordinator = new ReloadCoordinator(NullLogger.Instance);
        coordinator.Attach(_world);
        coordinator.Register(new ConfigContentReloadable(services.BuildServiceProvider()));
        _world.Start();
        return coordinator;
    }

    private sealed class NullSaveQueue : ICharacterSaveQueue
    {
        public void Enqueue(CharacterState state)
        {
        }
    }
}
