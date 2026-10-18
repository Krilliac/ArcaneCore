using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Kernel.Reflection;
using ArcaneCore.World.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Spells.Utility;

/// <summary>
/// Installs the spell script dispatcher (<see cref="SpellScriptDispatcher"/>) with every <see cref="ISpellScript"/> of the startup code set
/// (<see cref="StartupAssemblies"/>) on the world spell system (discovered <see cref="IWorldFeature"/>; attaches after <see cref="SpellFeature"/> by
/// name, so the spell table is loaded and the unknown-id warning is meaningful). docs/areas/warlock-mage-utility.md, wlm-02.
/// <para>
/// <see cref="Registry"/> is the live, swappable registry: the hot module lane (<c>ModuleGameSeams</c>) adds, replaces and removes a module's scripts
/// on it at a tick boundary, and the dispatcher follows (<see cref="SpellScriptRegistry.ScriptsChanged"/>). The scripts discovered here are built in.
/// </para>
/// </summary>
public sealed class SpellScriptFeature(IServiceProvider services, ILogger<SpellScriptFeature> logger) : IWorldFeature
{
    /// <summary>The code set whose spell scripts are built in: the Game assembly (no pack is configured at startup).</summary>
    public static CodeAssemblies StartupAssemblies { get; } = CodeAssemblies.Of(typeof(SpellScriptRegistry).Assembly);

    public SpellScriptDispatcher? Dispatcher { get; private set; }

    /// <summary>The live spell script registry (null before <see cref="Attach"/>); what module swaps commit to.</summary>
    public SpellScriptRegistry? Registry { get; private set; }

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        SpellSystem system = services.GetRequiredService<SpellFeature>().System;
        var registry = SpellScriptRegistry.Discover(StartupAssemblies.Assemblies);
        Dispatcher = SpellScriptDispatcher.Install(system, registry, logger);
        Registry = registry;
        logger.LogInformation("Installed {Scripts} spell script ids", registry.Count);
    }
}
