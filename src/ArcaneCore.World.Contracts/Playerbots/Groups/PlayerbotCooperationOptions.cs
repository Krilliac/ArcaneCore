namespace ArcaneCore.World.Playerbots;

/// <summary>
/// <c>World:Playerbots:Groups:Cooperation</c>: how bot groups grow beyond the bots that share one goal (docs/areas/playbots-groups.md,
/// Cooperation). A group still short of members or of a tank or healer after <see cref="RecruitWaitSeconds"/> recruits free bots nearby that
/// do not have the goal; bots answer other bots' LFG and LFM calls and guild calls; a bot far above a struggling friend mentors it; a world
/// elite too strong for a party becomes a raid goal. Every key is live: <c>.reload config</c> changes this object in place and the
/// coordinator reads it at every decision. Every chance is decided by a hash of the bot and the group, so the same bot gives the same
/// answer to the same group.
/// </summary>
public sealed class PlayerbotCooperationOptions
{
    /// <summary>Let a waiting group recruit nearby bots that lack its goal (on by default). Off: only bots that share the goal are grouped.</summary>
    public bool Recruit { get; set; } = true;

    /// <summary>Seconds a group of waiting bots stays short before it recruits (1..3600).</summary>
    public int RecruitWaitSeconds { get; set; } = 20;

    /// <summary>The farthest from the waiting bot, in yards, a recruit may stand (10..1500).</summary>
    public int RecruitRadiusYards { get; set; } = 300;

    /// <summary>
    /// The base chance that a candidate accepts (0..1). The chance is this plus 0.4 times the candidate's sociability, plus 0.003 per point of
    /// its affinity toward the group's caller, plus 0.2 when the candidate fills a missing tank or healer, plus <see cref="GuildBonus"/> for a
    /// guildmate and <see cref="LfgAnswerBonus"/> for one that answered the call; at most 0.95. A rival and an affinity at or below
    /// <see cref="RefuseAffinity"/> never accept.
    /// </summary>
    public float RecruitBaseChance { get; set; } = 0.35f;

    /// <summary>The chance and the preference a guildmate of a group's member gains (0..1).</summary>
    public float GuildBonus { get; set; } = 0.2f;

    /// <summary>The chance a bot that answered another bot's LFG or LFM line gains (0..1).</summary>
    public float LfgAnswerBonus { get; set; } = 0.15f;

    /// <summary>A candidate whose affinity toward the group's caller is at or below this never joins (-100..0).</summary>
    public int RefuseAffinity { get; set; } = -40;

    /// <summary>Let a bot that hears another bot's LFG or LFM line for a goal it could join offer itself to that group (on by default).</summary>
    public bool AnswerBotCalls { get; set; } = true;

    /// <summary>
    /// Let a guild's bot say "anyone for the dungeon?" in guild chat while a group of the guild's members is forming for it, and let the
    /// guildmates who hear it join at once (on by default). The line is said only when such a group exists.
    /// </summary>
    public bool GuildCalls { get; set; } = true;

    /// <summary>Let a bot far above a friend or guildmate that struggles with a quest group with it to clear the quest's objective (on by default).</summary>
    public bool Mentoring { get; set; } = true;

    /// <summary>The chance, times the mentor's helpfulness trait, that a bot able to mentor does (0..1).</summary>
    public float MentorChance { get; set; } = 0.3f;

    /// <summary>The longest, in minutes, a mentoring group lasts (1..240).</summary>
    public int MentorMaxMinutes { get; set; } = 20;

    /// <summary>The levels a mentor must stand above the friend (1..60).</summary>
    public int MentorLevelGap { get; set; } = 10;

    /// <summary>The affinity a mentor needs toward the friend (-100..100).</summary>
    public int MentorAffinity { get; set; } = 30;

    /// <summary>Let a risk-signal group that needs more than five become a raid goal when enough bots are nearby (on by default).</summary>
    public bool RaidEscalation { get; set; } = true;

    /// <summary>The most members a raid made from a risk signal recruits (6..40).</summary>
    public int MaxRaidSize { get; set; } = 40;

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Groups:Cooperation";
        Check(RecruitWaitSeconds, 1, 3600, nameof(RecruitWaitSeconds));
        Check(RecruitRadiusYards, 10, 1500, nameof(RecruitRadiusYards));
        CheckUnit(RecruitBaseChance, nameof(RecruitBaseChance));
        CheckUnit(GuildBonus, nameof(GuildBonus));
        CheckUnit(LfgAnswerBonus, nameof(LfgAnswerBonus));
        Check(RefuseAffinity, -100, 0, nameof(RefuseAffinity));
        CheckUnit(MentorChance, nameof(MentorChance));
        Check(MentorMaxMinutes, 1, 240, nameof(MentorMaxMinutes));
        Check(MentorLevelGap, 1, 60, nameof(MentorLevelGap));
        Check(MentorAffinity, -100, 100, nameof(MentorAffinity));
        Check(MaxRaidSize, 6, 40, nameof(MaxRaidSize));

        static void CheckUnit(float value, string name)
        {
            if (!float.IsFinite(value) || value < 0f || value > 1f) throw new InvalidOperationException($"{section}: {name} must be 0..1.");
        }

        static void Check(int value, int min, int max, string name)
        {
            if (value < min || value > max) throw new InvalidOperationException($"{section}: {name} must be {min}..{max}.");
        }
    }
}
