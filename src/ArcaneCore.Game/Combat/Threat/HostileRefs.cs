using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;

namespace ArcaneCore.Game.Combat.Threat;

/// <summary>
/// vmangos HostileRefManager: the operations that act on every threat list holding one target
/// (Threat/HostileRefManager.cpp), over <see cref="UnitCombat.ThreatenedBy"/>.
/// </summary>
public static class HostileRefs
{
    /// <summary>
    /// vmangos HostileRefManager::addTempThreat (HostileRefManager.cpp:39-55), the Fade-style effect: with
    /// <paramref name="apply"/> fold <paramref name="threat"/> into every list entry of <paramref name="target"/> that carries no
    /// temporary modifier yet; otherwise take every modifier out again.
    /// </summary>
    public static void AddTempThreat(Unit target, float threat, bool apply)
    {
        ArgumentNullException.ThrowIfNull(target);
        foreach (Unit holder in Snapshot(target))
        {
            if (apply)
            {
                holder.Combat.Threat.ApplyTempThreatModifier(target, threat);
            }
            else
            {
                holder.Combat.Threat.ResetTempThreat(target);
            }
        }
    }

    /// <summary>vmangos HostileRefManager::addThreatPercent: scale the target's threat in every list that holds it.</summary>
    public static void AddThreatPercent(Unit target, int percent)
    {
        ArgumentNullException.ThrowIfNull(target);
        foreach (Unit holder in Snapshot(target))
        {
            holder.Combat.Threat.ScaleThreat(target, percent);
        }
    }

    /// <summary>vmangos HostileRefManager::deleteReferences (HostileRefManager.cpp:124-134): remove the target from every list.</summary>
    public static void DeleteReferences(Unit target)
    {
        ArgumentNullException.ThrowIfNull(target);
        foreach (Unit holder in Snapshot(target))
        {
            holder.Combat.Threat.Remove(target);
        }
    }

    /// <summary>
    /// vmangos HostileRefManager::setOnlineOfflineState (HostileRefManager.cpp:95-106): put the target online or offline in every list
    /// that holds it (a game master goes offline, Player::SetGameMaster, Player.cpp:2639 and :2665).
    /// </summary>
    public static void SetOnlineOfflineState(Unit target, bool online)
    {
        ArgumentNullException.ThrowIfNull(target);
        foreach (Unit holder in Snapshot(target))
        {
            holder.Combat.Threat.SetOnlineState(target, online);
        }
    }

    /// <summary>vmangos HostileRefManager::updateThreatTables (HostileRefManager.cpp:110-118): re-derive the target's online state in every list that holds it.</summary>
    public static void UpdateThreatTables(Unit target)
    {
        ArgumentNullException.ThrowIfNull(target);
        foreach (Unit holder in Snapshot(target))
        {
            holder.Combat.Threat.UpdateOnlineStatus(target);
        }
    }

    /// <summary>vmangos SPELL_ATTR_EX4_NO_HELPFUL_THREAT (SpellDefines.h:989): HostileRefManager::threatAssist ignores the spell.</summary>
    public const uint AttributeEx4NoHelpfulThreat = 0x00000008;

    /// <summary>
    /// vmangos HostileRefManager::threatAssist: <paramref name="threat"/> is divided by the number of units whose lists hold
    /// <paramref name="target"/> (all references, not only living holders) and added to every one of them for the
    /// <paramref name="assister"/> as assist threat (zero while that holder is confused or fleeing); a spell with
    /// NO_HELPFUL_THREAT adds none. The threat formula applies per list (<see cref="ThreatCalc.Calc"/> with the spell's school, or physical
    /// without a spell). Returns the lists that were given threat.
    /// </summary>
    public static Unit[] ThreatAssist(Unit target, Unit assister, float threat, SpellInfo? spell, IThreatModifierSource? modifiers, bool singleTarget = false)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(assister);
        if (spell is not null && (spell.AttributesEx4 & AttributeEx4NoHelpfulThreat) != 0)
        {
            return [];
        }

        Unit[] holders = [.. target.Combat.ThreatenedBy];
        if (holders.Length == 0)
        {
            return [];
        }

        float each = threat / (singleTarget ? 1 : holders.Length);
        uint schoolMask = spell is null ? ThreatCalc.PhysicalMask : spell.SchoolMask();
        bool noNewEntry = spell is not null && ((uint)spell.AttributesEx & MapCombat.AttributeExNoThreat) != 0;
        foreach (Unit holder in holders)
        {
            holder.Combat.Threat.AddThreat(assister, ThreatCalc.Calc(modifiers, assister, each, false, schoolMask, spell), new ThreatContext(IsAssist: true, NoNewEntry: noNewEntry));
        }

        return holders;
    }

    // The lists mutate ThreatenedBy while they are walked.
    private static Unit[] Snapshot(Unit target) => [.. target.Combat.ThreatenedBy];
}
