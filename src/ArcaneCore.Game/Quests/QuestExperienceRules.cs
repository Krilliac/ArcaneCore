using ArcaneCore.Kernel.Quests;

namespace ArcaneCore.Game.Quests;

/// <summary>Where a quest's full experience comes from (config <c>Quests:XpSource</c>).</summary>
public enum QuestXpSource
{
    /// <summary>
    /// Per dataset: the <c>RewXP</c> column when any loaded quest carries one (vmangos data), otherwise derived from
    /// <c>RewMoneyMaxLevel</c> (cmangos / classic-db data, which has no RewXP column).
    /// </summary>
    Auto,

    /// <summary>vmangos Quest::XPValue: the <c>RewXP</c> column only (QuestDef.cpp:180-202).</summary>
    RewXpColumn,

    /// <summary>cmangos Quest::XPValue: derived from <c>RewMoneyMaxLevel</c> (Quests/QuestDef.cpp:171-206).</summary>
    Derived,
}

/// <summary>
/// Quest experience for a player level, in float32 exactly as the references compute it (every operation is a
/// single-precision one; a double implementation differs in a few hundred value combinations, for example
/// RewMoneyMaxLevel 7 at quest level 1 for a level 8 player: float32 gives 7, double gives 8).
/// </summary>
public static class QuestExperienceRules
{
    /// <summary>The source <see cref="QuestXpSource.Auto"/> resolves to for a dataset.</summary>
    public static QuestXpSource Resolve(QuestXpSource configured, bool datasetHasRewXp)
        => configured != QuestXpSource.Auto ? configured : datasetHasRewXp ? QuestXpSource.RewXpColumn : QuestXpSource.Derived;

    /// <summary>
    /// The full (unreduced) experience of <paramref name="template"/> at <paramref name="questLevel"/>:
    /// the RewXP column, or cmangos's <c>RewMoneyMaxLevel / 0.6</c> for quest levels 1..60 and
    /// <c>/ 1.2, 2.4, 3.6, 4.8, 6.0</c> for levels 61..65 and above (QuestDef.cpp:175-189). Both references copy the
    /// signed quest level into a <c>uint32</c>, so a level of -1 reads as 4294967295 (the 65-and-above divisor);
    /// that is reproduced, not "fixed". A quest of level 0 derives nothing.
    /// </summary>
    public static float FullXp(QuestTemplate template, int questLevel, QuestXpSource resolved)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (resolved == QuestXpSource.RewXpColumn)
        {
            return template.RewXP;
        }

        if (template.RewMoneyMaxLevel == 0)
        {
            return 0;
        }

        uint q = unchecked((uint)questLevel);
        float money = template.RewMoneyMaxLevel;
        return q >= 65 ? money / 6.0f
            : q == 64 ? money / 4.8f
            : q == 63 ? money / 3.6f
            : q == 62 ? money / 2.4f
            : q == 61 ? money / 1.2f
            : q is > 0 and <= 60 ? money / 0.6f
            : 0;
    }

    /// <summary>
    /// Experience for a player of <paramref name="playerLevel"/>: the full value up to quest level + 5, then 0.8, 0.6,
    /// 0.4, 0.2 and 0.1 of it (each step rounded up in float32; QuestDef.cpp:191-205, vmangos QuestDef.cpp:188-199).
    /// The comparisons are the references' unsigned ones, wrap-around of a -1 quest level included.
    /// </summary>
    public static uint Xp(QuestTemplate template, int questLevel, uint playerLevel, QuestXpSource resolved)
    {
        float full = FullXp(template, questLevel, resolved);
        if (full <= 0)
        {
            return 0;
        }

        uint q = unchecked((uint)questLevel);
        if (playerLevel <= unchecked(q + 5))
        {
            return (uint)MathF.Ceiling(full);
        }

        float factor = playerLevel == unchecked(q + 6) ? 0.8f
            : playerLevel == unchecked(q + 7) ? 0.6f
            : playerLevel == unchecked(q + 8) ? 0.4f
            : playerLevel == unchecked(q + 9) ? 0.2f
            : 0.1f;
        return (uint)MathF.Ceiling(full * factor);
    }
}
