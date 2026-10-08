using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>
    /// vmangos Unit::AddAura(spellId, addAuraFlags, pCaster) (Objects/Unit.cpp:10511-10561): put the spell's auras on <paramref name="target"/>
    /// without a cast (no checks, no cast packets, no non-aura effects). The holder gets the spell's duration, or none at all with
    /// <paramref name="permanent"/> (ADD_AURA_PERMANENT: SpellAuraHolder::SetPermanent, so it never runs out). The caster is the target unless
    /// <paramref name="caster"/> is given. Narrower than vmangos: only SPELL_EFFECT_APPLY_AURA effects are built (vmangos also builds the
    /// area and persistent area aura effects); a spell without one adds nothing. Returns whether the holder was added.
    /// </summary>
    public bool AddAura(Unit target, uint spellId, bool permanent = false, Unit? caster = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (Store.Get(spellId) is not { } spell)
        {
            return false;
        }

        caster ??= target;
        SpellAuraHolder? holder = null;
        for (int i = 0; i < SpellConstants.MaxEffects; i++)
        {
            SpellEffectInfo effect = spell.Effects[i];
            if (effect.Effect != SpellEffectName.ApplyAura)
            {
                continue;
            }

            if (!AuraHandlers.ContainsKey(effect.AuraType))
            {
                ReportUnsupported("aura", (uint)effect.AuraType, spell.Id);
            }

            // vmangos SpellAuraHolder ctor: SpellEntry::CalculateDuration; ADD_AURA_PERMANENT then makes the holder permanent.
            holder ??= new SpellAuraHolder(spell, target, caster, _auraCasterOwners.GetValue(caster, static c => new AuraCasterOwner(c)),
                permanent ? -1 : DurationFor(caster, spell));

            // vmangos Aura ctor: the amount is the caster's CalculateSpellEffectValue (spell mods included).
            int amount = ModInt(caster, spell, SpellModOp.AllEffects,
                ModifyValue(SpellValueKind.EffectValue, caster, spell, i, spell.CalculateEffectValue(i, CasterLevelOf(caster), Random), target));
            var aura = new SpellAura(i, effect.AuraType, amount, ModifiedAmplitude(caster, spell, effect), effect.MiscValue, target.PowerType);
            aura.PeriodicTimer = PeriodicTiming.InitialTimer(spell, aura);
            holder.SetAura(aura);
        }

        if (holder is null)
        {
            return false;
        }

        AddAuraHolder(holder);
        return !holder.IsRemoved && HasAura(target, spellId);
    }
}
