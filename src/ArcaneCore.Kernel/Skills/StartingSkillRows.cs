namespace ArcaneCore.Kernel.Skills;

/// <summary>One <c>playercreateinfo_skills</c> source row (race/class masks, skill line and starting rank).</summary>
public sealed record StartingSkillRow(uint RaceMask, uint ClassMask, ushort Skill, ushort Step, string Note)
{
    /// <summary>Allows the existing column-name row mapper to construct a source row.</summary>
    public StartingSkillRow() : this(0, 0, 0, 0, string.Empty) { }
}

/// <summary>Starting skills selected for one playable race/class pair.</summary>
public sealed record StartingSkill(uint Skill, ushort Step, string Note);

/// <summary>World source for SQL-imported playercreateinfo_skills rows.</summary>
public interface IStartingSkillSource
{
    Task<IReadOnlyList<StartingSkill>> GetAsync(byte race, byte playerClass, CancellationToken cancellationToken = default);
}
