namespace ArcaneCore.Game.Spells;

/// <summary>
/// All 23 Spell.dbc AuraInterruptFlags bits as raw masks (vmangos SpellDefines.h:577-599
/// SpellAuraInterruptFlags). <see cref="SpellAuraInterruptFlags"/> in SpellDefines.cs only names
/// the bits the spell system itself consumes; the interrupt dispatcher
/// (<see cref="SpellSystem.RemoveAurasWithInterruptFlags"/>) works on the full word so a data row
/// with any combination (Stealth, Vanish and Shadowmeld carry 0x3C07) behaves as the data says.
/// </summary>
public static class AuraInterruptMask
{
    /// <summary>Hit by a spell from a hostile caster.</summary>
    public const uint HostileActionReceived = 0x00000001;

    public const uint Damage = 0x00000002;

    /// <summary>Removed at the beginning of an action (spell cast, ability use).</summary>
    public const uint Action = 0x00000004;

    public const uint Moving = 0x00000008;

    public const uint Turning = 0x00000010;

    /// <summary>Used by Feign Death.</summary>
    public const uint Anim = 0x00000020;

    public const uint Dismount = 0x00000040;

    public const uint UnderWater = 0x00000080;

    public const uint AboveWater = 0x00000100;

    public const uint Sheathing = 0x00000200;

    /// <summary>Interacting with an NPC (gossip, trainer, stable master, ...).</summary>
    public const uint Interacting = 0x00000400;

    /// <summary>Interacting with a game object, or looting.</summary>
    public const uint Looting = 0x00000800;

    public const uint Attacking = 0x00001000;

    public const uint ItemUse = 0x00002000;

    /// <summary>Only assigned in channel flags.</summary>
    public const uint DamageChannelDuration = 0x00004000;

    public const uint Shapeshifting = 0x00008000;

    /// <summary>Removed at the completion of an action.</summary>
    public const uint ActionLate = 0x00010000;

    public const uint Mount = 0x00020000;

    public const uint Standing = 0x00040000;

    public const uint LeaveWorld = 0x00080000;

    public const uint StealthInvisibility = 0x00100000;

    public const uint InvulnerabilityBuff = 0x00200000;

    public const uint EnterWorld = 0x00400000;

    /// <summary>Stealth, Vanish and Shadowmeld in Spell.dbc: HostileAction|Damage|Action|Interacting|Looting|Attacking|ItemUse.</summary>
    public const uint StealthFamily = HostileActionReceived | Damage | Action | Interacting | Looting | Attacking | ItemUse;
}
