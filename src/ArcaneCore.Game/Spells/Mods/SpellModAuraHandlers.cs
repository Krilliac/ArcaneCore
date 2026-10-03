using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells.Mods;

/// <summary>
/// vmangos <c>Aura::HandleAddModifier</c> (SpellAuras.cpp:1081-1111) for aura types 107 and 108: only a player holds a
/// modifier, an operation at or above <see cref="SpellModOp.Max"/> is ignored, applying creates the mod and removing takes
/// the same one away, then the affected passive auras are refreshed.
/// <para>
/// Charges: a spell with StackAmount above 1 never carries charges, Shadow Trance and Netherwind Focus get one
/// (<see cref="SpellModOptions.CustomCharges"/>). A mod that would carry charges is NOT registered yet: charges are consumed by the
/// cast that uses the mod, and until that consumption exists a registered charged mod would apply to every cast forever
/// (a permanent free-cast). <see cref="SpellModEngine.InertChargedMods"/> counts them.
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
            if (charges > 0)
            {
                engine.InertChargedMods++;
                return;
            }

            var mod = new SpellMod((SpellModOp)aura.MiscValue, (SpellModType)(int)aura.Type, aura.Amount,
                engine.ClassMask(holder.Spell, aura.EffectIndex), holder.Spell.SpellFamilyName, holder.Spell.Id, aura.EffectIndex);
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
