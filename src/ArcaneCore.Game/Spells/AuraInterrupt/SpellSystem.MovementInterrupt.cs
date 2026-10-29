using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>
    /// vmangos Unit::HandleInterruptsOnMovement (Unit.cpp:10131-10140), the aura half: <paramref name="positionChangedOrMoving"/> removes
    /// every aura whose spell carries MOVING or TURNING in its AuraInterruptFlags (Feign Death 0x3C3C, Shadowmeld 0xBC0F, ...); false (the
    /// unit only turned) removes the TURNING ones. The cast and channel half (InterruptSpellsWithInterruptFlags MOVEMENT, InterruptSpellsWithChannelFlags MOVING_CANCELS) runs first; the cast update loop keeps its own 0.5 yd check. Runs for every movement
    /// packet, so a scan that allocates nothing decides first, and only a unit that really carries such an aura reaches
    /// <see cref="RemoveAurasWithInterruptFlags(Unit, uint, uint, bool, bool, bool, IReadOnlySet{uint}?)"/>. Returns how many auras were removed.
    /// World thread.
    /// </summary>
    public int RemoveAurasOnMovement(Unit unit, bool positionChangedOrMoving)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (GetState(unit.Guid) is not { } state || !ReferenceEquals(state.Unit, unit))
        {
            return 0;
        }

        if (positionChangedOrMoving)
        {
            // Unit.cpp:10133-10136 order: spells with the movement interrupt flag, then channels with MOVING_CANCELS. Cancel is a no-op on
            // a finished cast, so the cast update loop's own distance check finds nothing left and sends no second interrupt.
            InterruptSpellsWithInterruptFlags(unit, SpellInterruptFlags.Movement);
            InterruptChannelsWithFlags(unit, SpellAuraInterruptFlags.Moving);
        }

        uint mask = positionChangedOrMoving ? AuraInterruptMask.Moving | AuraInterruptMask.Turning : AuraInterruptMask.Turning;
        bool carries = false;
        List<SpellAuraHolder> auras = state.Auras;
        for (int i = 0; i < auras.Count; i++)
        {
            SpellAuraHolder holder = auras[i];
            if (!holder.IsRemoved && ((uint)holder.Spell.AuraInterruptFlags & mask) != 0)
            {
                carries = true;
                break;
            }
        }

        return carries ? RemoveAurasWithInterruptFlags(unit, mask) : 0;
    }
}
