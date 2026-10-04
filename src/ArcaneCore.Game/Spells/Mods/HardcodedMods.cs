using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells.Mods;

/// <summary>
/// The modifiers vmangos builds in code instead of reading from the spell data (SpellAuras.cpp:2117-2155, Mage family, builds
/// after 1.10.2): Frost Warding (11189, 28332) is a flat RESIST_MISS_CHANCE on class mask 0x100 and Improved Fire Ward (11094,
/// 13043) the same on mask 0x8, both with the amount of the spell's DUMMY aura. They hang on the holder events because the
/// DUMMY handler is a built-in that modules may not replace.
/// </summary>
internal sealed class HardcodedMods(SpellSystem system, SpellModEngine engine)
{
    private readonly ConditionalWeakTable<SpellAuraHolder, SpellMod> _live = new();

    public void Attach()
    {
        system.HolderAdded += OnAdded;
        system.HolderRemoved += OnRemoved;
    }

    private static ulong? WardMask(uint spellId) => spellId switch
    {
        11189 or 28332 => 0x100,
        11094 or 13043 => 0x8,
        _ => null,
    };

    private void OnAdded(SpellAuraHolder holder)
    {
        if (!engine.Options.Enabled || !engine.Options.HardcodedWardMods || holder.Target is not Player player
            || WardMask(holder.Spell.Id) is not { } mask || _live.TryGetValue(holder, out _))
        {
            return;
        }

        SpellAura? dummy = holder.Auras.FirstOrDefault(a => a?.Type == AuraType.Dummy);
        if (dummy is null)
        {
            return;
        }

        var mod = new SpellMod(SpellModOp.ResistMissChance, SpellModType.Flat, dummy.Amount, mask, holder.Spell.SpellFamilyName, holder.Spell.Id, dummy.EffectIndex);
        _live.Add(holder, mod);
        engine.Add(player, mod);
    }

    private void OnRemoved(SpellAuraHolder holder)
    {
        if (holder.Target is Player player && _live.TryGetValue(holder, out SpellMod? mod))
        {
            _live.Remove(holder);
            engine.Remove(player, mod);
        }
    }
}
