using ArcaneCore.Atlas.Core;

namespace ArcaneCore.Atlas.Extract;

/// <summary>
/// Registers the <c>extract.*</c> commands. They look the client files and the map directory up per call, so the module
/// composes whether or not a client is configured; without one the commands answer "unavailable".
/// </summary>
public sealed class ExtractModule : IAtlasModule
{
    /// <inheritdoc />
    public string Name => "extract";

    /// <inheritdoc />
    public int Order => ModuleOrder.Layers;

    /// <inheritdoc />
    public void Register(AtlasContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ExtractCommands.Register(context.Commands);
    }
}
