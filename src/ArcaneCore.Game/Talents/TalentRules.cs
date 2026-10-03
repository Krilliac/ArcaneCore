using ArcaneCore.Kernel.Talents;

namespace ArcaneCore.Game.Talents;

/// <summary>Why a talent request was refused (retail sends no failure packet; the reason is for logs and tests).</summary>
public enum TalentLearnOutcome
{
    Ok,
    NoFreePoints,
    RankOutOfRange,
    UnknownTalent,
    WrongClass,
    AlreadyKnown,
    NotEnoughPoints,
    PrerequisiteTalent,
    PrerequisiteSpell,
    TierLocked,
    MissingRankSpell,
}

/// <summary>The verdict of <see cref="TalentRules.EvaluateLearn"/>: the rank spell to learn and the points it consumes.</summary>
public readonly record struct TalentLearnResult(TalentLearnOutcome Outcome, uint RankSpell, uint PointsNeeded);

/// <summary>
/// What to do about the free-point count (vmangos Player::UpdateFreeTalentPoints, Player.cpp:3217-3247):
/// <see cref="Reset"/> = run a no-cost talent reset; <see cref="FreePoints"/> = then/otherwise write this value
/// (null = leave the field alone).
/// </summary>
public readonly record struct TalentFreePointsDecision(bool Reset, uint? FreePoints);

/// <summary>
/// Pure talent rules over the catalog and a "does the character know this spell" predicate. No entity wiring,
/// so every branch is unit-testable. Reference: vmangos src/game/Objects/Player.cpp.
/// </summary>
public static class TalentRules
{
    /// <summary>vmangos CalculateTalentsPoints (Player.cpp:20500-20504): (level &lt; 10 ? 0 : level - 9) * Rate.Talent, truncated.</summary>
    public static uint PointsForLevel(int level, double rate)
    {
        uint forLevel = level < 10 ? 0u : (uint)(level - 9);
        double points = Math.Floor(forLevel * rate);
        return points <= 0 ? 0u : points >= uint.MaxValue ? uint.MaxValue : (uint)points;
    }

    /// <summary>vmangos GetTalentSpellCost (DBCStores.cpp:466-480): rank index + 1 for a talent rank spell, 0 for any other spell.</summary>
    public static uint CostOfSpell(TalentCatalog catalog, uint spellId)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.TryGetRankPosition(spellId, out TalentRankPosition position) ? (uint)position.RankIndex + 1 : 0;
    }

    /// <summary>
    /// The points the character has spent: the sum of <see cref="CostOfSpell"/> over every known talent rank
    /// spell (vmangos m_usedTalentCount, accumulated in Player::AddSpell :3697-3700).
    /// </summary>
    public static uint UsedPoints(TalentCatalog catalog, Func<uint, bool> hasSpell)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(hasSpell);
        uint used = 0;
        foreach (TalentRecord talent in catalog.Talents)
        {
            used += Spent(talent, hasSpell);
        }

        return used;
    }

    /// <summary>Points spent in one tab: j + 1 for every known rank spell (vmangos Player.cpp:20756-20776).</summary>
    public static uint SpentInTab(TalentCatalog catalog, uint tabId, Func<uint, bool> hasSpell)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(hasSpell);
        uint spent = 0;
        foreach (TalentRecord talent in catalog.TalentsOfTab(tabId))
        {
            spent += Spent(talent, hasSpell);
        }

        return spent;
    }

    /// <summary>The highest known rank, one-based (0 = none), as vmangos's <c>curtalent_maxrank</c> (Player.cpp:20708-20716).</summary>
    public static int HighestKnownRank(TalentRecord talent, Func<uint, bool> hasSpell)
    {
        ArgumentNullException.ThrowIfNull(talent);
        ArgumentNullException.ThrowIfNull(hasSpell);
        for (int rank = talent.RankSpells.Count - 1; rank >= 0; rank--)
        {
            uint spell = talent.RankSpells[rank];
            if (spell != 0 && hasSpell(spell))
            {
                return rank + 1;
            }
        }

        return 0;
    }

    /// <summary>
    /// vmangos Player::LearnTalent (Player.cpp:20684-20800) in the same step order. <paramref name="requestedRank"/> is the
    /// zero-based rank from CMSG_LEARN_TALENT; jumping ranks is allowed when the points cover the delta.
    /// </summary>
    public static TalentLearnResult EvaluateLearn(
        TalentCatalog catalog, Func<uint, bool> hasSpell, uint talentId, uint requestedRank, uint freePoints, uint classMask)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(hasSpell);
        if (freePoints == 0)
        {
            return Refuse(TalentLearnOutcome.NoFreePoints);
        }

        if (requestedRank >= TalentRecord.MaxRanks)
        {
            return Refuse(TalentLearnOutcome.RankOutOfRange);
        }

        TalentRecord? talent = catalog.ById(talentId);
        if (talent is null)
        {
            return Refuse(TalentLearnOutcome.UnknownTalent);
        }

        TalentTabRecord tab = catalog.Tab(talent.TabId)!;
        if ((classMask & tab.ClassMask) == 0)
        {
            return Refuse(TalentLearnOutcome.WrongClass);
        }

        uint known = (uint)HighestKnownRank(talent, hasSpell);
        if (known >= requestedRank + 1)
        {
            return Refuse(TalentLearnOutcome.AlreadyKnown);
        }

        uint needed = requestedRank - known + 1;
        if (freePoints < needed)
        {
            return Refuse(TalentLearnOutcome.NotEnoughPoints);
        }

        if (talent.DependsOn > 0)
        {
            // The catalog guarantees the prerequisite exists (vmangos silently skips an unresolved one, :20729).
            TalentRecord prerequisite = catalog.ById(talent.DependsOn)!;
            bool enough = false;
            for (int rank = (int)talent.DependsOnRank; rank < prerequisite.RankSpells.Count; rank++)
            {
                uint spell = prerequisite.RankSpells[rank];
                if (spell != 0 && hasSpell(spell))
                {
                    enough = true;
                }
            }

            if (!enough)
            {
                return Refuse(TalentLearnOutcome.PrerequisiteTalent);
            }
        }

        if (talent.DependsOnSpell != 0 && !hasSpell(talent.DependsOnSpell))
        {
            return Refuse(TalentLearnOutcome.PrerequisiteSpell);
        }

        if (talent.Row > 0 && SpentInTab(catalog, talent.TabId, hasSpell) < talent.Row * (uint)TalentRecord.MaxRanks)
        {
            return Refuse(TalentLearnOutcome.TierLocked);
        }

        uint rankSpell = talent.RankSpells[(int)requestedRank];
        if (rankSpell == 0)
        {
            return Refuse(TalentLearnOutcome.MissingRankSpell);
        }

        if (hasSpell(rankSpell))
        {
            return Refuse(TalentLearnOutcome.AlreadyKnown);
        }

        return new TalentLearnResult(TalentLearnOutcome.Ok, rankSpell, needed);
    }

    /// <summary>
    /// vmangos Player::UpdateFreeTalentPoints (Player.cpp:3217-3247). Below level 10 any spent point is refunded (reset
    /// when allowed, then zero free points); otherwise free = allowance - used, and an overspend resets for non-administrators
    /// (when <paramref name="resetIfNeed"/>) and merely zeroes the free points for administrators.
    /// </summary>
    public static TalentFreePointsDecision DecideFreePoints(int level, uint usedPoints, double rate, bool resetIfNeed, bool administrator)
    {
        if (level < 10)
        {
            return usedPoints > 0 ? new TalentFreePointsDecision(resetIfNeed, 0) : new TalentFreePointsDecision(false, null);
        }

        uint allowance = PointsForLevel(level, rate);
        if (usedPoints > allowance)
        {
            return resetIfNeed && !administrator
                ? new TalentFreePointsDecision(true, null)
                : new TalentFreePointsDecision(false, 0);
        }

        return new TalentFreePointsDecision(false, allowance - usedPoints);
    }

    private static uint Spent(TalentRecord talent, Func<uint, bool> hasSpell)
    {
        uint spent = 0;
        for (int rank = 0; rank < talent.RankSpells.Count; rank++)
        {
            uint spell = talent.RankSpells[rank];
            if (spell != 0 && hasSpell(spell))
            {
                spent += (uint)rank + 1;
            }
        }

        return spent;
    }

    private static TalentLearnResult Refuse(TalentLearnOutcome outcome) => new(outcome, 0, 0);
}
