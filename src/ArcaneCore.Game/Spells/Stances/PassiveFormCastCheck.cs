using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// A form-bound passive spell is only cast on a player in its form: vmangos Player::IsNeedCastPassiveLikeSpellAtLearn
/// (Player.cpp:3748-3763), the gate of every learned or login-time passive cast (Player::AddSpell, _LoadSpells). Without
/// it, a talent passive such as Feline Swiftness (17002, Stances = Cat) or Sharpened Claws would apply in humanoid form.
/// </summary>
/// <remarks>
/// Applies to triggered casts of a passive spell by a <see cref="Player"/> (the learn and login casts are triggered
/// self casts); everything else passes. The form-apply path (<see cref="ShapeshiftService"/> casting the passives of the
/// new form) is not vetoed because it writes the form byte first, so <see cref="SpellInfo.IsNeedCastSpellAtFormApply"/> holds.
/// The trailing CasterAuraState clause of the vmangos function is already enforced for every cast by
/// <c>CasterAuraStateCheck</c> (CASTER_AURASTATE). A veto reports <see cref="SpellCastResult.OnlyShapeshift"/>, which is
/// never sent to a client (triggered casts and passives are silent). In vmangos' own words pre-3.x data has no passive
/// with ALLOW_WHILE_NOT_SHAPESHIFTED (SpellDefines.h:925), so that branch is only literal.
/// </remarks>
/// <param name="forms">The form table to read flags from (a null result means <see cref="ShapeshiftFormCatalog.Retail"/>).</param>
public sealed class PassiveFormCastCheck(Func<ShapeshiftFormCatalog?> forms) : ISpellCastCheck
{
    /// <summary>vmangos SPELL_ATTR_EX_CAST_WHEN_LEARNED (SpellDefines.h:901): AttributesEx bit 31.</summary>
    private const uint CastWhenLearned = 0x80000000;

    private readonly Func<ShapeshiftFormCatalog?> _forms = forms ?? throw new ArgumentNullException(nameof(forms));

    public SpellCheckPhase Phase => SpellCheckPhase.Caster;

    /// <summary>Just before the stance check (which does not run for triggered casts anyway).</summary>
    public int Order => SpellCastCheckOrder.Shapeshift - 1;

    public SpellCastResult Check(in SpellCastCheckContext context)
    {
        if (!context.Triggered || context.Caster is not Player player || !context.Spell.IsPassive)
        {
            return SpellCastResult.CastOk;
        }

        return IsNeedCastPassiveLikeSpellAtLearn(context.Spell, FormQueries.GetForm(player), _forms())
            ? SpellCastResult.CastOk
            : SpellCastResult.OnlyShapeshift;
    }

    /// <summary>
    /// vmangos Player::IsNeedCastPassiveLikeSpellAtLearn for a passive spell and the player's form (the CasterAuraState
    /// tail is checked separately, see the type remarks).
    /// </summary>
    public static bool IsNeedCastPassiveLikeSpellAtLearn(SpellInfo spell, byte form, ShapeshiftFormCatalog? catalog)
    {
        ArgumentNullException.ThrowIfNull(spell);
        if (((uint)spell.AttributesEx & CastWhenLearned) != 0)
        {
            uint? flags = form != 0 && (catalog ?? ShapeshiftFormCatalog.Retail).TryGet(form, out ShapeshiftFormInfo info) ? info.Flags1 : null;
            if (spell.GetErrorAtShapeshiftedCast(form, flags) == SpellCastResult.CastOk)
            {
                return true;
            }
        }

        if (spell.IsNeedCastSpellAtFormApply(form))
        {
            return true;
        }

        if (!spell.IsPassive)
        {
            return false;
        }

        // note: form passives activated with shapeshift spells are cast by HandleShapeshiftBoosts instead.
        return spell.Stances == 0 || (form == 0 && spell.HasAttribute(SpellAttributesEx2Combat.AllowWhileNotShapeshifted));
    }
}
