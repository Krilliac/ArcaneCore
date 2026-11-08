using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;

namespace ArcaneCore.Atlas.Diff;

/// <summary>
/// Registers the diff view: the <c>diff.terrain</c>, <c>diff.models</c> and <c>diff.nav</c> layers and the
/// <c>diff.terrain</c>, <c>diff.models</c> and <c>diff.nav</c> commands that compare the server's parsed
/// <c>.map</c>, <c>.vmtile</c>/<c>.vmo</c> and <c>.mmtile</c> data against the 1.12.1 client files and DotRecast, and <c>diff.vmap.native</c>, which
/// compares the server's model queries with vmangos' own vmap code run through its probe program.
/// </summary>
public sealed class DiffModule : IAtlasModule
{
    /// <inheritdoc />
    public string Name => "diff";

    /// <inheritdoc />
    public int Order => ModuleOrder.Diff;

    /// <inheritdoc />
    public void Register(AtlasContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Layers.Add(new DiffLayerSource(context.Services));
        DiffCommands.Register(context.Commands, context.Services);
        foreach (CommandDefinition command in VMapNativeCommands.Definitions)
        {
            context.Commands.Register(command);
        }
    }
}
