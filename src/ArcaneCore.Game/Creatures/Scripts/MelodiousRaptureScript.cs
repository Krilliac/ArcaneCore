using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Melodious Rapture (21050, the Rat Catcher's Flute) for quest 6661 "Deeprun Rat Roundup": mangos-classic <c>MelodiousRapture</c>
/// (scripts/world/spell_scripts.cpp:43-83), with vmangos' cast check (spell_item.cpp MelodiusRaptureScript). The target must be a Deeprun Rat
/// (13016). The rat becomes an Enthralled Deeprun Rat (13017), casts the visual (21051), and follows the player 0.5-3 yd behind. The player
/// gets the 13017 kill credit.
/// cmangos returns early when the caster IS a player (an inverted check that makes the effect never run for the quest), so the intended
/// "players only" check is used here.
/// </summary>
[SpellScript(SpellMelodiousRapture)]
public sealed class MelodiousRaptureScript : ISpellScript
{
    public const uint SpellMelodiousRapture = 21050, SpellMelodiousRaptureVisual = 21051, NpcDeeprunRat = 13016, NpcEnthralledDeeprunRat = 13017;

    public SpellCastResult OnCheckCast(in SpellCastCheckContext context) =>
        context.Target is Creature { Entry: NpcDeeprunRat } ? SpellCastResult.CastOk : SpellCastResult.BadTargets;

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Caster is not Player player || context.Target is not Creature { Entry: NpcDeeprunRat } rat
            || rat.Map?.FindUpdater<CreatureMapSystem>() is not { } creatures)
        {
            return;
        }

        creatures.UpdateEntry(rat, NpcEnthralledDeeprunRat);
        context.System.CastSpell(rat, SpellMelodiousRaptureVisual, SpellCastTargets.ForSelf(), triggered: false);
        if (rat.Motion.CurrentType == MovementGeneratorType.Waypoint)
        {
            creatures.MoveIdle(rat);
        }

        float distance = creatures.RandomInt(50, 300) / 100f, angle = MathF.PI * creatures.RandomInt(80, 120) / 100f;
        rat.Motion.MoveFollow(player, distance, angle);
        creatures.KilledMonsterCredit(player, NpcEnthralledDeeprunRat, rat.Guid);
    }
}
