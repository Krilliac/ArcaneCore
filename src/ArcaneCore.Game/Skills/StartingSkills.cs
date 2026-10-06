using ArcaneCore.Kernel.Skills;

namespace ArcaneCore.Game.Skills;

public sealed partial class PlayerSkills
{
    /// <summary>
    /// Applies SQL-imported playercreateinfo_skills after persisted rows and spellbook replay. Existing live
    /// skills and forgotten skills win, so login never resets progress or resurrects an explicitly unlearned skill.
    /// </summary>
    public int ApplyStartingSkills(IEnumerable<StartingSkill> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        int added = 0;
        foreach (StartingSkill row in rows)
        {
            if (row.Skill == 0 || Has(row.Skill) || _forgotten.ContainsKey((ushort)row.Skill))
            {
                continue;
            }

            SkillRaceClassInfoRecord? raceClass = _catalog.RaceClassInfo(row.Skill, (byte)_player.Race, (byte)_player.Class);
            if (raceClass is null || row.Step > SkillTierRecord.StepCount)
            {
                continue;
            }

            SkillRangeType? range = _catalog.RangeType(row.Skill, raceClass);
            ushort value;
            ushort max;
            switch (range)
            {
                case SkillRangeType.Language:
                    value = max = 300;
                    break;
                case SkillRangeType.Mono:
                    value = max = 1;
                    break;
                case SkillRangeType.Level:
                    value = 1;
                    max = SkillRules.MaxForLevel(_player.Level);
                    break;
                case SkillRangeType.Rank when row.Step > 0 && _catalog.Tier(raceClass.TierId) is { } tier:
                    value = 1;
                    max = (ushort)Math.Min(ushort.MaxValue, tier.MaxValue[row.Step - 1]);
                    break;
                default:
                    continue;
            }

            if ((raceClass.Flags & SkillRaceClassFlags.AlwaysMaxValue) != 0
                || range == SkillRangeType.Level && Options.AlwaysMaxSkillForLevel)
            {
                value = max;
            }

            if (max == 0 || !Set(row.Skill, value, max, row.Step))
            {
                continue;
            }

            added++;
        }

        return added;
    }
}
