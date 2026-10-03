namespace ArcaneCore.Game.Spells.Druid;

/// <summary>
/// Rip's attack-power term (D:\refs\vmangos\src\game\Spells\SpellAuras.cpp:4341-4363, the build branch after
/// 1.11.2): per tick amount += attack power * min(comboPoints, 4) / 100, so a fifth point adds nothing. It is
/// added to the base amount AFTER the EffectPointsPerComboPoint scaling (SpellCaster.cpp:1180, up to 5 points),
/// once at aura application (:4420-4438), and skipped while the character is loading, so a saved aura keeps its
/// stored amount. The caller must read the combo points before the finisher clears them.
/// </summary>
public static class RipDamageRules
{
    public const int MaxComboPointsForAttackPowerTerm = 4;

    public static float AttackPowerTerm(float attackPower, int comboPoints) =>
        attackPower * Math.Min(comboPoints, MaxComboPointsForAttackPowerTerm) / 100;

    /// <summary>The per-tick amount stored on the aura: the combo-scaled base plus the attack-power term.</summary>
    public static float TickAmount(float comboScaledBaseAmount, float attackPower, int comboPoints) =>
        comboScaledBaseAmount + AttackPowerTerm(attackPower, comboPoints);
}
