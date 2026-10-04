using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Death.Ghost;

/// <summary>
/// The ghost aura of a released spirit (vmangos Player::ApplyGhostForm and RemoveGhostForm, Player.cpp:4561-4577): spell 8326,
/// and for a player who knows the wisp passive 20585 (the night elves) also 20584, both cast triggered on the player. When the
/// spell is not in the spell store, or <see cref="DeathOptions.GhostFormAura"/> is off, nothing is cast: combat then sets the
/// ghost flag itself, as it did before the aura existed (a missing 8326 is logged once).
/// <para>Thread affinity: world thread.</para>
/// </summary>
/// <param name="world">The world, for the death options.</param>
/// <param name="spells">The spell system; null until the spell feature is there.</param>
/// <param name="warn">Receives the one-time warning about a missing ghost spell.</param>
public sealed class GhostForm(WorldRuntime world, Func<SpellSystem?> spells, Action<string>? warn = null) : IGhostForm
{
    /// <summary>SPELL_WISP_SPIRIT (vmangos Player.cpp): the night elf passive that makes the ghost a wisp.</summary>
    public const uint WispSpiritSpellId = 20585;

    private bool _warned;

    /// <inheritdoc />
    public void Apply(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (Usable() is not { } system)
        {
            return;
        }

        if (HasWisp(system, player))
        {
            system.CastSpell(player, CombatConstants.WispGhostSpellId, SpellCastTargets.ForSelf(), triggered: true);
        }

        system.CastSpell(player, CombatConstants.GhostSpellId, SpellCastTargets.ForSelf(), triggered: true);
    }

    /// <inheritdoc />
    public void Remove(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (spells() is not { } system)
        {
            return;
        }

        // Removal is not gated on the option or on the store: a stored ghost aura must be able to go whatever the settings are now.
        if (HasWisp(system, player))
        {
            system.RemoveAuras(player, CombatConstants.WispGhostSpellId);
        }

        system.RemoveAuras(player, CombatConstants.GhostSpellId);
    }

    private SpellSystem? Usable()
    {
        if (!DeathHooks.For(world).Options.GhostFormAura || spells() is not { } system)
        {
            return null;
        }

        if (system.Store.Get(CombatConstants.GhostSpellId) is null)
        {
            if (!_warned)
            {
                _warned = true;
                warn?.Invoke($"the ghost spell {CombatConstants.GhostSpellId} is not in the spell store; released spirits get the ghost flag without the aura (no ghost speed)");
            }

            return null;
        }

        return system;
    }

    private static bool HasWisp(SpellSystem system, Player player)
        => system.Spellbook?.HasSpell(player, WispSpiritSpellId) == true && system.Store.Get(CombatConstants.WispGhostSpellId) is not null;
}
