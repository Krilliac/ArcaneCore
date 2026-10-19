using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// vmangos Unit::SetTransformScale / ResetTransformScale / GetNativeScale (Unit.cpp:10967-10991): a transformation sets the
/// model scale by a ratio against the scale the previous transformation left (m_nativeScaleOverride), so other scale effects
/// (Mod Scale auras) stay multiplied in, and a reset returns to the native scale. The native scale is the unit's scale when it is
/// first touched with the factor of its live Mod Scale auras taken out (vmangos reads it from the race or creature model, which no
/// aura changes). Bounding radius and combat reach follow the scale by the same ratio (Unit::UpdateModelData, Unit.cpp:9364-9393).
/// </summary>
public static class TransformScale
{
    private sealed class State(float native)
    {
        public float Native { get; } = native;

        public float Override { get; set; } = native;

        /// <summary>Whether a transformation's scale is applied (<see cref="Set"/>), not the native one (<see cref="Reset"/>, or never set).</summary>
        public bool Transformed { get; set; }
    }

    private static readonly ConditionalWeakTable<Unit, State> s_states = new();

    private static State Of(Unit unit, SpellSystem? spells)
    {
        if (s_states.TryGetValue(unit, out State? state))
        {
            return state;
        }

        float scale = unit.GetFloat(UpdateFields.ObjectFieldScaleX);
        float auras = spells is null ? 1f : VisualAuras.ActiveScaleFactor(spells, unit);
        state = new State(auras > 0 && float.IsFinite(auras) ? scale / auras : scale);
        s_states.AddOrUpdate(unit, state);
        return state;
    }

    /// <summary>The native (pre-transformation) scale of the unit.</summary>
    /// <param name="unit">The unit.</param>
    /// <param name="spells">The spell system whose Mod Scale auras are on the unit, or null when none can be (the factor is then 1).</param>
    public static float GetNative(Unit unit, SpellSystem? spells = null)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return Of(unit, spells).Native;
    }

    /// <summary>Set the transformation scale; a scale of 0 is refused (vmangos logs "Attempt to set transform scale to 0!").</summary>
    /// <param name="unit">The unit.</param>
    /// <param name="scale">The transformation's scale.</param>
    /// <param name="spells">The spell system whose Mod Scale auras are on the unit, or null when none can be (see <see cref="GetNative"/>).</param>
    public static void Set(Unit unit, float scale, SpellSystem? spells = null)
    {
        ArgumentNullException.ThrowIfNull(unit);
        Apply(unit, Of(unit, spells), scale, transformed: true);
    }

    /// <summary>Back to the native scale.</summary>
    public static void Reset(Unit unit, SpellSystem? spells = null)
    {
        ArgumentNullException.ThrowIfNull(unit);
        State state = Of(unit, spells);
        Apply(unit, state, state.Native, transformed: false);
    }

    /// <summary>
    /// The scale of the transformation applied to <paramref name="unit"/> (vmangos <c>m_nativeScaleOverride</c> after
    /// <c>SetTransformScale</c>), or false when none is: never set, or reset to the native scale.
    /// </summary>
    internal static bool TryGetTransformation(Unit unit, out float scale)
    {
        if (s_states.TryGetValue(unit, out State? state) && state.Transformed)
        {
            scale = state.Override;
            return true;
        }

        scale = 0f;
        return false;
    }

    private static void Apply(Unit unit, State state, float scale, bool transformed)
    {
        if (scale == 0 || state.Override == 0)
        {
            return;
        }

        float factor = scale / state.Override;
        foreach (int field in new[] { UpdateFields.ObjectFieldScaleX, UpdateFields.UnitFieldBoundingradius, UpdateFields.UnitFieldCombatreach })
        {
            unit.SetFloat(field, unit.GetFloat(field) * factor);
        }

        state.Override = scale;
        state.Transformed = transformed;
    }
}
