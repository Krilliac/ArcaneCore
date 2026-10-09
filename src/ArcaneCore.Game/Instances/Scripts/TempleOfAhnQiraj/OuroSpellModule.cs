using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Instances.Scripts.TempleOfAhnQiraj;

/// <summary>Ouro's Despawn Sandworm Base spell has no imported spell_script_target row.</summary>
public sealed class OuroSpellModule : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        system.RegisterSpellTargetSelector(26594, (SpellImplicitTarget)40, (_, cast, _, _) =>
        {
            if (cast.Caster.Map?.FindUpdater<GameObjectMapSystem>() is not { } objects)
                return [];
            GameObject? sandwormBase = objects.GameObjects.Where(go => go.Entry == 180795 && go.IsSpawned)
                .Where(go => (go.X - cast.Caster.X) * (go.X - cast.Caster.X)
                    + (go.Y - cast.Caster.Y) * (go.Y - cast.Caster.Y) <= 40f * 40f)
                .OrderBy(go => (go.X - cast.Caster.X) * (go.X - cast.Caster.X)
                    + (go.Y - cast.Caster.Y) * (go.Y - cast.Caster.Y))
                .FirstOrDefault();
            if (sandwormBase is null) return [];
            cast.ObjectTargetsByEffect[0] = [sandwormBase.Guid];
            return [(cast.Caster, 1f)];
        });
    }
}
