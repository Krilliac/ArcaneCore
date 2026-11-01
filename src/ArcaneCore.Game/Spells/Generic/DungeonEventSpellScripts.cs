using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Spells.Generic;

/// <summary>
/// Banish Burning Exile 4130, Banish Cresting Exile 4131 and Banish Thundering Exile 4132 (the Burning, Cresting and Thundering Charms of the
/// Gnomeregan-area elemental exile quest items; mangos-classic item_scripts.cpp:114-146 <c>BanishExile</c>, registered :435; vmangos has no C++
/// case: its gnomeregan.cpp only holds the same numbers as broadcast text ids). Each charm works on its own exile only (Burning Exile 2760,
/// Cresting Exile 2761, Thundering Exile 2762; any other target fails the cast with BAD_TARGETS). On the hit the exile says
/// "%s is ripped apart as it is banished from this world." (script text -1010004, scriptdev2.sql:1656) and casts Quiet Suicide 3617
/// (INSTAKILL on itself, triggered) on itself.
/// <para>Ownership: stateless, one instance per registry; world thread only. No allocation.</para>
/// </summary>
[SpellScript(4130, 4131, 4132)]
public sealed class BanishExileScript : ISpellScript
{
    public const uint QuietSuicide = 3617;
    public const int BanishedText = -1010004;

    /// <summary>The creature entry the charm spell <paramref name="spellId"/> works on; 0 for a spell that is no charm.</summary>
    public static uint ExileFor(uint spellId) => spellId switch
    {
        4130 => 2760,
        4131 => 2761,
        4132 => 2762,
        _ => 0,
    };

    public SpellCastResult OnCheckCast(in SpellCastCheckContext context)
    {
        // cmangos BanishExile::OnCheckCast: only an explicit unit target is judged; it has to be the charm's exile.
        if (context.Target is not { } target)
        {
            return SpellCastResult.CastOk;
        }

        return target is Creature creature && creature.Entry == ExileFor(context.Spell.Id) ? SpellCastResult.CastOk : SpellCastResult.BadTargets;
    }

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Target is not Creature exile)
        {
            return;
        }

        exile.System?.SayText(exile, BanishedText, context.Caster);
        GadgetCasts.Cast(context, exile, exile, QuietSuicide);
    }
}

/// <summary>
/// Unsummon Mechanical Yeti 17163 (Winterspring, Umi Thorn's quest line; vmangos winterspring.cpp:272-310 <c>npc_umi_yeti</c>, <c>SpellHit</c>):
/// Umi's Mechanical Yeti (10980) that is hit stops moving and goes away one second later. The DB movement scripts of Sprinkle and Quixxil cast
/// the spell (creature_movement_scripts 303 and 306); every other creature in the area is left alone, as only the yeti's AI reacts in vmangos.
/// <para>Ownership: stateless; world thread only.</para>
/// </summary>
[SpellScript(17163)]
public sealed class UnsummonMechanicalYetiScript : ISpellScript
{
    public const uint UmisMechanicalYeti = 10980;
    public const uint DespawnDelayMs = 1000;

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Target is not Creature { Entry: UmisMechanicalYeti } yeti)
        {
            return;
        }

        yeti.System?.MoveIdle(yeti); // vmangos winterspring.cpp:298 GetMotionMaster()->MoveIdle(): no generator may restart the yeti before it despawns
        WorldDummyCasts.Despawn(yeti, DespawnDelayMs);
    }
}
