using ArcaneCore.Game.Scripting.Modules;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Scripting.Modules.DuelReset;

/// <summary>
/// AzerothCore mod-duel-reset as an ArcaneCore script module (<see cref="DuelResetScript"/>). Off unless
/// <c>Modules:DuelReset:Enabled</c> is true; the other keys of that section are <see cref="DuelResetSettings"/>.
/// </summary>
public sealed class DuelResetModule : IScriptModule
{
    public string Name => "DuelReset";

    /// <summary>The registered script (after <see cref="Register"/>).</summary>
    public DuelResetScript? Script { get; private set; }

    public void Register(ScriptModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        IServiceProvider services = context.Services;
        Script = new DuelResetScript(Bind(context.Configuration), () => services.GetService<SpellFeature>()?.System);
        context.AddHooks(Script);
    }

    public void ReloadConfig(IConfiguration section) => Script?.Apply(Bind(section));

    private static DuelResetSettings Bind(IConfiguration section)
    {
        var settings = new DuelResetSettings();
        section.Bind(settings);
        return settings;
    }
}
