using ArcaneCore.Game.Death.Visibility;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Features;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Death;

/// <summary>
/// Attaches the ghost visibility rule to every map, as the stealth feature does for its own (docs/areas/graveyards-resurrection.md):
/// the living and the dead do not see each other, spirit healers show only to ghosts. The group resolver and the creature aggro
/// rate are read when a visibility check runs, so the order in which the features attach does not matter.
/// </summary>
public sealed class GhostVisibilityFeature(IServiceProvider services) : IWorldFeature
{
    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var rule = new GhostVisibilityRule(
            () => services.GetService<SpellFeature>()?.System.Groups,
            () => services.GetService<CreatureWorldFeature>()?.Options.AggroRate ?? 1.0f);

        void Install(Map map) => map.AddVisibilityRule(rule);

        world.MapCreated += Install;
        foreach (Map map in world.Maps)
        {
            Install(map);
        }
    }
}
