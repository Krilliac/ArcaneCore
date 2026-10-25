using ArcaneCore.Atlas.Core;

namespace ArcaneCore.Atlas.Host.Public;

/// <summary>
/// Registers the public-listener commands (<c>guest.*</c>, <c>pick.public</c>, <c>me.characters</c>, <c>public.*</c>) and the <see cref="PublicState"/> they read.
/// The commands exist in every Atlas process so <c>ctl help</c> lists them; they answer from the listener only while one runs in the process.
/// Registering them does not start anything and opens no connection.
/// </summary>
public sealed class PublicModule : IAtlasModule
{
    /// <inheritdoc />
    public string Name => "public";

    /// <inheritdoc />
    public int Order => ModuleOrder.Host + 10;

    /// <inheritdoc />
    public void Register(AtlasContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var state = new PublicState();
        context.Services.Add(state);
        GuestCommands.Register(context.Commands, state);
        PublicLoginCommands.Register(context.Commands);
        PublicOpsCommands.Register(context.Commands, state);
    }
}
