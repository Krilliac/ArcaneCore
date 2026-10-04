using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Stats;

namespace ArcaneCore.Game.Spells.Druid;

/// <summary>
/// The attack power and energy parts of the druid finishers at the 1.12 build (vmangos, branches after client 1.11.2):
/// <list type="bullet">
/// <item>Ferocious Bite (22568, 22827, 22828, 22829, 31018; scripts/spells/spell_druid.cpp:23-72): effect 0 gets
/// <c>attack power * combo points * 0.03</c> and the remaining energy times the effect's damage multiplier (1, 1.5, 2, 2.5,
/// 2.7 by rank), and a hit sets the energy to 0. Each term is added to the integer damage with its own truncation.</item>
/// <item>Rip (family Druid, flag bit 23, periodic damage; SpellAuras.cpp:4341-4363, 4420-4438): the tick amount gets
/// <c>attack power * min(combo points, 4) / 100</c> once, when the aura is created.</item>
/// </list>
/// Both read the combo points while the effect values are computed, which is before the combo finish observer spends them.
/// Register it after <see cref="ComboPointService.Install"/> so the combo value scaling comes first.
/// </summary>
public sealed class DruidFinisherScripts : ISpellValueModifier, ISpellCastObserver
{
    /// <summary>Ferocious Bite ranks 1 to 5.</summary>
    public static readonly uint[] FerociousBiteSpells = [22568, 22827, 22828, 22829, 31018];

    /// <summary>vmangos SPELLFAMILY_DRUID and CF_DRUID_RIP_BITE (bit 23), SpellFamilyFlags 0x800000.</summary>
    public const uint DruidFamily = 7;

    public const int RipBiteFlagBit = 23;

    /// <summary>Attack power per combo point of a Ferocious Bite (spell_druid.cpp:44).</summary>
    public const float FerociousBiteAttackPowerPerCombo = 0.03f;

    private readonly ComboPointService _combos;

    private DruidFinisherScripts(ComboPointService combos) => _combos = combos;

    /// <summary>Install the scripts on <paramref name="spells"/>, which must already have the combo point service installed.</summary>
    public static DruidFinisherScripts Install(SpellSystem spells, ComboPointService combos)
    {
        ArgumentNullException.ThrowIfNull(spells);
        ArgumentNullException.ThrowIfNull(combos);
        var scripts = new DruidFinisherScripts(combos);
        spells.RegisterValueModifier(scripts);
        spells.RegisterObserver(scripts);
        return scripts;
    }

    public static bool IsFerociousBite(SpellInfo spell) => Array.IndexOf(FerociousBiteSpells, spell.Id) >= 0;

    public static bool IsRip(SpellInfo spell)
        => spell.IsFitToFamily(DruidFamily, RipBiteFlagBit) && !IsFerociousBite(spell) && spell.HasAura(AuraType.PeriodicDamage);

    /// <summary>Unit::GetTotalAttackPowerValue(BASE_ATTACK) (Unit.cpp:8037-8061) from the unit's fields.</summary>
    private static float MeleeAttackPower(Player player) => StatFormulas.TotalAttackPower(
        player.GetInt32(UpdateFields.UnitFieldAttackPower),
        (short)player.GetUInt16(UpdateFields.UnitFieldAttackPowerMods, 0),
        (short)player.GetUInt16(UpdateFields.UnitFieldAttackPowerMods, 1),
        player.GetFloat(UpdateFields.UnitFieldAttackPowerMultiplier));

    public int Modify(SpellValueKind kind, in SpellValueContext context, int value)
    {
        if (kind != SpellValueKind.EffectValue || context.Caster is not Player player)
        {
            return value;
        }

        SpellInfo spell = context.Spell;
        if (context.EffectIndex == 0 && IsFerociousBite(spell) && context.Target is not null)
        {
            int combo = _combos.GetComboPoints(player);
            if (combo != 0)
            {
                value = (int)(value + (MeleeAttackPower(player) * combo * FerociousBiteAttackPowerPerCombo));
            }

            return (int)(value + (SpellSystem.GetPower(player, PowerType.Energy) * spell.Effects[0].DamageMultiplier));
        }

        if (IsRip(spell) && spell.Effects[context.EffectIndex].AuraType == AuraType.PeriodicDamage)
        {
            int combo = Math.Min(_combos.GetComboPoints(player), RipDamageRules.MaxComboPointsForAttackPowerTerm);
            return (int)(value + (MeleeAttackPower(player) * combo / 100));
        }

        return value;
    }

    /// <summary>A Ferocious Bite that hit spends all the energy (spell_druid.cpp:68).</summary>
    public void OnTargetOutcome(SpellCast cast, SpellTargetOutcome outcome)
    {
        if (cast.Caster is Player player && IsFerociousBite(cast.Spell) && (outcome.EffectMask & 1) != 0 && outcome.Miss == SpellMissInfo.None)
        {
            SpellSystem.SetPower(player, PowerType.Energy, 0);
        }
    }
}
