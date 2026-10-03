namespace ArcaneCore.Game.Spells.Rules;

/// <summary>
/// Spell.dbc mechanic ids (vmangos SpellDefines.h:659-695 enum Mechanics; the same numbering in
/// cmangos-classic SharedDefines.h Mechanics). 0 = none; 31 is the vmangos custom "slow cast speed".
/// </summary>
public enum SpellMechanic : uint
{
    None = 0,
    Charm = 1,
    Disoriented = 2,
    Disarm = 3,
    Distract = 4,
    Fear = 5,
    Fumble = 6,
    Root = 7,
    Pacify = 8,
    Silence = 9,
    Sleep = 10,
    Snare = 11,
    Stun = 12,
    Freeze = 13,
    Knockout = 14,
    Bleed = 15,
    Bandage = 16,
    Polymorph = 17,
    Banish = 18,
    Shield = 19,
    Shackle = 20,
    Mount = 21,
    Persuade = 22,
    Turn = 23,
    Horror = 24,
    Invulnerability = 25,
    Interrupt = 26,
    Daze = 27,
    Discovery = 28,
    ImmuneShield = 29,
    Sapped = 30,
    SlowCastSpeed = 31,
}

/// <summary>Mechanic bit-mask helpers (vmangos: <c>1 &lt;&lt; (mechanic - 1)</c>, SpellDefines.h:697-707 masks).</summary>
public static class SpellMechanics
{
    /// <summary>vmangos FIRST_MECHANIC.</summary>
    public const uint First = 1;

    /// <summary>vmangos MAX_MECHANIC.</summary>
    public const uint Max = 31;

    /// <summary>The mask bit of <paramref name="mechanic"/> (0 for none or out of range).</summary>
    public static uint Mask(uint mechanic) => mechanic is >= First and <= Max ? 1u << (int)(mechanic - 1) : 0u;

    /// <inheritdoc cref="Mask(uint)"/>
    public static uint Mask(SpellMechanic mechanic) => Mask((uint)mechanic);

    /// <summary>vmangos IMMUNE_TO_ROOT_AND_SNARE_MASK.</summary>
    public static uint RootAndSnareMask => Mask(SpellMechanic.Root) | Mask(SpellMechanic.Snare);

    /// <summary>vmangos IMMUNE_TO_ROOT_AND_STUN_MASK.</summary>
    public static uint RootAndStunMask => Mask(SpellMechanic.Root) | Mask(SpellMechanic.Stun);

    /// <summary>vmangos CONFUSED_MECHANIC_MASK (disoriented | polymorph).</summary>
    public static uint ConfusedMask => Mask(SpellMechanic.Disoriented) | Mask(SpellMechanic.Polymorph);
}
