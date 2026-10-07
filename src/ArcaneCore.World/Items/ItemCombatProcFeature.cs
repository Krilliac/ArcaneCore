using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Features;
using ArcaneCore.World.Spells;

namespace ArcaneCore.World.Items;

public sealed class ItemCombatProcFeature(SpellFeature spells) : IWorldFeature
{
    public void Attach(WorldRuntime world)
    {
        foreach (Map map in world.Maps)
            Subscribe(map);
        world.MapCreated += Subscribe;
    }

    private void Subscribe(Map map)
    {
        if (map.FindUpdater<MapCombat>() is { } combat)
            combat.MeleeWeaponHitDealt += spells.System.OnMeleeWeaponHit; // item chance-on-hit spells, then the victim's damage shields (Unit.cpp:1774-1782)
    }
}
