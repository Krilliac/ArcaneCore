using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    private static bool IsMoving(Player player)
        => (player.Movement.Flags & (MovementFlags.Forward | MovementFlags.Backward | MovementFlags.StrafeLeft
            | MovementFlags.StrafeRight | MovementFlags.Jumping | MovementFlags.FallingFar)) != 0;

    internal static float CastSpeed(Unit unit)
    {
        float speed = unit.GetFloat(UpdateFields.UnitModCastSpeed);
        return speed > 0 ? speed : 1.0f;
    }

    private static float CombatReach(Unit unit)
    {
        float reach = unit.GetFloat(UpdateFields.UnitFieldCombatreach);
        return reach > 0 ? reach : SpellConstants.DefaultCombatReach;
    }

    private static float Distance3D(Unit unit, float x, float y, float z)
    {
        float dx = unit.X - x;
        float dy = unit.Y - y;
        float dz = unit.Z - z;
        return MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    /// <summary>UNIT_FIELD_POWER1 + power type (vmangos Unit::GetPower).</summary>
    public static uint GetPower(Unit unit, PowerType power)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return ArcaneCore.Game.Combat.MapCombat.GetPower(unit, power);
    }

    /// <summary>vmangos Unit::SetPower: clamped to UNIT_FIELD_MAXPOWER1 + power type.</summary>
    public static void SetPower(Unit unit, PowerType power, uint value)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArcaneCore.Game.Combat.MapCombat.SetPower(unit, power, value);
    }
}
