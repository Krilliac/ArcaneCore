using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// Told by <see cref="ShapeshiftService"/> after a unit's form changed and the form's data were initialised (vmangos
/// Player::InitDataForForm, Player.cpp:18271-18312, runs at the end of Aura::HandleAuraModShapeshift). The stat area uses
/// it to recompute attack power, damage and attack times; the spell lane never edits the stat files. Listeners run on the
/// world thread inside the aura handler and must not change the unit's form.
/// </summary>
public interface IFormChangeListener
{
    /// <summary>The unit's form byte went from <paramref name="oldForm"/> to <paramref name="newForm"/> (0 = none).</summary>
    void OnFormChanged(Unit unit, byte oldForm, byte newForm);
}
