using ArcaneCore.Game.GameObjects;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells;

/// <summary>SPELL_EFFECT_ACTIVATE_OBJECT (86): the spell's game-object target receives its DBC action.</summary>
public sealed class ActivateObjectEffect : ISpellHandlerModule
{
    public void Register(SpellSystem system) => system.RegisterEffect(SpellEffectName.ActivateObject, Apply);

    private static void Apply(SpellEffectContext context)
    {
        if (context.Caster.Map?.FindUpdater<GameObjectMapSystem>() is not { } objects)
            return;

        IReadOnlyList<ObjectGuid> targets = context.Cast.ObjectTargetsByEffect.TryGetValue(context.EffectIndex, out ObjectGuid[]? selected)
            ? selected : context.Cast.Targets.GameObject.IsEmpty ? [] : [context.Cast.Targets.GameObject];
        foreach (ObjectGuid guid in targets)
        {
            if (objects.Find(guid) is { } go)
                objects.ActivateBySpell(context.Caster, go, context.Spell.Id, context.Effect.MiscValue, context.System.Random);
        }
    }
}
