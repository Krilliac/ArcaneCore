using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Reference.Client.Terrain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Client.Terrain;

public sealed class ClientTerrainModuleTests
{
    private sealed class FilesModule(int order, TerrainFixture? fixture) : IAtlasModule
    {
        public string Name => "test.client-files";

        public int Order { get; } = order;

        public void Register(AtlasContext context)
        {
            if (fixture is null)
            {
                return;
            }

            context.Services.Add<IClientFileSource>(fixture.Files);
            context.Services.Add<IMapDirectory>(fixture.Maps);
        }
    }

    private static AtlasContext Compose(params IAtlasModule[] modules)
        => AtlasModules.Compose(new AtlasPaths(null, null), NullLoggerFactory.Instance, headless: true, modules);

    [Fact]
    public void IsDiscoveredByTheHostAndRunsInTheLayersTier()
    {
        IReadOnlyList<IAtlasModule> found = AtlasModules.Discover(typeof(AtlasModules).Assembly);

        ClientTerrainModule module = Assert.Single(found.OfType<ClientTerrainModule>());
        Assert.Equal(ModuleOrder.Layers, module.Order);
        Assert.False(string.IsNullOrWhiteSpace(module.Name));
    }

    [Fact]
    public async Task Register_AddsTheLayersServicesAndCommands()
    {
        var fixture = new TerrainFixture();
        using AtlasContext context = Compose(new FilesModule(ModuleOrder.Foundation, fixture), new ClientTerrainModule());

        Assert.Empty(context.Problems);
        Assert.Equal(new[] { LayerIds.RefTerrain, LayerIds.RefLiquid }, context.Layers.Available);
        Assert.NotNull(context.Services.Get<ClientTerrainService>());
        IClientPlacementSource placements = context.Services.Require<IClientPlacementSource>();
        Assert.IsType<ClientPlacementSource>(placements);
        Assert.Equal(3, placements.Placements(TerrainFixture.Goldshire)!.Count);
        Assert.True(context.Commands.TryGet("ref.query.height", out _));
        Assert.True(context.Commands.TryGet("ref.adt.info", out _));

        LayerBuild terrain = context.Cache.Get(LayerIds.RefTerrain, TerrainFixture.Goldshire, CancellationToken.None);
        Assert.Equal(TileState.Ok, terrain.Status.State);

        CommandResult info = await context.Commands.InvokeAsync(
            "ref.adt.info", JsonNode.Parse("""{"map":0,"x":48,"y":32}""")!.AsObject(), context.Services, CommandOrigin.Cli, true, CancellationToken.None);
        Assert.True(info.Ok, info.ErrorMessage);
    }

    [Fact]
    public void Register_FindsClientFilesThatALaterModuleRegisters()
    {
        var fixture = new TerrainFixture();
        using AtlasContext context = Compose(new ClientTerrainModule(), new FilesModule(ModuleOrder.Diff, fixture));

        LayerBuild build = context.Cache.Get(LayerIds.RefTerrain, TerrainFixture.Flat, CancellationToken.None);

        Assert.Empty(context.Problems);
        Assert.Equal(TileState.Ok, build.Status.State);
    }

    [Fact]
    public void Register_WithoutClientFilesStillComposesAndReportsTilesMissing()
    {
        using AtlasContext context = Compose(new ClientTerrainModule());

        LayerBuild build = context.Cache.Get(LayerIds.RefLiquid, TerrainFixture.Goldshire, CancellationToken.None);
        IClientPlacementSource placements = context.Services.Require<IClientPlacementSource>();

        Assert.Empty(context.Problems);
        Assert.Equal(TileState.Missing, build.Status.State);
        Assert.Contains("No client files", build.Status.Detail, StringComparison.Ordinal);
        Assert.Null(placements.Placements(TerrainFixture.Goldshire));
        Assert.True(context.Layers.TryGet(LayerIds.RefTerrain, out ILayerSource source));
        Assert.Empty(source.AvailableTiles(LayerIds.RefTerrain, 0));
    }

    [Fact]
    public void Register_RefusesToRunTwiceOnOneContext()
    {
        var fixture = new TerrainFixture();
        using AtlasContext context = Compose(new FilesModule(ModuleOrder.Foundation, fixture), new ClientTerrainModule());

        // A second registration cannot take the same layers, commands or service: the host reports it, nothing is half-added.
        Assert.ThrowsAny<ArgumentException>(() => new ClientTerrainModule().Register(context));
        Assert.Equal(new[] { LayerIds.RefTerrain, LayerIds.RefLiquid }, context.Layers.Available);
    }
}
