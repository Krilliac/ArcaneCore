using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Combat;

public sealed partial class MapCombat
{
    /// <summary>
    /// The rule that opens vmangos SpellCaster::DealDamageMods (SpellCaster.cpp:1774-1787), run on a hit before its log: nothing reaches a
    /// victim that is dead or on a taxi flight (<see cref="UnitFlags.TaxiFlight"/>, vmangos IsTaxiFlying) or a creature in evade mode, and a
    /// priest <paramref name="attacker"/> under <see cref="SpellSystem.SpiritOfRedemptionSpell"/> deals nothing ("Pas de degats sous esprit
    /// de redemption"). The whole <paramref name="damage"/> is then added to <paramref name="absorb"/>, so the log shows it absorbed, and
    /// <paramref name="damage"/> becomes 0. The aura is asked of <see cref="SpellMitigation"/>: without a spell system it never applies.
    /// Environmental damage calls this (Player::EnvironmentalDamage, Player.cpp:756, where the player is both sides).
    /// </summary>
    public void DealDamageMods(Unit attacker, Unit victim, ref uint damage, ref uint absorb)
    {
        ArgumentNullException.ThrowIfNull(attacker);
        ArgumentNullException.ThrowIfNull(victim);
        if (!IsAliveState(victim) || (victim.UnitFlags & UnitFlags.TaxiFlight) != 0 || victim is ICombatCreature { IsInEvadeMode: true }
            || (attacker.Class == Class.Priest && SpellMitigation?.HasAura(attacker, SpellSystem.SpiritOfRedemptionSpell) == true))
        {
            absorb += damage;
            damage = 0;
        }
    }
}
