using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Stealth;

/// <summary>
/// The invisibility queries by their short names (vmangos Unit.cpp:6401-6435, 6502-6541). The rules and the aura handlers live in
/// <see cref="InvisibilityAuras"/>, which the stealth feature installs with its visibility registry.
/// </summary>
public static class Invisibility
{
    /// <summary>PLAYER_FIELD_BYTE2_INVISIBILITY_GLOW (vmangos Player.h:390).</summary>
    public const byte PlayerGlow = InvisibilityAuras.GlowFlag;

    /// <summary>The invisibility types (misc values 0-31) of the unit's live auras of <paramref name="type"/>.</summary>
    public static uint Mask(SpellSystem spells, Unit unit, AuraType type) => InvisibilityAuras.Mask(spells, unit, type);

    /// <summary>Whether <paramref name="viewer"/> sees through <paramref name="target"/>'s invisibility (true when it has none).</summary>
    public static bool CanDetect(SpellSystem spells, Unit viewer, Unit target) => InvisibilityAuras.CanDetect(spells, viewer, target);

    /// <summary>
    /// Install the invisibility and detection aura handlers on <paramref name="spells"/> with <paramref name="registry"/> (a fresh one when
    /// none is given: a host without the stealth feature).
    /// </summary>
    public static void Register(SpellSystem spells, StealthRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(spells);
        spells.RegisterAura(AuraType.ModInvisibility, InvisibilityAuras.InvisibilityHandler(registry ?? new StealthRegistry()));
        spells.RegisterAura(AuraType.ModInvisibilityDetection, InvisibilityAuras.DetectionHandler());
    }
}
