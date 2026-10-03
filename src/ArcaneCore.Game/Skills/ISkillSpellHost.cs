namespace ArcaneCore.Game.Skills;

/// <summary>
/// What <see cref="PlayerSkills"/> needs from the spellbook owner (vmangos Player::AddSpell / LearnSpell /
/// RemoveSpell / HasSpell). The spellbook lives in the world layer; this seam keeps the skill rules testable
/// without a world and avoids a dependency from Game on it.
/// </summary>
public interface ISkillSpellHost
{
    /// <summary>vmangos Player::HasSpell: the spell is known and not disabled.</summary>
    bool HasSpell(uint spellId);

    /// <summary>
    /// Teach a spell a skill grants (vmangos UpdateSkillTrainedSpells, Player.cpp:5761-5764): while the player is
    /// not yet in the world it is added silently (<c>AddSpell(spell, true, true, true, false)</c>), otherwise it is
    /// learned with the client messages (<c>LearnSpell(spell, true)</c>). Knowing it already is not an error.
    /// </summary>
    void LearnSpell(uint spellId);

    /// <summary>vmangos Player::RemoveSpell for a spell that depends on a skill the player lost or has too little of.</summary>
    void RemoveSpell(uint spellId);

    /// <summary>
    /// A weapon skill was removed (vmangos Player::AutoUnequipWeaponsIfNeed, Player.cpp:5893, 19697): weapons the
    /// player can no longer use must leave the equipment slots.
    /// </summary>
    void WeaponSkillRemoved();
}
