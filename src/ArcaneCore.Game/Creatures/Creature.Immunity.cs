using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// The creature's active <c>creature_immunities</c> set (mangos-classic <c>Creature::m_immunitySet</c> and <c>UpdateImmunitiesSet</c>,
/// Entities/Creature.cpp:2462-2481). The immunities themselves are served to the spell rules by
/// <see cref="Spells.Rules.Immunity.CreatureDataImmunities"/>, which reads <see cref="ImmunitySetId"/> and the creature's template.
/// Thread affinity: world thread.
/// </summary>
public sealed partial class Creature
{
    /// <summary>The <c>creature_immunities</c> set in force: 0 from creation and from every respawn until an EventAI SET_IMMUNITY_SET switches it.</summary>
    public uint ImmunitySetId { get; private set; }

    /// <summary>
    /// Switch to <c>creature_immunities</c> set <paramref name="setId"/> of the creature's current entry. As mangos-classic does, a set the
    /// entry has no rows for is refused and nothing changes (so a creature whose set 0 is empty cannot switch back to it). Returns whether the
    /// set was taken; switching to the set already in force is taken and changes nothing.
    /// </summary>
    internal bool UpdateImmunitySet(uint setId)
    {
        if (Content.FindImmunitySet(Template.Entry, setId) is null)
        {
            return false;
        }

        ImmunitySetId = setId;
        return true;
    }

    /// <summary>
    /// A respawn and an entry change go back to set 0 (mangos-classic Creature::UpdateEntry ends with <c>UpdateImmunitiesSet(0)</c>, Creature.cpp:613; a respawn reaches it
    /// through ResetEntry, Creature.cpp:636-654). As in mangos-classic, an entry with no set 0 rows keeps the set it was on (<see cref="UpdateImmunitySet"/> refuses it).
    /// </summary>
    internal void ResetImmunitySet() => UpdateImmunitySet(0);
}
