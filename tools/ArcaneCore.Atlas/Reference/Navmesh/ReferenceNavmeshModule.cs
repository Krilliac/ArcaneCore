using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Atlas.Reference.Navmesh;

/// <summary>
/// The DotRecast reference navmesh: the <c>ref.nav.poly</c> and <c>ref.nav.links</c> layers, the <c>ref.query.path</c>,
/// <c>ref.query.nearest</c> and <c>ref.nav.tile</c> commands, and a <see cref="ReferenceNavMeshService"/> other modules (the
/// navmesh diff) can read.
/// </summary>
public sealed class ReferenceNavmeshModule : IAtlasModule
{
    /// <inheritdoc/>
    public string Name => "Reference.Navmesh";

    /// <inheritdoc/>
    public int Order => ModuleOrder.Layers;

    /// <inheritdoc/>
    public void Register(AtlasContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var service = new ReferenceNavMeshService(context.Paths.MMapsDirectory, context.Loggers.CreateLogger<ReferenceNavMeshService>());
        context.Services.Add(service);
        context.Layers.Add(new ReferenceNavLayerSource(service));
        foreach (CommandDefinition command in NavCommands.Definitions)
        {
            context.Commands.Register(command);
        }
    }
}
