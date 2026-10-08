namespace ArcaneCore.Game.Spells;

/// <summary>
/// The aura types the combat tables and the weapon damage bonuses read when they roll or deal damage, which have nothing to do when they are
/// applied or removed: vmangos dispatches
/// them to <c>Aura::HandleNoImmediateEffect</c> (or, for MOD_SPELL_HIT_CHANCE, to a handler that only keeps the running sum
/// <c>m_modSpellHitChance</c> the roll reads; SpellAuras.cpp:5051-5054), and the engine reads the live aura list at the roll instead. They are
/// registered here so applying one is a supported, silent no-op rather than a once-logged unsupported type, and so the support matrix and
/// the talent coverage report see them as handled.
/// <list type="table">
/// <item><term>MOD_HIT_CHANCE (54)</term><description>weapon-checked attacker +hit, <c>MapCombat.BuildRollInput</c> and the melee/ranged spell table
/// (SpellCaster::GetMeleeMissChance, SpellCaster.cpp:389-390).</description></item>
/// <item><term>MOD_SPELL_HIT_CHANCE (55), MOD_ATTACKER_SPELL_HIT_CHANCE (186)</term><description>the magic hit chance,
/// <see cref="VanillaSpellCombatRules.MagicHitPercent"/> (SpellCaster::MagicSpellHitChance, :845-866).</description></item>
/// <item><term>MOD_ATTACKER_MELEE_HIT_CHANCE (184), MOD_ATTACKER_RANGED_HIT_CHANCE (185)</term><description>the victim's side of the miss chance
/// (:414-418).</description></item>
/// <item><term>MOD_ATTACKER_MELEE_CRIT_CHANCE (187), MOD_ATTACKER_RANGED_CRIT_CHANCE (188)</term><description>the victim's side of the crit chance
/// (Unit::GetUnitCriticalChance, Unit.cpp:2577-2581).</description></item>
/// <item><term>MOD_DAMAGE_DONE_CREATURE (59), MOD_MELEE_ATTACK_POWER_VERSUS (102), RANGED_ATTACK_POWER_ATTACKER_BONUS (127), MOD_RANGED_ATTACK_POWER_VERSUS
/// (131), MELEE_ATTACK_POWER_ATTACKER_BONUS (165), MOD_DAMAGE_DONE_VERSUS (168)</term><description>the done bonuses of weapon damage,
/// <see cref="Combat.MeleeDamageBonus.Done"/> (SpellCaster::MeleeDamageBonusDone, SpellCaster.cpp:1295-1455); 59 and 168 also, with
/// MOD_FLAT_SPELL_DAMAGE_VERSUS (180), the spell damage (SpellCaster::SpellDamageBonusDone, :1613-1679, <c>SpellBonusModule</c>).</description></item>
/// <item><term>MOD_DAMAGE_TAKEN (14), MOD_DAMAGE_PERCENT_TAKEN (87), MOD_RANGED_DAMAGE_TAKEN (113), MOD_RANGED_DAMAGE_TAKEN_PCT (114),
/// MOD_MELEE_DAMAGE_TAKEN (125), MOD_MELEE_DAMAGE_TAKEN_PCT (126)</term><description>the taken modifiers, <see cref="Combat.MeleeDamageBonus.Taken"/>
/// (Unit::MeleeDamageBonusTaken, Unit.cpp:5676-5749) and, for 14 and 87, the spell damage (SpellBonusModule).</description></item>
/// </list>
/// World thread only; no state.
/// </summary>
public sealed class CombatTableAuras : ISpellHandlerModule
{
    /// <summary>The types this module answers for, in aura order.</summary>
    public static IReadOnlyList<AuraType> Types { get; } =
    [
        AuraType.ModHitChance,
        AuraType.ModSpellHitChance,
        AuraType.ModAttackerMeleeHitChance,
        AuraType.ModAttackerRangedHitChance,
        AuraType.ModAttackerSpellHitChance,
        AuraType.ModAttackerMeleeCritChance,
        AuraType.ModAttackerRangedCritChance,
        AuraType.ModDamageTaken,
        AuraType.ModDamageDoneCreature,
        AuraType.ModDamagePercentTaken,
        AuraType.ModMeleeAttackPowerVersus,
        AuraType.ModRangedDamageTaken,
        AuraType.ModRangedDamageTakenPct,
        AuraType.ModMeleeDamageTaken,
        AuraType.ModMeleeDamageTakenPct,
        AuraType.RangedAttackPowerAttackerBonus,
        AuraType.ModRangedAttackPowerVersus,
        AuraType.MeleeAttackPowerAttackerBonus,
        AuraType.ModDamageDoneVersus,
        AuraType.ModFlatSpellDamageVersus,
    ];

    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        var noImmediateEffect = new AuraHandler(null, null);
        foreach (AuraType type in Types)
        {
            system.RegisterAura(type, noImmediateEffect);
        }
    }
}
