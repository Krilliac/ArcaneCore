using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>vmangos Unit::HasAuraType: any aura holder on <paramref name="unit"/> carries an aura of <paramref name="type"/>.</summary>
    public bool HasAuraType(Unit unit, AuraType type)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return GetAuras(unit).Any(h => !h.IsRemoved && h.Auras.Any(a => a is not null && a.Type == type));
    }

    /// <summary>
    /// vmangos Unit::InterruptSpellsCastedOnMe (Unit.cpp:10181-10215): cancel the casts other units are currently preparing (with a cast
    /// time) or channeling at <paramref name="target"/>. Friendly casters are spared unless <paramref name="interruptPositiveSpells"/>;
    /// a caster whose Hunter's Mark is on the target is spared with <paramref name="onlyIfNotStalked"/>. In-flight delayed spells
    /// (<c>killDelayed</c>) do not exist here: this system has no travel time yet.
    /// </summary>
    public void InterruptSpellsCastedOnMe(Unit target, bool interruptPositiveSpells = false, bool onlyIfNotStalked = true)
    {
        ArgumentNullException.ThrowIfNull(target);
        foreach (UnitSpellState state in _states.Values.ToArray())
        {
            if (ReferenceEquals(state.Unit, target) || state.CurrentCast is not { } cast || cast.Targets.Unit != target.Guid)
            {
                continue;
            }

            if (!interruptPositiveSpells && Relations.IsFriendly(target, state.Unit))
            {
                continue;
            }

            if (onlyIfNotStalked && GetAuras(target).Any(h => !h.IsRemoved && h.CasterGuid == state.Unit.Guid
                && h.Auras.Any(a => a is not null && a.Type == AuraType.ModStalked)))
            {
                continue;
            }

            if ((cast.State == SpellCastState.Preparing && cast.CastTime > 0) || cast.State == SpellCastState.Casting)
            {
                Cancel(cast);
            }
        }
    }
}
