using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// vmangos Unit::SetTransformScale / ResetTransformScale / GetNativeScale (Unit.cpp:10967-10991): a transformation sets the
/// model scale by a ratio against the scale the previous transformation left (m_nativeScaleOverride), so other scale effects
/// (Mod Scale auras) stay multiplied in, and a reset returns to the native scale. The scale the unit has when it is first
/// touched is its native scale (vmangos reads it from the race or creature model). Bounding radius and combat reach follow the
/// scale by the same ratio (Unit::UpdateModelData, Unit.cpp:9364-9393).
/// </summary>
public static class TransformScale
{
    private sealed class State(float native)
    {
        public float Native { get; } = native;

        public float Override { get; set; } = native;
    }

    private static readonly ConditionalWeakTable<Unit, State> s_states = new();

    private static State Of(Unit unit) => s_states.GetValue(unit, static u => new State(u.GetFloat(UpdateFields.ObjectFieldScaleX)));

    /// <summary>The native (pre-transformation) scale of the unit.</summary>
    public static float GetNative(Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return Of(unit).Native;
    }

    /// <summary>Set the transformation scale; a scale of 0 is refused (vmangos logs "Attempt to set transform scale to 0!").</summary>
    public static void Set(Unit unit, float scale)
    {
        ArgumentNullException.ThrowIfNull(unit);
        State state = Of(unit);
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
    }

    /// <summary>Back to the native scale.</summary>
    public static void Reset(Unit unit) => Set(unit, GetNative(unit));
}
