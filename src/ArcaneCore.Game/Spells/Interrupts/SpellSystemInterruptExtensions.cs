using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells.Interrupts;

/// <summary>
/// Helpers built only from the public <see cref="SpellSystem"/> API. The aura half of the interrupt family is the
/// instance method <see cref="SpellSystem.RemoveAurasWithInterruptFlags"/> (rogue lane); it is not duplicated here.
/// </summary>
public static class SpellSystemInterruptExtensions
{
    /// <summary>
    /// vmangos Unit::InterruptSpellsWithChannelFlags(flag): stop the unit's channel when its spell's
    /// ChannelInterruptFlags intersect <paramref name="mask"/>.
    /// </summary>
    public static void InterruptChannelWithFlags(this SpellSystem system, Unit unit, uint mask)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(unit);
        uint channelSpell = unit.GetUInt32(UpdateFields.UnitChannelSpell);
        if (channelSpell != 0 && system.Store.Get(channelSpell) is { } spell && ((uint)spell.ChannelInterruptFlags & mask) != 0)
        {
            system.CancelChannel(unit);
        }
    }
}
