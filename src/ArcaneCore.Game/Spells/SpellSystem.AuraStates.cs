using ArcaneCore.Game.Combat;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>
    /// The world's aura states (vmangos Unit::ModifyAuraState, <see cref="AuraStateService"/>), set by the world feature that owns them. Spell
    /// code that sets or clears a state goes through it, so the state's side effects happen: passive spells bound to a state are cast when it
    /// is set, auras whose spell needs it are removed when it clears. Null (tests, a bare spell system): <see cref="ModifyAuraState"/> only flips
    /// the UNIT_FIELD_AURASTATE bit.
    /// </summary>
    public AuraStateService? AuraStates { get; set; }

    /// <summary>
    /// vmangos Unit::ModifyAuraState: through <see cref="AuraStates"/> when the world installed it, else the bare bit (bit state - 1) of
    /// UNIT_FIELD_AURASTATE.
    /// </summary>
    public void ModifyAuraState(Entities.Unit unit, AuraState state, bool apply)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (AuraStates is { } states)
        {
            states.ModifyAuraState(unit, state, apply);
            return;
        }

        if (state == AuraState.None)
        {
            return;
        }

        uint bit = 1u << ((int)state - 1);
        uint current = unit.GetUInt32(UpdateFields.UnitFieldAurastate);
        unit.SetUInt32(UpdateFields.UnitFieldAurastate, apply ? current | bit : current & ~bit);
    }
}
