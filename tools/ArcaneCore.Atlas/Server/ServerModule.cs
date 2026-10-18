using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Atlas.Server;

/// <summary>
/// The server view: the fourteen server layers (<c>tiles</c>, <c>terrain</c>, <c>terrain.holes</c>, <c>terrain.noheight</c>, <c>area</c>,
/// <c>liquid</c>, <c>vmap.wmo</c>, <c>vmap.m2</c>, <c>vmap.bounds</c>, <c>vmap.liquid</c>, <c>nav.poly</c>, <c>nav.detail</c>, <c>nav.portals</c>,
/// <c>nav.bv</c>) read with the server's own file readers, the probe commands that ask the server's own collision code
/// (<c>query.height</c>, <c>query.los</c>, <c>query.path</c>, <c>query.floorstack</c>, <c>server.status</c>), and the two services that back them:
/// <see cref="ServerData"/> (parsed-file caches, any thread) and <see cref="ServerEngine"/> (the headless world on its owner thread).
/// <para>
/// Registration reads nothing: the data object only remembers the directories and the engine creates its world on the first query that needs
/// one, so a host that never probes pays nothing. Both services are disposed with the context.
/// </para>
/// </summary>
public sealed class ServerModule : IAtlasModule
{
    /// <inheritdoc />
    public string Name => "server";

    /// <inheritdoc />
    public int Order => ModuleOrder.Foundation;

    /// <inheritdoc />
    public void Register(AtlasContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var data = new ServerData(context.Paths);
        var engine = new ServerEngine(data, context.Loggers);
        context.Services.Add(data);
        context.Services.Add(engine);

        context.Layers.Add(new TilesLayerSource(data));
        context.Layers.Add(new TerrainLayerSource(data));
        context.Layers.Add(new VMapLayerSource(data));
        context.Layers.Add(new NavLayerSource(data));

        ILogger log = context.Loggers.CreateLogger("ArcaneCore.Atlas.Server.Commands");
        foreach (CommandDefinition command in ServerCommands.Create(engine, log))
        {
            context.Commands.Register(command);
        }
    }
}
