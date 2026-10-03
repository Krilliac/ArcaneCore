using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Stealth;
using ArcaneCore.World.Features;
using ArcaneCore.World.Spells;

namespace ArcaneCore.World.Stealth;

/// <summary>
/// Invisibility in the world daemon (discovered <see cref="IWorldFeature"/>): attaches <see cref="InvisibilityVisibilityRule"/> to every map. The aura
/// handlers (<see cref="InvisibilityAuras"/>) are a spell handler module and need no feature. The group rule shares the stealth settings
/// (<see cref="StealthFeature.Options"/>, World:Stealth:Group visibility mode).
/// </summary>
public sealed class InvisibilityFeature(SpellFeature spells, StealthFeature stealth) : IWorldFeature
{
    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        void Install(Map map) => map.AddVisibilityRule(new InvisibilityVisibilityRule(spells.System, stealth.Options));

        world.MapCreated += Install;
        foreach (Map map in world.Maps)
        {
            Install(map);
        }
    }
}
