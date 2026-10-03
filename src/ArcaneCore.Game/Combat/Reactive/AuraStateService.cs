using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Combat;

/// <summary>
/// Unit aura states (UNIT_FIELD_AURASTATE, vmangos Unit::ModifyAuraState / HasAuraState, Unit.cpp:4682-4745): one bit per
/// state, bit (state - 1). Spells name a state they need on the caster (<see cref="SpellInfo.CasterAuraState"/>: Revenge needs
/// Defense) or the target (Execute needs Healthless20Percent).
/// </summary>
public sealed class AuraStateService
{
    private readonly SpellSystem _spells;
    private readonly Func<Player, IEnumerable<uint>> _knownSpells;

    /// <param name="spells">The spell system (passives bound to a state are cast, auras that need it are removed).</param>
    /// <param name="knownSpells">A player's spellbook (vmangos Player::GetSpellMap).</param>
    public AuraStateService(SpellSystem spells, Func<Player, IEnumerable<uint>> knownSpells)
    {
        _spells = spells ?? throw new ArgumentNullException(nameof(spells));
        _knownSpells = knownSpells ?? throw new ArgumentNullException(nameof(knownSpells));
    }

    private static uint Bit(AuraState state) => 1u << ((int)state - 1);

    /// <summary>vmangos Unit::HasAuraState.</summary>
    public bool HasAuraState(Unit unit, AuraState state)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return state != AuraState.None && (unit.GetUInt32(UpdateFields.UnitFieldAurastate) & Bit(state)) != 0;
    }

    /// <summary>
    /// vmangos Unit::ModifyAuraState. Setting a state a player did not have casts every passive spell they know that needs it
    /// as caster state; clearing one removes every aura on the unit whose spell needs it (the old-client Berserking state
    /// excepted).
    /// </summary>
    public void ModifyAuraState(Unit unit, AuraState state, bool apply)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (state == AuraState.None)
        {
            return;
        }

        bool has = HasAuraState(unit, state);
        if (apply)
        {
            if (has)
            {
                return;
            }

            unit.SetUInt32(UpdateFields.UnitFieldAurastate, unit.GetUInt32(UpdateFields.UnitFieldAurastate) | Bit(state));
            if (unit is Player player)
            {
                foreach (uint spellId in _knownSpells(player).ToArray())
                {
                    if (_spells.Store.Get(spellId) is { IsPassive: true } spell && spell.CasterAuraState == state)
                    {
                        _spells.CastSpell(unit, spellId, SpellCastTargets.ForSelf(), triggered: true);
                    }
                }
            }

            return;
        }

        if (!has)
        {
            return;
        }

        unit.SetUInt32(UpdateFields.UnitFieldAurastate, unit.GetUInt32(UpdateFields.UnitFieldAurastate) & ~Bit(state));
        if (state == AuraState.Berserking)
        {
            return;
        }

        foreach (SpellAuraHolder holder in _spells.GetAuras(unit).Where(h => !h.IsRemoved && h.Spell.CasterAuraState == state).ToArray())
        {
            _spells.RemoveAuras(unit, holder.Spell.Id);
        }
    }

    /// <summary>
    /// The 20%-health state (vmangos Unit::Update, Unit.cpp:318-319): set while an alive unit has less than a fifth of its
    /// maximum health, so Execute works on it. Called every tick for units that can be targeted.
    /// </summary>
    public void UpdateHealthState(Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (unit.IsAlive)
        {
            ModifyAuraState(unit, AuraState.Healthless20Percent, unit.Health < unit.MaxHealth * 0.20f);
        }
    }

    /// <summary>A unit died: the health state goes with it (vmangos Unit::SetDeathState, Unit.cpp:7357).</summary>
    public void ClearHealthStates(Unit unit) => ModifyAuraState(unit, AuraState.Healthless20Percent, false);
}

/// <summary>
/// Cast checks of the aura states: the caster state of any spell (vmangos Spell::CheckCast, Spell.cpp:5392-5393) and the
/// one target state vmangos checks, the 20% state of Execute (Spell.cpp:5733-5742).
/// </summary>
public static class AuraStateCastChecks
{
    /// <summary>Install both checks on <paramref name="spells"/>.</summary>
    public static void Install(SpellSystem spells, AuraStateService states)
    {
        ArgumentNullException.ThrowIfNull(spells);
        ArgumentNullException.ThrowIfNull(states);
        spells.RegisterCastCheck(new CasterAuraStateCheck(states));
        spells.RegisterCastCheck(new TargetHealthStateCheck(states));
    }
}

/// <summary>vmangos: the caster must be in the spell's <see cref="SpellInfo.CasterAuraState"/> (CASTER_AURASTATE otherwise); applies to triggered casts too.</summary>
public sealed class CasterAuraStateCheck(AuraStateService states) : ISpellCastCheck
{
    public SpellCheckPhase Phase => SpellCheckPhase.Caster;

    public int Order => SpellCastCheckOrder.CasterAuraState;

    public SpellCastResult Check(in SpellCastCheckContext context)
        => context.Spell.CasterAuraState != AuraState.None && !states.HasAuraState(context.Caster, context.Spell.CasterAuraState)
            ? SpellCastResult.CasterAurastate
            : SpellCastResult.CastOk;
}

/// <summary>
/// vmangos: "all spells that require target to be below 20% have this" (Spell.cpp:5733-5742): no explicit target is
/// BAD_IMPLICIT_TARGETS, a target above 20% BAD_TARGETS. The other target states are not checked by vmangos.
/// </summary>
public sealed class TargetHealthStateCheck(AuraStateService states) : ISpellCastCheck
{
    public SpellCheckPhase Phase => SpellCheckPhase.Final;

    public int Order => SpellCastCheckOrder.TargetAuraState;

    public SpellCastResult Check(in SpellCastCheckContext context)
    {
        if (context.Spell.TargetAuraState != AuraState.Healthless20Percent)
        {
            return SpellCastResult.CastOk;
        }

        if (context.Target is null)
        {
            return SpellCastResult.BadImplicitTargets;
        }

        return states.HasAuraState(context.Target, AuraState.Healthless20Percent) ? SpellCastResult.CastOk : SpellCastResult.BadTargets;
    }
}
