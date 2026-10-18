using ArcaneCore.Atlas.Core;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Atlas.Reference.Client.Terrain;

/// <summary>
/// Registers the client terrain reference: the <c>ref.terrain</c> and <c>ref.liquid</c> layers, the
/// <see cref="ClientTerrainService"/> and <see cref="IClientPlacementSource"/> services, and the
/// <c>ref.query.height</c> and <c>ref.adt.info</c> commands. It runs after the foundation modules; the client files and
/// the map directory are looked up per request, so a host that registers them later (or never) still composes, and the
/// layers then report the tile as Missing with the reason.
/// </summary>
public sealed class ClientTerrainModule : IAtlasModule
{
    /// <inheritdoc />
    public string Name => "reference.client-terrain";

    /// <inheritdoc />
    public int Order => ModuleOrder.Layers;

    /// <inheritdoc />
    public void Register(AtlasContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ILogger log = context.Loggers.CreateLogger("ArcaneCore.Atlas.Reference.ClientTerrain");
        var service = new ClientTerrainService(
            () => context.Services.Get<IClientFileSource>(),
            () => context.Services.Get<IMapDirectory>(),
            log);
        context.Services.Add(service);
        context.Services.Add<IClientPlacementSource>(new ClientPlacementSource(service));
        context.Layers.Add(new ClientTerrainLayerSource(service, log));
        ClientTerrainCommands.Register(context.Commands);
    }
}
