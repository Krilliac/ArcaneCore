using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Combat;

public sealed partial class MapCombat
{
    /// <summary>
    /// vmangos Unit::DealDamage duel prologue (Unit.cpp:762-779): a player victim that holds a duel object (pending, running or finished,
    /// vmangos tests <c>m_duel</c> only) and takes damage that would bring it to 0 hp (<c>damage + 1 >= health</c>) is flagged as ending the
    /// duel. When the dealer is the duel opponent or a unit the opponent controls (<see cref="IPlayerControlledUnit"/>), the damage is
    /// cut to health - 1 so the victim survives; any other dealer (a third player, a creature, the victim itself) is not clamped and kills.
    /// The reflected-spell clause (<c>pVictim == this &amp;&amp; reflected</c>) needs the reflected flag the spell combat rules lane owns
    /// and is not modelled. A world without a <see cref="DuelService"/> has no duels, so nothing happens.
    /// </summary>
    private bool ApplyDuelClamp(Unit attacker, Unit victim, ref uint damage)
    {
        if (victim is not Player { Duel: { } duel } || DuelService.Find(_world) is null || (ulong)damage + 1 < victim.Health)
        {
            return false;
        }

        if (ReferenceEquals(duel.Opponent, attacker) || ReferenceEquals(duel.Opponent, DuelRules.ControllingPlayer(attacker)))
        {
            damage = victim.Health > 0 ? victim.Health - 1 : 0;
        }

        return true;
    }

    /// <summary>
    /// The victim died to damage that was not clamped (Unit.cpp:825-843): "last damage from non duel opponent or opponent controlled
    /// creature": both sides stop fighting and the duel is interrupted.
    /// </summary>
    private void AfterLethalDuelDamage(Player victim)
    {
        if (DuelService.Find(_world) is not { } service || victim.Duel is not { } duel)
        {
            return;
        }

        service.CombatStopWithPets(duel.Opponent, includingCast: true);
        service.CombatStopWithPets(victim, includingCast: true);
        service.Complete(victim, DuelCompleteType.Interrupted);
    }

    /// <summary>
    /// The victim survived the clamped hit (Unit.cpp:954-969, "last damage from duel opponent"): health 1, both sides stop fighting, the loser
    /// begs (Grovel, spell 7267, triggered) and the duel completes with the victim as the loser.
    /// </summary>
    private void AfterClampedDuelDamage(Player victim)
    {
        if (DuelService.Find(_world) is not { } service || victim.Duel is not { } duel)
        {
            return;
        }

        victim.Health = 1;
        service.CombatStopWithPets(duel.Opponent, includingCast: true);
        service.CombatStopWithPets(victim, includingCast: true);
        service.Spells?.CastSpell(victim, DuelService.GrovelSpellId, SpellCastTargets.ForSelf(), triggered: true);
        service.Complete(victim, DuelCompleteType.Won);
    }
}
