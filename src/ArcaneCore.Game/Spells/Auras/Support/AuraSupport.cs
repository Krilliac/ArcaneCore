namespace ArcaneCore.Game.Spells;

/// <summary>How far the engine implements one aura type (docs/areas/aura-engine.md).</summary>
public enum AuraSupportLevel
{
    /// <summary>The value 0, which is no aura.</summary>
    NotAnAura,

    /// <summary>A handler is registered with the spell system (<see cref="SpellSystem.HasAuraHandler"/>): the apply, remove or tick side exists.</summary>
    Handler,

    /// <summary>
    /// No handler, but code outside the aura files names the type (a stat formula, a combat rule, a cast check reads it). This
    /// says the type is referenced, not that every vmangos consumer exists: the per-type notes in the doc say which.
    /// </summary>
    Referenced,

    /// <summary>Nothing in the engine acts on the type: applying it logs the type once as unsupported and the aura only occupies a slot.</summary>
    Unsupported,
}

/// <summary>One row of the support matrix: an aura type, its level, the vmangos handler it corresponds to and who owns the gap.</summary>
/// <param name="Type">The aura type.</param>
/// <param name="Level">How far it is implemented.</param>
/// <param name="VmangosHandler">The Aura:: member vmangos dispatches the type to (SpellAuras.cpp AuraHandler[] table).</param>
/// <param name="VmangosLine">The line of that table row in SpellAuras.cpp (D:\refs\vmangos at the pinned reference).</param>
/// <param name="Consumers">Source files that reference the type, up to three (a hint, not an inventory).</param>
/// <param name="Owner">The lane or slice that owns the missing part, empty when none is scheduled.</param>
public sealed record AuraSupportEntry(AuraType Type, AuraSupportLevel Level, string VmangosHandler, int VmangosLine, string Consumers, string Owner);

/// <summary>
/// The aura support matrix: one row for every one of the 193 <see cref="AuraType"/> values (vmangos TOTAL_AURAS), so
/// "implemented or consciously unsupported" is checked rather than claimed. The rows live in AuraSupportBaseline.cs;
/// the totality test keeps them consistent with the live registrations.
/// </summary>
public static partial class AuraSupport
{
    /// <summary>Every row, ordered by aura type value.</summary>
    public static IReadOnlyList<AuraSupportEntry> Entries => s_baseline;

    /// <summary>The row of <paramref name="type"/>; every defined type has one.</summary>
    public static AuraSupportEntry Get(AuraType type) => s_baseline.First(e => e.Type == type);
}
