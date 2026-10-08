using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Instances.Scripts.Scholomance;

/// <summary>
/// Darkmaster Gandling's Shadow Portal 17950 (mangos-classic SpellEffects.cpp EffectDummy, case 17950): the DUMMY effect has the caster
/// cast one of the six room portals 17863, 17939, 17943, 17944, 17946 or 17948 at the target, triggered. Each of those teleports the target
/// into a room (TARGET_TABLE_X_Y_Z_COORDINATES, spell_target_position) and sends one of the events 5618-5623 that
/// <see cref="ScholomanceInstance"/> turns into the closed room door and its Risen Guardians.
/// </summary>
[SpellScript(SpellId)]
public sealed class ShadowPortalScript : ISpellScript
{
    public const uint SpellId = 17950;

    /// <summary>The room portals, in the reference's spell_list order.</summary>
    public static readonly uint[] RoomPortals = [17863, 17939, 17943, 17944, 17946, 17948];

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Effect.Effect != SpellEffectName.Dummy)
        {
            return;
        }

        uint portal = RoomPortals[context.System.Random.Next(RoomPortals.Length)]; // urand(0, 5)
        context.System.CastSpell(context.Caster, portal, SpellCastTargets.ForUnit(context.Target.Guid), triggered: true);
    }
}
