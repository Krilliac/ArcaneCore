using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Core;

public sealed class ModulesTests
{
    private sealed class FixedSource(params string[] layers) : ILayerSource
    {
        public IReadOnlyList<string> Layers { get; } = layers;

        public IReadOnlyCollection<TileKey> AvailableTiles(string layerId, uint mapId) => [];

        public TileStatus PeekStatus(string layerId, TileKey tile) => new(layerId, tile, TileState.Missing);

        public LayerBuild Build(string layerId, TileKey tile, CancellationToken cancellationToken) => LayerBuild.Missing(layerId, tile);
    }

    private sealed class Module(string name, int order, Action<AtlasContext> register) : IAtlasModule
    {
        public string Name { get; } = name;

        public int Order { get; } = order;

        public void Register(AtlasContext context) => register(context);
    }

    private sealed class Marker
    {
        public List<string> Seen { get; } = [];
    }

    [Fact]
    public void Compose_RunsModulesInOrder_AndReportsTheOneThatThrows()
    {
        IAtlasModule[] modules =
        [
            new Module("layers", ModuleOrder.Layers, c => c.Services.Require<Marker>().Seen.Add("layers")),
            new Module("broken", ModuleOrder.Layers, _ => throw new InvalidDataException("bad archive")),
            new Module("foundation", ModuleOrder.Foundation, c =>
            {
                c.Services.Add(new Marker());
                c.Services.Require<Marker>().Seen.Add("foundation");
                c.Layers.Add(new FixedSource(LayerIds.Terrain, LayerIds.Liquid));
                c.Commands.Register(new CommandDefinition("ping", "Ping.", [], (_, _) => Task.FromResult<JsonNode?>("pong")));
            }),
        ];

        using AtlasContext context = AtlasModules.Compose(new AtlasPaths(null, null), NullLoggerFactory.Instance, headless: true, modules);

        Assert.Equal(new[] { "foundation", "layers" }, context.Services.Require<Marker>().Seen);
        string problem = Assert.Single(context.Problems);
        Assert.Contains("broken", problem, StringComparison.Ordinal);
        Assert.Contains("bad archive", problem, StringComparison.Ordinal);
        Assert.Equal(new[] { LayerIds.Terrain, LayerIds.Liquid }, context.Layers.Available);
        Assert.True(context.Commands.TryGet("ping", out _));
        Assert.Throws<InvalidOperationException>(() => context.Layers.Add(new FixedSource(LayerIds.Area)));
    }

    [Fact]
    public void Layers_RefuseUnknownIds_AndSecondSources()
    {
        var registry = new LayerRegistry();
        registry.Add(new FixedSource(LayerIds.NavPoly));

        Assert.Throws<ArgumentException>(() => registry.Add(new FixedSource("nav.unknown")));
        Assert.Throws<ArgumentException>(() => registry.Add(new FixedSource(LayerIds.Area, LayerIds.NavPoly)));

        // The refused source must not have claimed its other layer either.
        Assert.False(registry.TryGet(LayerIds.Area, out _));
    }

    [Fact]
    public void Paths_Problems_AreReportedAtCompose()
    {
        string missing = Path.Combine(Path.GetTempPath(), "atlas-missing-" + Guid.NewGuid().ToString("N"));

        using AtlasContext context = AtlasModules.Compose(new AtlasPaths(missing, null), NullLoggerFactory.Instance, true, []);

        Assert.Contains(context.Problems, p => p.Contains("does not exist", StringComparison.Ordinal));
    }

    [Fact]
    public void Services_DisposeInReverseOrder()
    {
        var order = new List<string>();
        var services = new AtlasServices();
        services.Add(new Disposable("a", order));
        services.Add<IDisposable>(new Disposable("b", order));

        services.Dispose();

        Assert.Equal(new[] { "b", "a" }, order);
        Assert.Throws<ArgumentException>(() =>
        {
            var again = new AtlasServices();
            again.Add(new Marker());
            again.Add(new Marker());
        });
    }

    [Fact]
    public void MemoryClientFiles_MatchPathsCaseAndSlashInsensitively()
    {
        var files = new MemoryClientFiles();
        files.Add("World/Maps/Azeroth/Azeroth.wdt", [1, 2, 3]);
        files.Add("World\\Maps\\Azeroth\\Azeroth_32_48.adt", [4]);

        Assert.True(files.Exists("WORLD\\MAPS\\AZEROTH\\AZEROTH.WDT"));
        Assert.Equal(new byte[] { 1, 2, 3 }, files.Read("\\world\\maps\\azeroth\\azeroth.wdt"));
        Assert.Null(files.Read("World\\Maps\\Kalimdor\\Kalimdor.wdt"));
        Assert.Equal(
            new[] { "World\\Maps\\Azeroth\\Azeroth.wdt", "World\\Maps\\Azeroth\\Azeroth_32_48.adt" },
            files.List("world/maps/azeroth/"));
    }

    private sealed class Disposable(string name, List<string> order) : IDisposable
    {
        public void Dispose() => order.Add(name);
    }
}
