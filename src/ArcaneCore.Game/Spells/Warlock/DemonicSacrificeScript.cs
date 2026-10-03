using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Spells.Warlock;

/// <summary>
/// Demonic Sacrifice (spell 18788, vmangos scripts/spells/spell_warlock.cpp:19-56): the spell instantly kills the caster's pet (INSTAKILL on
/// target 5) and, before it does, this script reads the pet's entry and has the caster cast the buff of that demon on itself, triggered:
/// Imp 416 gives Burning Wish 18789, Felhunter 417 Fel Energy 18792, Voidwalker 1860 Fel Stamina 18790, Succubus 1863 Touch of Shadow 18791.
/// Another entry grants nothing (vmangos logs an error and the pet still dies). The script runs before the INSTAKILL effect, so it asks the
/// dispatcher to chain that effect (<see cref="SpellScriptAttribute.ExecuteEffects"/>); the first effect only (vmangos checks <c>effIdx == 0</c>).
/// Summoning a demon removes the buff again (<see cref="Pets.SummonService"/>, class script 2228).
/// </summary>
[SpellScript(DemonicSacrificeScript.SpellId, ExecuteEffects = new[] { SpellEffectName.Instakill })]
public sealed class DemonicSacrificeScript : ISpellScript
{
    public const uint SpellId = 18788;

    /// <summary>The creature entries of the four demons (creature_template of vmangos).</summary>
    public const uint Imp = 416;

    public const uint Felhunter = 417;

    public const uint Voidwalker = 1860;

    public const uint Succubus = 1863;

    /// <summary>The Demonic Sacrifice buffs carry SPELL_AURA_OVERRIDE_CLASS_SCRIPTS with this misc value (SpellEffects.cpp:3218-3228).</summary>
    public const int ClassScriptMisc = 2228;

    /// <summary>The buff of a demon entry, 0 for an entry the spell does not know.</summary>
    public static uint BuffOf(uint entry) => entry switch
    {
        Imp => 18789,
        Felhunter => 18792,
        Voidwalker => 18790,
        Succubus => 18791,
        _ => 0,
    };

    public void OnEffectExecute(SpellEffectContext context)
    {
        if (context.EffectIndex != 0 || context.Target is not Creature pet)
        {
            return;
        }

        if (BuffOf(pet.Entry) is { } buff and not 0)
        {
            context.System.CastSpell(context.Caster, buff, SpellCastTargets.ForSelf(), triggered: true);
        }
    }
}
