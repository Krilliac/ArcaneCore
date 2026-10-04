using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Stats;

/// <summary>
/// Keeps a player's combat stats right while the player is shapeshifted: the stat part of vmangos Player::InitDataForForm
/// after every form change (attack times, attack power, damage; Player.cpp:18271-18312, see
/// <see cref="PlayerStatSystem.OnFormChanged"/>) and the Predatory Strikes refresh (a Dummy aura of spell icon 1563 whose
/// amount is the percent of the level a cat or bear adds to its attack power; Aura::HandleAuraDummy,
/// SpellAuras.cpp:2164-2171, calls Player::UpdateAttackPowerAndDamage on apply and removal). Register it on the
/// <see cref="ShapeshiftService"/> (<see cref="ShapeshiftService.AddListener"/>) and attach it to the spell system.
/// </summary>
public sealed class FormStatListener(bool resetFistAttackTime = false) : IFormChangeListener
{
    /// <summary>Whether a hand without a weapon gets the 2.0 s base attack time when a form ends (opt-in deviation, default off) (see <see cref="PlayerStatSystem.OnFormChanged"/>).</summary>
    public bool ResetFistAttackTime { get; } = resetFistAttackTime;

    /// <summary>SpellIconID of the Predatory Strikes talent auras (SpellAuras.cpp:2166, StatSystem.cpp:259).</summary>
    public const uint PredatoryStrikesIconId = 1563;

    private SpellSystem? _spells;

    /// <inheritdoc/>
    public void OnFormChanged(Unit unit, byte oldForm, byte newForm)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (unit is Player { StatState.Maintainer: { } system } player)
        {
            system.OnFormChanged(player, newForm, ResetFistAttackTime);
        }
    }

    /// <summary>Follow the Predatory Strikes auras of <paramref name="spells"/>.</summary>
    public void Attach(SpellSystem spells)
    {
        ArgumentNullException.ThrowIfNull(spells);
        if (_spells is not null)
        {
            throw new InvalidOperationException("this listener is already attached to a spell system");
        }

        _spells = spells;
        spells.HolderAdded += OnHolderChanged;
        spells.HolderRemoved += OnHolderChanged;
    }

    private void OnHolderChanged(SpellAuraHolder holder)
    {
        if (holder.Spell.SpellIconId != PredatoryStrikesIconId || holder.Target is not Player player || _spells is null)
        {
            return;
        }

        int percent = 0;
        foreach (SpellAuraHolder other in _spells.GetAuras(player))
        {
            if (!other.IsRemoved && other.Spell.SpellIconId == PredatoryStrikesIconId
                && other.Auras.FirstOrDefault(a => a is { Type: AuraType.Dummy }) is { } dummy)
            {
                percent = dummy.Amount;
                break;
            }
        }

        if (player.StatState.PredatoryStrikesPercent != percent)
        {
            player.StatState.PredatoryStrikesPercent = percent;
            player.StatState.Maintainer?.UpdateAttackPowerAndDamage(player, ranged: false);
        }
    }
}
