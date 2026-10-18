using ArcaneCore.Game.GameObjects;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// SPELL_EFFECT_PULL (70) and SPELL_EFFECT_DESPAWN_OBJECT (130), after vmangos SpellEffects.cpp:2626-2630 and :5908-5914.
/// <list type="bullet">
/// <item>PULL: vmangos' handler is a no-op ("TODO: create a proper pull towards distract spell center"); its one 5875 user, Distract Move (15051),
/// is cast for its visual only. Registered so the cast no longer counts as an unhandled effect.</item>
/// <item>DESPAWN_OBJECT: the effect's game-object target leaves the world (vmangos AddObjectToRemoveList). Here it despawns through
/// <see cref="GameObjectMapSystem.Despawn"/>, so a database spawn comes back on its own respawn timer and a summoned object is removed.
/// No 5875 Spell.dbc row uses the effect; it is for custom and scripted spells.</item>
/// </list>
/// </summary>
public sealed class PullAndDespawnObjectEffects : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterEffect(SpellEffectName.Pull, static _ => { });
        system.RegisterEffect(SpellEffectName.DespawnObject, EffectDespawnObject);
    }

    private static void EffectDespawnObject(SpellEffectContext context)
    {
        if (context.Caster.Map?.FindUpdater<GameObjectMapSystem>() is not { } objects)
        {
            return;
        }

        IReadOnlyList<ObjectGuid> targets = context.Cast.ObjectTargetsByEffect.TryGetValue(context.EffectIndex, out ObjectGuid[]? selected)
            ? selected : context.Cast.Targets.GameObject.IsEmpty ? [] : [context.Cast.Targets.GameObject];
        foreach (ObjectGuid guid in targets)
        {
            if (objects.Find(guid) is { } go)
            {
                objects.Despawn(go);
            }
        }
    }
}