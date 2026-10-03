using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells.Interrupts;

/// <summary>Helpers built only from the public <see cref="SpellSystem"/> aura API.</summary>
public static class SpellSystemInterruptExtensions
{
    /// <summary>
    /// vmangos Unit::RemoveAurasWithInterruptFlags(flag, except): remove every spell on the unit whose
    /// AuraInterruptFlags intersect <paramref name="mask"/>, except spell <paramref name="exceptSpellId"/>.
    /// </summary>
    public static void RemoveAurasWithInterruptFlags(this SpellSystem system, Unit unit, uint mask, uint exceptSpellId = 0)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(unit);
        foreach (uint spellId in system.GetAuras(unit)
            .Where(h => h.Spell.Id != exceptSpellId && ((uint)h.Spell.AuraInterruptFlags & mask) != 0)
            .Select(h => h.Spell.Id)
            .Distinct()
            .ToArray())
        {
            system.RemoveAuras(unit, spellId);
        }
    }

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
