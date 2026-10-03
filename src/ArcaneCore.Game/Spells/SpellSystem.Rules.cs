using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Spells.Rules.Application;
using ArcaneCore.Game.Spells.Rules.Immunity;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// Hooks the spell combat rules (<c>Spells/Rules</c>) share with the rest of the spell system. A separate
/// partial file so the rule slices add their seams without editing the shared system files.
/// </summary>
public sealed partial class SpellSystem
{
    /// <summary>
    /// Close the open loot window of a player (vmangos WorldSession::DoLootRelease); a stun or fear
    /// calls it. Null until the loot area installs one, which means no window can be open.
    /// </summary>
    public Action<Player>? ReleaseLoot { get; set; }

    internal UnitSpellState StateOf(Unit unit) => GetOrCreateState(unit);

    /// <summary>
    /// Talent spell modifiers (vmangos Player::ApplySpellMod: crit chance and damage, resist-miss chance, dispel
    /// resistance, ...). The identity until the talents area installs the real storage.
    /// </summary>
    public ISpellModifiers SpellModifiers { get; set; } = ISpellModifiers.None;

    /// <summary>
    /// Whether immunities are enforced (retail: true; <c>SpellRules:ImmunityEnforcement</c> false disables every check
    /// for development hosts).
    /// </summary>
    public bool ImmunityEnforcement { get; set; } = true;

    /// <summary>The static immunities of creatures (creature data area); null means none.</summary>
    public ICreatureImmunityProvider? CreatureImmunities { get; set; }

    /// <summary>
    /// The application rules run for every spell landing on a target, in order (mechanic resistance, diminishing
    /// returns, ...): they narrow the effect mask and may veto the built aura holder. Empty by default.
    /// </summary>
    public List<ISpellApplicationRule> ApplicationRules { get; } = [];

    /// <summary>An aura holder was put on its target (after its handlers ran); a refreshed stack does not raise it.</summary>
    public event Action<SpellAuraHolder>? HolderAdded;

    /// <summary>An aura holder was taken off its target (after its handlers ran).</summary>
    public event Action<SpellAuraHolder>? HolderRemoved;

    /// <summary>A unit died and its auras were removed (see <see cref="OnUnitDied"/>).</summary>
    public event Action<Unit>? UnitDied;

    internal void RaiseHolderAdded(SpellAuraHolder holder) => HolderAdded?.Invoke(holder);

    internal void RaiseHolderRemoved(SpellAuraHolder holder) => HolderRemoved?.Invoke(holder);

    internal void RaiseUnitDied(Unit unit) => UnitDied?.Invoke(unit);

    /// <summary>
    /// Whether a live root or stun aura roots <paramref name="unit"/> (vmangos Unit::SetRooted via HandleAuraModRoot /
    /// HandleAuraModStun). The creature movement code reads this; players are rooted through <see cref="Player.SetRooted"/>.
    /// </summary>
    public bool IsRooted(Unit unit) => HasLiveAura(unit, AuraType.ModRoot, AuraType.ModStun);

    /// <summary>True while a live (not removed) aura of any of <paramref name="types"/> is on <paramref name="unit"/> (vmangos Unit::HasAuraType).</summary>
    internal bool HasLiveAura(Unit unit, params AuraType[] types)
    {
        foreach (SpellAuraHolder holder in GetAuras(unit))
        {
            if (holder.IsRemoved)
            {
                continue;
            }

            foreach (SpellAura? aura in holder.Auras)
            {
                if (aura is not null && Array.IndexOf(types, aura.Type) >= 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// vmangos Unit::InterruptNonMeleeSpells(false) restricted by <paramref name="filter"/>: cancel the cast or
    /// channel in progress when it matches (all when null). True when one was interrupted.
    /// </summary>
    internal bool InterruptCurrentCast(Unit unit, Func<SpellInfo, bool>? filter = null)
    {
        if (GetState(unit.Guid)?.CurrentCast is { } cast && cast.State != SpellCastState.Finished && (filter is null || filter(cast.Spell)))
        {
            Cancel(cast);
            return true;
        }

        return false;
    }
}
