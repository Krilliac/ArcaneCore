using ArcaneCore.Game.Entities;

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

    // The lists mutate ThreatenedBy while they are walked.
    private static Unit[] Snapshot(Unit target) => [.. target.Combat.ThreatenedBy];
}
