using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Spells.Mods;

/// <summary>
/// The modifiers vmangos builds in code instead of reading from the spell data (SpellAuras.cpp:2117-2155, Mage family, builds
/// after 1.10.2): Frost Warding (11189, 28332) is a flat RESIST_MISS_CHANCE on class mask 0x100 and Improved Fire Ward (11094,
/// 13043) the same on mask 0x8, both with the amount of the spell's DUMMY aura. They hang on the holder events because the
/// DUMMY handler is a built-in that modules may not replace.
/// <para>
/// The 5875 Spell.dbc rows of Improved Fire Ward (11094, 13043) carry a DUMMY <em>effect</em> (no aura) with the amount as base points, while the
/// vmangos spell_template rows (builds 5464 and later) turn it into APPLY_AURA / DUMMY, which is what SpellAuras.cpp:2142-2152 reads. Cast as it
/// stands, the passive therefore never produces a holder: <see cref="ImprovedFireWardScript"/> takes the DUMMY effect of the learned passive and
/// <see cref="ApplyPassive"/> keeps the same mod alive for as long as the spell is in the book (<see cref="PassiveWardObserver"/> takes it away
/// when the spell is forgotten or replaced by a higher rank). Frost Warding's rows are APPLY_AURA / DUMMY in the DBC and use the holder path.
/// </para>
/// </summary>
internal sealed class HardcodedMods(SpellSystem system, SpellModEngine engine)
{
    private readonly ConditionalWeakTable<SpellAuraHolder, SpellMod> _live = new();
    private readonly ConditionalWeakTable<Player, Dictionary<uint, SpellMod>> _passive = new();

    public void Attach()
    {
        system.HolderAdded += OnAdded;
        system.HolderRemoved += OnRemoved;
        system.AddLearnObserver(new PassiveWardObserver(this));
        engine.Wards = this;
    }

    /// <summary>
    /// The DUMMY effect of a learned ward passive (see the class remarks): the player holds the flat RESIST_MISS_CHANCE mod of
    /// <paramref name="spell"/> with <paramref name="amount"/> until the spell leaves the book. A second cast of the same spell (a re-learned
    /// passive) replaces the mod, so it never stacks.
    /// </summary>
    internal void ApplyPassive(Player player, SpellInfo spell, int effectIndex, int amount)
    {
        if (!engine.Options.Enabled || !engine.Options.HardcodedWardMods || WardMask(spell.Id) is not { } mask)
        {
            return;
        }

        RemovePassive(player, spell.Id);
        var mod = new SpellMod(SpellModOp.ResistMissChance, SpellModType.Flat, amount, mask, spell.SpellFamilyName, spell.Id, effectIndex);
        _passive.GetOrCreateValue(player)[spell.Id] = mod;
        engine.Add(player, mod);
    }

    internal void RemovePassive(Player player, uint spellId)
    {
        if (_passive.TryGetValue(player, out Dictionary<uint, SpellMod>? held) && held.Remove(spellId, out SpellMod? mod))
        {
            engine.Remove(player, mod);
        }
    }

    private sealed class PassiveWardObserver(HardcodedMods owner) : ISpellLearnObserver
    {
        public void AfterRemove(Player player, uint spellId) => owner.RemovePassive(player, spellId);
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

/// <summary>
/// Improved Fire Ward 11094 / 13043, DUMMY effect form (see <see cref="HardcodedMods"/>): vmangos SpellAuras.cpp:2142-2152 builds the
/// RESIST_MISS_CHANCE flat mod on class mask 0x8 with the DUMMY amount. The effect runs when the passive is cast on learning and at login.
/// </summary>
[SpellScript(11094, 13043)]
public sealed class ImprovedFireWardScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Effect.Effect == SpellEffectName.Dummy && context.Target is Player player
            && context.System.Mods is SpellModEngine { Wards: { } wards })
        {
            wards.ApplyPassive(player, context.Spell, context.EffectIndex, context.Value);
        }
    }
}
