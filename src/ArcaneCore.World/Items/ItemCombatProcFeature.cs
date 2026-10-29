using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Features;
using ArcaneCore.World.Spells;

namespace ArcaneCore.World.Items;

public sealed class ItemCombatProcFeature(SpellFeature spells) : IWorldFeature
{
    private readonly HashSet<MapCombat> _subscribed = new(ReferenceEqualityComparer.Instance);

    public void Attach(WorldRuntime world)
    {
        world.MapCreated += Subscribe;
        world.MapUnloading += Unsubscribe;
        foreach (Map map in world.Maps)
            Subscribe(map);
    }

    /// <summary>Follow a map's weapon hits once (the attach-time walk and <see cref="WorldRuntime.MapCreated"/> may both reach a map).</summary>
    private void Subscribe(Map map)
    {
        if (map.FindUpdater<MapCombat>() is { } combat && _subscribed.Add(combat))
            combat.MeleeWeaponHitDealt += spells.System.OnMeleeWeaponHit; // item chance-on-hit spells, then the victim's damage shields (Unit.cpp:1774-1782)
    }

    /// <summary>Release an unloading map's combat (as <c>SpellFeature.UnsubscribeCombat</c>).</summary>
    private void Unsubscribe(Map map)
    {
        if (map.FindUpdater<MapCombat>() is { } combat && _subscribed.Remove(combat))
            combat.MeleeWeaponHitDealt -= spells.System.OnMeleeWeaponHit;
    }
}
