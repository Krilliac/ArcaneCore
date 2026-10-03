namespace ArcaneCore.Game.Spells.Druid;

/// <summary>
/// Form-dependent druid combat numbers, ported from vmangos (primary reference; mangos-classic uses a different,
/// TBC-leaning formula and is documented as a difference only):
/// attack power StatSystem.cpp:194-296 (Unit::GetAttackPowerFromStrengthAndAgility), min/max damage
/// StatSystem.cpp:354-435 (Player::CalculateMinMaxDamage, IsAttackSpeedOverridenShapeShift branch, build after
/// 1.6.1), attack time Player.cpp:18271-18312 (InitDataForForm), speed multiplier SpellCaster.cpp:1826-1851.
/// Modifier layers the stat system owns (BASE_VALUE, BASE_PCT, TOTAL_VALUE, TOTAL_PCT) are inputs, not
/// reimplemented here.
/// </summary>
public static class FeralFormulas
{
    public const int CatAttackTimeMs = 1000;
    public const int BearAttackTimeMs = 2500;

    /// <summary>
    /// Melee attack power. <paramref name="predatoryStrikesAmount"/> is the amount of the Dummy aura with
    /// SpellIconID 1563 (50/100/150, read only while in cat, bear or dire bear; pass 0 if the player has none).
    /// Caster form none: str*2-20. Cat adds agility (patch 1.7).
    /// </summary>
    public static float AttackPower(byte level, float strength, float agility, byte form, int predatoryStrikesAmount)
    {
        float levelMult = DruidForms.IsAttackSpeedOverridden(form) ? predatoryStrikesAmount / 100.0f : 0.0f;
        return form switch
        {
            DruidForms.Cat => (level * levelMult) + (strength * 2.0f) + agility - 20.0f,
            DruidForms.Bear or DruidForms.DireBear => (level * levelMult) + (strength * 2.0f) - 20.0f,
            _ => (strength * 2.0f) - 20.0f,
        };
    }

    /// <summary>Ranged attack power for a druid: 0 in cat/bear/dire bear, otherwise agility - 10 (StatSystem.cpp:201-217).</summary>
    public static float RangedAttackPower(float agility, byte form) =>
        DruidForms.IsAttackSpeedOverridden(form) ? 0.0f : agility - 10.0f;

    /// <summary>Base attack time written by InitDataForForm for both hands, or null to restore the regular time.</summary>
    public static int? AttackTimeMs(byte form) => form switch
    {
        DruidForms.Cat => CatAttackTimeMs,
        DruidForms.Bear or DruidForms.DireBear => BearAttackTimeMs,
        _ => null,
    };

    /// <summary>
    /// Feral min/max damage for damage index <paramref name="index"/> (StatSystem.cpp:381-411,432-433) with
    /// unit modifiers at identity (base_pct = total_pct = 1, no total_value or physical aura bonus - feral forms
    /// zero total_value to drop weapon enchants). Weapon damage is replaced by level (capped at 60) x 0.85..1.25 x
    /// attack speed; index above 0 gets zero weapon damage and, like the source, no base value.
    /// </summary>
    public static (float Min, float Max) DamageRange(byte level, float attackPower, float attackSpeed, int index)
    {
        if (index > 0)
        {
            return (0.0f, 0.0f);
        }

        float lvl = Math.Min(level, (byte)60);
        float baseValue = attackPower / 14.0f * attackSpeed;
        return (baseValue + (lvl * 0.85f * attackSpeed), baseValue + (lvl * 1.25f * attackSpeed));
    }

    /// <summary>GetAPMultiplier for a non-normalised swing: attack time / 1000 (SpellCaster.cpp:1832-1833).</summary>
    public static float AttackSpeed(int attackTimeMs) => attackTimeMs / 1000.0f;
}
