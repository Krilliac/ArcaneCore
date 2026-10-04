using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.World.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Spells.Utility;

/// <summary>
/// Installs the spell script dispatcher (<see cref="SpellScriptDispatcher"/>) with every <see cref="ISpellScript"/> of the game
/// assembly on the world spell system (discovered <see cref="IWorldFeature"/>; attaches after <see cref="SpellFeature"/> by name, so
/// the spell table is loaded and the unknown-id warning is meaningful). docs/integration/wlm-spell-scripts.md.
/// </summary>
public sealed class SpellScriptFeature(IServiceProvider services, ILogger<SpellScriptFeature> logger) : IWorldFeature
{
    public SpellScriptDispatcher? Dispatcher { get; private set; }

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        SpellSystem system = services.GetRequiredService<SpellFeature>().System;
        var registry = SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly);
        Dispatcher = SpellScriptDispatcher.Install(system, registry, logger);
        logger.LogInformation("Installed {Scripts} spell script ids", registry.Count);
    }
}
