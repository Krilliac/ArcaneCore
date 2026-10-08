using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// SPELL_AURA_MOD_RESISTANCE_EXCLUSIVE (143), the paladin resistance auras (Shadow, Frost and Fire Resistance Aura) and their kind, after vmangos
/// <c>Aura::HandleAuraModResistanceExclusive</c> (SpellAuras.cpp:4503-4549): for each school of the misc mask only the strongest bonus and the
/// strongest malus of all such auras on the unit count. An aura that is stronger than every other one swaps the old best for itself when it
/// comes and back when it goes; a weaker one changes nothing. A player's resistance buff mod fields follow the change ("UI malus info").
/// Like <see cref="StatAuras"/> the resistance field is moved by deltas (there is no TOTAL_VALUE ledger); holy has no resistance field here.
/// </summary>
public sealed class ResistanceExclusiveAuras : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterAura(AuraType.ModResistanceExclusive, new AuraHandler(Apply, null));
    }

    private static void Apply(SpellSystem system, SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        if (aura.Amount == 0)
        {
            return;
        }

        Unit target = holder.Target;
        int amount = aura.Amount;
        for (int school = 0; school < 7; school++)
        {
            if (((uint)aura.MiscValue & (1u << school)) == 0)
            {
                continue;
            }

            int bonusMax = 0;
            int malusMax = 0;
            foreach (SpellAuraHolder other in system.GetAuras(target))
            {
                if (other.IsRemoved && !ReferenceEquals(other, holder))
                {
                    continue;
                }

                foreach (SpellAura? candidate in other.Auras)
                {
                    if (candidate is null || ReferenceEquals(candidate, aura) || candidate.Type != AuraType.ModResistanceExclusive
                        || ((uint)candidate.MiscValue & (1u << school)) == 0)
                    {
                        continue;
                    }

                    if (candidate.Amount > bonusMax)
                    {
                        bonusMax = candidate.Amount;
                    }
                    else if (candidate.Amount < malusMax)
                    {
                        malusMax = candidate.Amount;
                    }
                }
            }

            int change = amount > bonusMax ? amount - bonusMax
                : amount < malusMax ? amount - malusMax
                : 0;
            if (change == 0)
            {
                continue;
            }

            int delta = apply ? change : -change;
            if (school != (int)SpellSchool.Holy)
            {
                int field = UpdateFields.UnitFieldResistances + school;
                target.SetInt32(field, target.GetInt32(field) + delta);
                PercentStatAuras.RefreshResistance(target, school);
            }

            if (target is Player)
            {
                int buffField = (amount > 0 ? UpdateFields.PlayerFieldResistancebuffmodspositive : UpdateFields.PlayerFieldResistancebuffmodsnegative) + school;
                target.SetInt32(buffField, target.GetInt32(buffField) + delta);
            }
        }
    }
}
