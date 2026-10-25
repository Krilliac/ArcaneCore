using ArcaneCore.Atlas.Core;

namespace ArcaneCore.Atlas.Live;

/// <summary>
/// Plugs the live client into Atlas: the <see cref="LiveFeed"/> service, the <c>live.*</c> commands and a layer source for the live layers.
/// Discovered by reflection (public parameterless constructor); the second constructor lets tests and embedders choose the pipe naming,
/// discovery and timeouts.
/// </summary>
public sealed class LiveModule : IAtlasModule
{
    private readonly LiveOptions _options;

    /// <summary>The module with <see cref="LiveOptions.Default"/>.</summary>
    public LiveModule()
        : this(LiveOptions.Default)
    {
    }

    /// <summary>The module with the given options.</summary>
    public LiveModule(LiveOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public string Name => "live";

    /// <inheritdoc />
    public int Order => ModuleOrder.Layers;

    /// <inheritdoc />
    public void Register(AtlasContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var feed = new LiveFeed(_options, context.Events, context.View, context.Loggers.CreateLogger(LiveFeed.LogCategory));
        context.Services.Add(feed);
        context.Services.Add<IFeedRequester>(new LiveFeedRequester(feed));
        context.Layers.Add(new LiveLayerSource());
        LiveCommands.Register(context.Commands, feed);
    }
}

/// <summary>
/// The live layers have no tiles: the UI draws them from <see cref="EventHub.Topics.LiveFrame"/> pushes. This source exists so the layers
/// have an owner (the host lists a layer without a source as unavailable) and answers every tile with
/// <see cref="TileState.NotApplicable"/> and the reason, never an empty-looking success.
/// </summary>
internal sealed class LiveLayerSource : ILayerSource
{
    private static readonly string[] LiveLayers = [.. LayerCatalog.All.Where(l => l.View == LayerView.Live).Select(l => l.Id)];

    /// <inheritdoc />
    public IReadOnlyList<string> Layers => LiveLayers;

    /// <inheritdoc />
    public IReadOnlyCollection<TileKey> AvailableTiles(string layerId, uint mapId)
    {
        ArgumentNullException.ThrowIfNull(layerId);
        return [];
    }

    /// <inheritdoc />
    public TileStatus PeekStatus(string layerId, TileKey tile)
    {
        ArgumentNullException.ThrowIfNull(layerId);
        return new(layerId, tile, TileState.NotApplicable, "live layers are drawn from live frames (the live.frame event), not from tiles");
    }

    /// <inheritdoc />
    public LayerBuild Build(string layerId, TileKey tile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layerId);
        if (!tile.IsValid)
        {
            throw new ArgumentException($"Tile {tile} is outside the map grid.", nameof(tile));
        }

        return LiveLayers.Contains(layerId, StringComparer.Ordinal)
            ? new LayerBuild(PeekStatus(layerId, tile), [])
            : throw new ArgumentException($"Layer '{layerId}' is not a live layer.", nameof(layerId));
    }
}
