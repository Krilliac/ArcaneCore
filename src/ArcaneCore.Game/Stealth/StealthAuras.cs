using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Stealth;

/// <summary>
/// SPELL_AURA_MOD_STEALTH (vmangos Aura::HandleModStealth, SpellAuras.cpp:3631-3693): applying it breaks the auras that carry
/// STEALTH_INVIS_CANCELS (BG flag carriers), raises the client stealth flags, enters the stealth visibility group (everyone who
/// is not exempt loses sight of the unit at once) and cancels the hostile casts in progress at the unit. Removing the last
/// MOD_STEALTH aura clears the flags and makes the unit visible again.
/// <para>
/// Not modelled here: the GM-invisibility guard (VISIBILITY_OFF; the gm-commands lane owns it),
/// the Silithus flag drop (29519) and the cancel-removes-Vanish rule (RG-04 in docs/areas/rogue.md).
/// </para>
/// </summary>
public static class StealthAuras
{
    /// <summary>UNIT_FIELD_BYTES_1 byte 3 (vmangos UNIT_BYTES_1_OFFSET_VIS_FLAG).</summary>
    public const int VisFlagByte = 3;

    /// <summary>vmangos UNIT_VIS_FLAGS_CREEP: applied by SPELL_AURA_MOD_STEALTH.</summary>
    public const byte VisFlagCreep = 0x02;

    /// <summary>PLAYER_FIELD_BYTES2 byte 1 (vmangos PLAYER_FIELD_BYTES_2_OFFSET_FLAGS).</summary>
    public const int PlayerFlagsByte = 1;

    /// <summary>vmangos PLAYER_FIELD_BYTE2_STEALTH.</summary>
    public const byte PlayerStealthFlag = 0x20;

    /// <summary>The aura handler for SPELL_AURA_MOD_STEALTH.</summary>
    public static AuraHandler Handler(StealthRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return new AuraHandler((spells, holder, _, apply) =>
        {
            if (apply)
            {
                Apply(spells, registry, holder.Target);
            }
            else
            {
                Remove(spells, registry, holder.Target);
            }
        }, null);
    }

    private static void Apply(SpellSystem spells, StealthRegistry registry, Unit target)
    {
        // "drop flag at stealth in bg"
        spells.RemoveAurasWithInterruptFlags(target, AuraInterruptMask.StealthInvisibility);

        SetByteFlag(target, UpdateFields.UnitFieldBytes1, VisFlagByte, VisFlagCreep);
        if (target is Player)
        {
            SetByteFlag(target, UpdateFields.PlayerFieldBytes2, PlayerFlagsByte, PlayerStealthFlag);
        }

        // vmangos SetVisibility(NO_DETECT) then SetVisibility(STEALTH), each running UpdateVisibilityAndView: the first pass hides the
        // unit from every viewer (a unit that just stealthed ignores old detected state), the second leaves it hidden until detected.
        registry.SetVisibility(target, StealthVisibility.NoDetect);
        target.Map?.RefreshVisibility(target);
        registry.SetVisibility(target, StealthVisibility.Stealth);
        target.Map?.RefreshVisibility(target);

        spells.InterruptSpellsCastedOnMe(target, interruptPositiveSpells: false, onlyIfNotStalked: true);
    }

    private static void Remove(SpellSystem spells, StealthRegistry registry, Unit target)
    {
        // only at the removal of the last SPELL_AURA_MOD_STEALTH
        if (spells.HasAuraType(target, AuraType.ModStealth))
        {
            return;
        }

        RemoveByteFlag(target, UpdateFields.UnitFieldBytes1, VisFlagByte, VisFlagCreep);
        if (target is Player)
        {
            RemoveByteFlag(target, UpdateFields.PlayerFieldBytes2, PlayerFlagsByte, PlayerStealthFlag);
        }

        if (spells.HasAuraType(target, AuraType.ModInvisibility))
        {
            // vmangos SpellAuras.cpp:3698-3705 restores invisibility after the last stealth aura fades.
            registry.SetVisibility(target, StealthVisibility.NoDetect);
            target.Map?.RefreshVisibility(target);
            registry.SetVisibility(target, StealthVisibility.Invisibility);
        }
        else
        {
            registry.SetVisibility(target, StealthVisibility.On);
        }

        target.Map?.RefreshVisibility(target);
    }

    private static void SetByteFlag(Unit unit, int index, int offset, byte flag)
        => unit.SetByte(index, offset, (byte)(unit.GetByte(index, offset) | flag));

    private static void RemoveByteFlag(Unit unit, int index, int offset, byte flag)
        => unit.SetByte(index, offset, (byte)(unit.GetByte(index, offset) & ~flag));
}
