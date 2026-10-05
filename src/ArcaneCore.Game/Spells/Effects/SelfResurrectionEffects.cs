using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// SELF_RESURRECT (94), after vmangos SpellEffects.cpp:5334-5370. The effect restores a
/// dead online player directly; availability and release-dialog producers are separate.
/// </summary>
public sealed class SelfResurrectionEffects : ISpellHandlerModule
{
    public void Register(SpellSystem system) => system.RegisterEffect(SpellEffectName.SelfResurrect, Resurrect);

    private static void Resurrect(SpellEffectContext context)
    {
        if (context.Target is Player player && player.Map is { } map)
        {
            map.Combat.ResurrectSelf(player, context.Value, context.Effect.MiscValue, context.System.Random);
        }
    }
}
