using ArcaneCore.Game.Stealth;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// Leaving the spawn animation state of a creature. This lives in Creatures, not in Spells, because it needs the map's
/// <see cref="StealthRegistry"/>: the feature-namespace freeze (<c>GameFeatureCycleFreezeTests</c>) allows the
/// Creatures/Stealth edge but not a new Spells/Stealth cycle, and Spells already reaches Creatures.
/// </summary>
public static class CreatureSpawnState
{
    /// <summary>
    /// vmangos Spell::EffectSpawn (SpellEffects.cpp:2990-2998): the creature leaves the spawn animation state
    /// (UNIT_FLAG_SPAWNING is cleared and a visibility group other than ON is forced to ON through the map's
    /// <see cref="StealthRegistry"/>, then re-evaluated for everyone near it, as vmangos Unit::SetVisibility does with
    /// UpdateVisibilityAndView, Objects/Unit.cpp:6494-6500: nothing else shows a stationary creature to stationary viewers).
    /// VISIBILITY_OFF, the third state the reference tests with the same <c>!= VISIBILITY_ON</c>, has no
    /// <see cref="StealthVisibility"/> member and is not modelled.
    /// </summary>
    public static void LeaveSpawnAnimation(Creature creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        creature.UnitFlags &= ~UnitFlags.Spawning;
        if (creature.Map is { } map && StealthServices.Find(map)?.Registry is { } registry
            && registry.VisibilityOf(creature) != StealthVisibility.On)
        {
            registry.SetVisibility(creature, StealthVisibility.On);
            map.RefreshVisibility(creature);
        }
    }
}
