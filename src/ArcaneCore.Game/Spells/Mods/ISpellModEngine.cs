using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Rules;

namespace ArcaneCore.Game.Spells.Mods;

/// <summary>
/// The spell-modifier engine (vmangos Player::m_spellMods and ApplySpellMod). It is an <see cref="ISpellModifiers"/>, so every
/// existing consumer of that seam (crit chance and damage, resist-miss, dispel resistance, mana shield, pushback) reads live
/// talents; new consumers call <see cref="ISpellModifiers.Apply"/> or <see cref="Apply(Unit, SpellInfo, SpellModOp, int)"/>
/// without referencing the engine class. World thread only.
/// </summary>
public interface ISpellModEngine : ISpellModifiers
{
    SpellModOptions Options { get; }

    ISpellModOwnerResolver OwnerResolver { get; set; }

    /// <summary>The mask overlay, or null for the spell data's own 32-bit masks.</summary>
    IClassMaskSource? MaskSource { get; set; }

    /// <summary>
    /// The integer form of <see cref="ISpellModifiers.Apply"/>: the sum is truncated toward zero, as vmangos's
    /// <c>T(float(base) + diff)</c> does for an int32/uint32 base.
    /// </summary>
    int Apply(Unit caster, SpellInfo spell, SpellModOp op, int value);

    /// <summary>The class mask of a modifier aura effect: the overlay when it has a row, otherwise the spell data's own EffectItemType.</summary>
    ulong ClassMask(SpellInfo spell, int effectIndex);

    /// <summary>The mods <paramref name="owner"/> holds for <paramref name="op"/>, in the order they were added.</summary>
    IReadOnlyList<SpellMod> ModsOf(Unit owner, SpellModOp op);

    /// <summary>Add a mod to a player (vmangos Player::AddSpellMod(mod, true)).</summary>
    void Add(Player owner, SpellMod mod);

    /// <summary>Remove a mod from a player (vmangos Player::AddSpellMod(mod, false)); false if it was not held.</summary>
    bool Remove(Player owner, SpellMod mod);

    /// <summary>
    /// A new charge scope for a cast of <paramref name="spell"/> by <paramref name="caster"/> (vmangos Spell::m_appliedMods), or
    /// null when nobody holds modifiers for that caster.
    /// </summary>
    SpellModScope? CreateScope(Unit caster, SpellInfo spell);

    /// <summary>
    /// Open a consume window: while it is open, every <see cref="ISpellModifiers.Apply"/> for the scope's caster and spell spends
    /// the charges of the mods it uses, except for the operations vmangos reads without a spell (duration, cooldowns, threat,
    /// charges, activation time, chance of success, haste, attack power). Outside a window the engine only reads (the
    /// first CheckCast, power checks): a pinned mod (-1 charges) still applies there. Windows nest; dispose to close.
    /// </summary>
    SpellModWindow Begin(SpellModScope scope);

    /// <summary>
    /// The cast succeeded (or its channel started): forget the scope and remove the aura of every mod that is out of charges
    /// (vmangos Player::RemoveSpellMods, Player.cpp:17731-17763). Does nothing if the scope is already closed.
    /// </summary>
    void Seal(SpellModScope scope);

    /// <summary>
    /// The cast was cancelled or failed: give every spent charge back (-1 becomes 1, otherwise +1; vmangos
    /// Player::RestoreSpellMods, Player.cpp:17684-17722). Does nothing if the scope is already closed.
    /// </summary>
    void Restore(SpellModScope scope);

    /// <summary>Raised after a mod was added (true) or removed (false), before passives are reapplied.</summary>
    event Action<Player, SpellMod, bool>? Changed;
}
