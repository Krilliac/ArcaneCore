using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells.Mods;

/// <summary>
/// vmangos <c>Aura::HandleAddModifier</c> (SpellAuras.cpp:1081-1111) for aura types 107 and 108: only a player holds a
/// modifier, an operation at or above <see cref="SpellModOp.Max"/> is ignored, applying creates the mod and removing takes
/// the same one away, then the affected passive auras are refreshed.
/// <para>
/// Charges (:1100-1105): a spell with StackAmount above 1 never carries charges ("all this spell expected expire not at use but at
/// spell proc event check"), Shadow Trance and Netherwind Focus get one (<see cref="SpellModOptions.CustomCharges"/>), anything else
/// starts with the holder's charges (Spell.dbc procCharges, 0 = unlimited). The casts that use the mod spend them
/// (<see cref="SpellModScope"/>); the holder's own charge count is not touched, exactly as in vmangos.
/// </para>
/// </summary>
internal sealed class SpellModAuraHandlers(SpellSystem system, SpellModEngine engine)
{
    private readonly ConditionalWeakTable<SpellAura, SpellMod> _live = new();

    public void OnAura(SpellSystem spells, SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        if (holder.Target is not Player player || aura.MiscValue < 0 || aura.MiscValue >= (int)SpellModOp.Max)
        {
            return;
        }

        if (apply)
        {
            if (!engine.Options.Enabled || _live.TryGetValue(aura, out _))
            {
                return;
            }

            if (engine.Options.CustomCharges && holder.Spell.Id is 17941 or 22008)
            {
                holder.Charges = 1;
            }

            int charges = holder.Spell.StackAmount > 1 ? 0 : holder.Charges;
            var mod = new SpellMod((SpellModOp)aura.MiscValue, (SpellModType)(int)aura.Type, aura.Amount,
                engine.ClassMask(holder.Spell, aura.EffectIndex), holder.Spell.SpellFamilyName, holder.Spell.Id, aura.EffectIndex, charges);
            _live.Add(aura, mod);
            engine.Add(player, mod);
            PassiveReapply.Run(system, engine, player, mod, holder.Spell);
        }
        else if (_live.TryGetValue(aura, out SpellMod? mod))
        {
            _live.Remove(aura);
            engine.Remove(player, mod);
            PassiveReapply.Run(system, engine, player, mod, holder.Spell);
        }
    }
}
