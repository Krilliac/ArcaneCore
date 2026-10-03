namespace ArcaneCore.Game.Spells.Interrupts;

/// <summary>
/// Spell.dbc AuraInterruptFlags bit values (D:\refs\vmangos\src\game\Spells\SpellDefines.h:575-599,
/// SpellAuraInterruptFlags), including the bits <see cref="SpellAuraInterruptFlags"/> does not name. Kept as
/// plain constants so the existing enum is not edited.
/// </summary>
public static class AuraInterruptMasks
{
    public const uint HostileActionReceivedCancels = 0x00000001;
    public const uint DamageCancels = 0x00000002;
    public const uint ActionCancels = 0x00000004;
    public const uint MovingCancels = 0x00000008;
    public const uint TurningCancels = 0x00000010;
    public const uint DismountCancels = 0x00000040;

    /// <summary>Removed by entering deep liquid (Travel Form, mounts, Food, Drink).</summary>
    public const uint UnderWaterCancels = 0x00000080;

    /// <summary>Removed by leaving deep liquid (Aquatic Form).</summary>
    public const uint AboveWaterCancels = 0x00000100;

    public const uint ShapeshiftingCancels = 0x00008000;
    public const uint ActionCancelsLate = 0x00010000;
    public const uint MountCancels = 0x00020000;
}
