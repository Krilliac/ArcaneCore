using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Insight;
using ArcaneCore.Atlas.Live;
using ArcaneCore.Atlas.Tests.Live;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Insight;

/// <summary>perf.report and perf.stats, and how the host sees the insight module.</summary>
public sealed class InsightPerfAndModuleTests
{
    private static readonly string[] CommandNames =
    [
        "insight.watch",
        "live.anomalies",
        "live.heatmap",
        "live.highlight",
        "live.inspect",
        "live.record",
        "live.replay",
        "live.replay.diff",
        "live.replay.entities",
        "perf.report",
        "perf.stats",
    ];

    [Fact]
    public void TheHostFindsTheModuleByReflection_WithoutAnySharedList()
    {
        IReadOnlyList<IAtlasModule> found = AtlasModules.Discover(typeof(InsightModule).Assembly);

        InsightModule module = Assert.Single(found.OfType<InsightModule>());
        Assert.Equal("insight", module.Name);
        Assert.True(module.Order > ModuleOrder.Layers, "insight must register after the live module it uses");
        Assert.True(module.Order < ModuleOrder.Host);
    }

    [Fact]
    public void Register_AddsEveryCommand_WithASchemaAndNoDuplicate_AndTheHubService()
    {
        using AtlasContext context = AtlasModules.Compose(
            new AtlasPaths(null, null),
            NullLoggerFactory.Instance,
            headless: true,
            [new LiveModule(new LiveOptions { PipeNameFor = _ => "atlas-insight-unused" }), new InsightModule()]);

        foreach (string name in CommandNames)
        {
            Assert.True(context.Commands.TryGet(name, out CommandDefinition? definition), $"{name} is not registered");
            Assert.False(string.IsNullOrWhiteSpace(definition.Summary));
            Assert.NotNull(CommandSchema.InputSchema(definition)["properties"]);
            Assert.True(definition.Surfaces.HasFlag(CommandSurfaces.Headless), $"{name} must work from run, script and MCP");
        }

        Assert.NotNull(context.Services.Get<InsightHub>());
        Assert.Empty(context.Problems);
    }

    [Fact]
    public void WithoutTheLiveModule_TheInsightModuleReportsAProblem_InsteadOfRegisteringCommandsThatCannotWork()
    {
        using AtlasContext context = AtlasModules.Compose(new AtlasPaths(null, null), NullLoggerFactory.Instance, headless: true, [new InsightModule()]);

        Assert.Contains(context.Problems, p => p.Contains("live", StringComparison.OrdinalIgnoreCase));
        Assert.False(context.Commands.TryGet("live.inspect", out _));
    }

    [WindowsFact]
    public async Task PerfStats_WithoutAUiReport_HasHostNumbersAndNoUiBlock()
    {
        await using InsightRig rig = await InsightRig.StartAsync();

        CommandResult stats = await rig.RunAsync("perf.stats");

        Assert.True(stats.Ok, stats.ErrorMessage);
        JsonObject process = stats.Data!["process"]!.AsObject();
        Assert.True(process["workingSetMb"]!.GetValue<double>() > 0);
        Assert.True(process["gcHeapMb"]!.GetValue<double>() > 0);
        Assert.True(process["layerCacheMb"]!.GetValue<double>() >= 0);
        Assert.True(stats.Data["live"]!["connected"]!.GetValue<bool>());
        Assert.Null(stats.Data["ui"]);
        Assert.Equal(0, stats.Data["recorder"]!["frames"]!.GetValue<int>());
    }

    [WindowsFact]
    public async Task PerfReport_StoresTheLatestUiNumbers_AndPerfStatsReturnsThemWithTheirAge()
    {
        await using InsightRig rig = await InsightRig.StartAsync();
        rig.Clock.Value = 50_000;
        var report = new JsonObject { ["fps"] = 59.5, ["frameMsP50"] = 16.2, ["frameMsP95"] = 21.0, ["drawCalls"] = 120, ["triangles"] = 456_000, ["gpuMb"] = 312.5, ["textureMb"] = 40, ["entities"] = 1000, ["online"] = 3 };

        CommandResult accepted = await rig.RunAsync("perf.report", new JsonObject { ["stats"] = report }, CommandOrigin.Ui);
        Assert.True(accepted.Ok, accepted.ErrorMessage);
        rig.Clock.Value = 52_500;
        CommandResult stats = await rig.RunAsync("perf.stats");

        JsonObject ui = stats.Data!["ui"]!.AsObject();
        Assert.Equal(59.5, ui["fps"]!.GetValue<double>());
        Assert.Equal(456_000, ui["triangles"]!.GetValue<double>());
        Assert.Equal(40, ui["textureMb"]!.GetValue<double>());
        Assert.Equal(2500, ui["ageMs"]!.GetValue<long>());

        await rig.RunAsync("perf.report", new JsonObject { ["stats"] = new JsonObject { ["fps"] = 30 } }, CommandOrigin.Ui);
        Assert.Equal(30, (await rig.RunAsync("perf.stats")).Data!["ui"]!["fps"]!.GetValue<double>());
    }

    [WindowsFact]
    public async Task PerfReport_RefusesUnknownKeys_NegativeNumbers_AndNonNumbers()
    {
        await using InsightRig rig = await InsightRig.StartAsync();

        foreach (JsonObject bad in new[]
        {
            new JsonObject { ["fps"] = 60, ["secret"] = 1 },
            new JsonObject { ["fps"] = -1 },
            new JsonObject { ["fps"] = "fast" },
            new JsonObject { ["fps"] = new JsonObject() },
            new JsonObject(),
        })
        {
            CommandResult refused = await rig.RunAsync("perf.report", new JsonObject { ["stats"] = bad }, CommandOrigin.Ui);
            Assert.Equal(CommandException.BadArguments, refused.ErrorCode);
        }

        Assert.Null((await rig.RunAsync("perf.stats")).Data!["ui"]);
    }
}
