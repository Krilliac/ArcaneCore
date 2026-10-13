namespace ArcaneCore.Game.Creatures;

/// <summary>
/// cmangos <c>TemporaryFactionFlags</c> (Entities/Creature.h:577-589): when a temporary faction is given back. The toggle flags (0x08-0x80)
/// are kept for scripts that pass them but change no unit flags yet.
/// </summary>
[Flags]
public enum TemporaryFactionFlags : uint
{
    /// <summary>The faction stays until a script changes it back (but a respawn here always re-reads the template's).</summary>
    None = 0x00,
    /// <summary>Restored at respawn (Creature::SetDeathState JUST_ALIVED, Creature.cpp:1929).</summary>
    RestoreRespawn = 0x01,
    /// <summary>Restored at CombatStop: death and evade (Unit::CombatStop, Unit.cpp:6376).</summary>
    RestoreCombatStop = 0x02,
    /// <summary>Restored when the home movement arrives (evade), unless CombatStop already did it.</summary>
    RestoreReachHome = 0x04,
    ToggleNonAttackable = 0x08,
    ToggleImmuneToPlayer = 0x10,
    ToggleImmuneToNpc = 0x20,
    TogglePacified = 0x40,
    ToggleNotSelectable = 0x80,
}

public sealed partial class Creature
{
    /// <summary>The restore flags of the current temporary faction (cmangos m_temporaryFactionFlags); None when none is set.</summary>
    public TemporaryFactionFlags TemporaryFactionFlags { get; private set; }

    /// <summary>cmangos Creature::SetFactionTemporary (Creature.cpp:2653-2670): the faction until a restore point the flags name.</summary>
    public void SetFactionTemporary(uint factionTemplate, TemporaryFactionFlags flags)
    {
        TemporaryFactionFlags = flags;
        FactionTemplate = factionTemplate;
    }

    /// <summary>
    /// cmangos Creature::ClearTemporaryFaction (Creature.cpp:2672-2700): the template's faction back, unless a charmer holds the creature
    /// (its faction is the charmer's until the charm ends, which restores the template's itself).
    /// </summary>
    public void ClearTemporaryFaction()
    {
        if (GetUInt64(UpdateFields.UnitFieldCharmedby) != 0)
        {
            return;
        }

        TemporaryFactionFlags = TemporaryFactionFlags.None;
        FactionTemplate = Template.Faction;
    }

    /// <summary>Give the template's faction back when the current temporary faction restores at <paramref name="point"/>.</summary>
    internal void RestoreTemporaryFactionAt(TemporaryFactionFlags point)
    {
        if ((TemporaryFactionFlags & point) != 0)
        {
            ClearTemporaryFaction();
        }
    }
}
