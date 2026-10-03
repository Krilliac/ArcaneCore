using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Stats;

/// <summary>
/// Maximum health from stamina and maximum mana from intellect, from the player's TOTAL stat fields (level base values
/// plus items), as vmangos Player::UpdateMaxHealth and UpdateMaxPower add them (StatSystem.cpp:165-192):
/// <c>GetHealthBonusFromStamina(stamina)</c> to health and, only for a class with a mana pool (create mana above
/// zero, StatSystem.cpp:184), <c>GetManaBonusFromIntellect(intellect)</c> to mana.
/// <para>
/// The maximum fields themselves are still built from deltas (class base health and mana, item health and mana, aura
/// percentages), so the bonus is kept as a ledger on <see cref="PlayerStatState"/>: each update moves the maximum by
/// the difference between the bonus the stat fields call for and the bonus already included. That makes it
/// idempotent and lets the level application and the item hook both keep the stat fields moving without knowing
/// about the bonus. A lowered maximum clamps the current value, as Unit::SetMaxHealth and SetMaxPower do.
/// </para>
/// </summary>
internal static class StatBonuses
{
    /// <summary>Bring the health and mana maximums in line with the current stamina and intellect.</summary>
    public static void Update(Player player)
    {
        PlayerStatState state = player.StatState;

        uint healthBonus = (uint)StatFormulas.HealthBonusFromStamina(player.GetUInt32(UpdateFields.UnitFieldStat0 + 2));
        if (healthBonus != state.HealthBonusIncluded)
        {
            long max = (long)player.MaxHealth + healthBonus - state.HealthBonusIncluded;
            state.HealthBonusIncluded = healthBonus;
            player.MaxHealth = (uint)Math.Clamp(max, 1L, uint.MaxValue);
            if (player.Health > player.MaxHealth)
            {
                player.Health = player.MaxHealth;
            }
        }

        uint manaBonus = player.GetUInt32(UpdateFields.UnitFieldBaseMana) > 0
            ? (uint)StatFormulas.ManaBonusFromIntellect(player.GetUInt32(UpdateFields.UnitFieldStat0 + 3))
            : 0;
        if (manaBonus != state.ManaBonusIncluded)
        {
            int maxField = UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Mana;
            long max = (long)player.GetUInt32(maxField) + manaBonus - state.ManaBonusIncluded;
            state.ManaBonusIncluded = manaBonus;
            player.SetUInt32(maxField, (uint)Math.Clamp(max, 0L, uint.MaxValue));
            int powerField = UpdateFields.UnitFieldPower1 + (int)PowerType.Mana;
            if (player.GetUInt32(powerField) > player.GetUInt32(maxField))
            {
                player.SetUInt32(powerField, player.GetUInt32(maxField));
            }
        }
    }
}
