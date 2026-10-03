using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.GameObjects;

/// <summary>
/// Enforces <c>SpellInfo.RequiresSpellFocus</c> in the world daemon (discovered <see cref="IWorldFeature"/>): registers
/// <see cref="SpellFocusCastCheck"/> on the spell system with the per-map game object systems of
/// <see cref="GameObjectLootFeature"/>. Configuration <c>Spells:RequireSpellFocus</c> (default true, retail) switches it off.
/// </summary>
public sealed class SpellFocusFeature(IServiceProvider services) : IWorldFeature
{
    public const string ConfigKey = "Spells:RequireSpellFocus";

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        IConfiguration? configuration = services.GetService<IConfiguration>();
        bool enabled = configuration is null || !bool.TryParse(configuration[ConfigKey], out bool configured) || configured;
        services.GetRequiredService<Spells.SpellFeature>().System.RegisterCastCheck(
            new SpellFocusCastCheck(map => services.GetService<GameObjectLootFeature>()?.FindSystem(map), () => enabled));
    }
}
