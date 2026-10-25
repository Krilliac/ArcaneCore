using ArcaneCore.Kernel.Characters;

namespace ArcaneCore.World.Playerbots.Groups;

/// <summary>What a candidate for a waiting group is, as the recruiting rules see it (<see cref="PlayerbotRecruitment"/>).</summary>
/// <param name="Sociability">The candidate's sociability trait (0..1).</param>
/// <param name="Affinity">Its effective affinity toward the group's caller (<c>PlayerbotSocialMemory.EffectiveAffinity</c>; 0 for a stranger).</param>
/// <param name="WorstAffinity">Its lowest effective affinity toward any member of the group (0 when it knows none).</param>
/// <param name="Rival">It treats a member of the group as a rival (<c>PlayerbotSocialMemory.IsRival</c>).</param>
/// <param name="FillsRole">It can fill a tank or healer the group lacks.</param>
/// <param name="Guildmate">It shares a guild with a member.</param>
/// <param name="AnsweredCall">It answered the group's LFG or LFM line.</param>
internal readonly record struct PlayerbotRecruitFacts(float Sociability, int Affinity, int WorstAffinity, bool Rival, bool FillsRole, bool Guildmate, bool AnsweredCall);

/// <summary>
/// The pure rules of recruiting (docs/areas/playbots-groups.md, Cooperation): who may be asked, how likely a candidate is to come, and which
/// of the bots a waiting group could still use it takes first. Every roll is a hash of the candidate and the group, never a
/// <see cref="Random"/>, so a candidate gives the same answer to the same group. Affinities arrive already read through
/// <c>EffectiveAffinity</c> and <c>IsRival</c> (War Mode's layer while it is on); nothing here reads a raw relation.
/// </summary>
internal static class PlayerbotRecruitment
{
    /// <summary>The ceiling of an acceptance chance: nobody is certain to come.</summary>
    internal const float MaxChance = 0.95f;

    /// <summary>The chance added for a candidate that fills a missing tank or healer.</summary>
    internal const float RoleBonus = 0.2f;

    /// <summary>How much a point of sociability (0..1) adds to the chance.</summary>
    internal const float SociabilityWeight = 0.4f;

    /// <summary>How much a point of affinity toward the caller adds to the chance.</summary>
    internal const float AffinityWeight = 0.003f;

    /// <summary>A quest this close to done (this many objectives left, or fewer) is not interrupted.</summary>
    internal const int NearlyDoneObjectives = 2;

    /// <summary>Whether the candidate refuses outright: it holds a rival among the members, or likes one at or below <see cref="PlayerbotCooperationOptions.RefuseAffinity"/>.</summary>
    internal static bool Refuses(PlayerbotCooperationOptions options, in PlayerbotRecruitFacts facts)
    {
        ArgumentNullException.ThrowIfNull(options);
        return facts.Rival || facts.WorstAffinity <= options.RefuseAffinity || facts.Affinity <= options.RefuseAffinity;
    }

    /// <summary>
    /// The chance the candidate comes: <c>clamp(RecruitBaseChance + 0.4 x Sociability + 0.003 x affinity + 0.2 if it fills a missing role
    /// + GuildBonus for a guildmate + LfgAnswerBonus if it answered the call, 0, 0.95)</c>; 0 for a candidate that <see cref="Refuses"/>.
    /// </summary>
    internal static float Chance(PlayerbotCooperationOptions options, in PlayerbotRecruitFacts facts)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (Refuses(options, facts)) return 0f;
        double chance = options.RecruitBaseChance + (SociabilityWeight * facts.Sociability) + (AffinityWeight * facts.Affinity)
            + (facts.FillsRole ? RoleBonus : 0f) + (facts.Guildmate ? options.GuildBonus : 0f) + (facts.AnsweredCall ? options.LfgAnswerBonus : 0f);
        return (float)Math.Clamp(chance, 0d, MaxChance);
    }

    /// <summary>The largest group a creature that is no elite may ask for: a party.</summary>
    internal const int PartyLimit = PlayerbotGroupContent.PartySize;

    /// <summary>
    /// The largest group a risk signal may ask for: a party (five), or with <see cref="PlayerbotCooperationOptions.RaidEscalation"/> and
    /// <see cref="PlayerbotGroupOptions.RaidsEnabled"/> up to <see cref="PlayerbotCooperationOptions.MaxRaidSize"/> (a raid the coordinator
    /// converts the group to once it forms).
    /// </summary>
    internal static int SignalLimit(PlayerbotGroupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.RaidsEnabled && options.Cooperation.RaidEscalation
            ? Math.Clamp(options.Cooperation.MaxRaidSize, PlayerbotGroupContent.PartySize, ArcaneCore.Game.Groups.Group.MaxRaidSize)
            : PlayerbotGroupContent.PartySize;
    }

    /// <summary>
    /// The seed a candidate rolls on for a waiting group in one refusal window (<paramref name="window"/>: see <see cref="RefusalWindow"/>):
    /// window 0 rolls on the group's id, every other window on a different seed. The pair's windows are phase-shifted ("RefusalPhase"), so
    /// the window number is not the same for every pair at one instant.
    /// </summary>
    internal static int RollGroup(int groupId, uint window) => unchecked(groupId ^ (int)(window * 0x9E3779B1u));

    /// <summary>
    /// The offset of one (candidate, group) pair's refusal windows, in [0, <paramref name="periodMs"/>): a hash of the pair, so the windows of
    /// different pairs do not all start at the same instant.
    /// </summary>
    internal static uint RefusalPhase(uint periodMs, ulong candidateGuid, int groupId)
        => (uint)(PlayerbotContention.Mix(candidateGuid, (ulong)(uint)groupId) % periodMs);

    /// <summary>The refusal window of the pair at <paramref name="nowMs"/>: time plus the pair's phase, divided by the period (<see cref="RollGroup"/>'s <c>window</c>).</summary>
    internal static uint RefusalWindow(uint nowMs, uint periodMs, ulong candidateGuid, int groupId)
        => unchecked(nowMs + RefusalPhase(periodMs, candidateGuid, groupId)) / periodMs;

    /// <summary>The candidate's roll for the group: a fixed number in [0, 1) for the pair, whoever asks and whenever.</summary>
    internal static double Roll(ulong candidateGuid, int groupId)
        => (PlayerbotContention.Mix(PlayerbotContention.Mix(candidateGuid, 0x5EC8017AUL), (ulong)(uint)groupId) >> 11) * (1.0 / 9007199254740992.0);

    /// <summary>Whether the candidate comes: its <see cref="Roll"/> is below its <see cref="Chance"/>.</summary>
    internal static bool Accepts(PlayerbotCooperationOptions options, ulong candidateGuid, int groupId, in PlayerbotRecruitFacts facts)
        => Roll(candidateGuid, groupId) < Chance(options, facts);

    /// <summary>
    /// Whether a bot on <paramref name="goal"/> may be pulled away from it: idle or on a goal of lower priority than a group (exploring, grinding,
    /// the town errands, training). Never in a fight, recovering from a death, in a battleground, following a master, or already in a group;
    /// a quest only while it is more than <see cref="NearlyDoneObjectives"/> objectives from done (<paramref name="remainingObjectives"/>).
    /// </summary>
    internal static bool GoalYields(PlayerbotGoalKind goal, int remainingObjectives) => goal switch
    {
        PlayerbotGoalKind.Explore or PlayerbotGoalKind.Grind or PlayerbotGoalKind.Vendor or PlayerbotGoalKind.Train
            or PlayerbotGoalKind.Home or PlayerbotGoalKind.Mount => true,
        PlayerbotGoalKind.Quest => remainingObjectives > NearlyDoneObjectives,
        _ => false,
    };

    /// <summary>
    /// The tanks and healers a group still lacks: its needs for <paramref name="size"/> less what <paramref name="members"/> can cover
    /// (tanks assigned first, from those that can tank, then healers from the rest).
    /// </summary>
    internal static (int Tanks, int Healers) MissingRoles(IReadOnlyList<PlayerbotGroupCandidate> members, int size, PlayerbotGroupOptions options)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(options);
        (int tanks, int healers) = PlayerbotGroupContent.RoleNeeds(size, options);
        if (tanks + healers == 0) return (0, 0);
        var left = new List<PlayerbotGroupCandidate>(members);
        int tanked = 0;
        foreach (PlayerbotGroupCandidate tank in left.Where(c => PlayerbotGroupContent.CanTank(c.Class))
                     .OrderByDescending(c => PlayerbotGroupContent.Preferred(c.Class, c.TalentRole) == PlayerbotGroupRole.Tank)
                     .ThenBy(c => c.BotId).Take(tanks).ToArray())
        {
            tanked++;
            left.Remove(tank);
        }

        int healed = left.Count(c => PlayerbotGroupContent.CanHeal(c.Class));
        return (tanks - tanked, Math.Max(0, healers - healed));
    }

    /// <summary>One accepted candidate the recruiting step weighed.</summary>
    internal readonly record struct Ranked(PlayerbotGroupCandidate Bot, int Preference, float DistanceSq, bool CanTank, bool CanHeal);

    /// <summary>
    /// The order a group takes accepted candidates in: those that fill a missing tank, then a missing healer (up to the missing counts), then
    /// the rest; inside each part the higher preference (affinity and trust toward the members, plus the guild bonus) first, then the nearer, then
    /// the lower bot id. The group takes as many as it has <paramref name="slots"/> free, and never fewer than the role fillers.
    /// </summary>
    internal static List<PlayerbotGroupCandidate> Order(IReadOnlyList<Ranked> accepted, (int Tanks, int Healers) missing, int slots)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        List<Ranked> byPreference = [.. accepted.OrderByDescending(r => r.Preference).ThenBy(r => r.DistanceSq).ThenBy(r => r.Bot.BotId)];
        var chosen = new List<PlayerbotGroupCandidate>();
        var used = new HashSet<Guid>();
        foreach (Ranked ranked in byPreference.Where(r => r.CanTank).Take(Math.Max(0, missing.Tanks)))
        {
            chosen.Add(ranked.Bot);
            used.Add(ranked.Bot.BotId);
        }

        foreach (Ranked ranked in byPreference.Where(r => r.CanHeal && !used.Contains(r.Bot.BotId)).Take(Math.Max(0, missing.Healers)))
        {
            chosen.Add(ranked.Bot);
            used.Add(ranked.Bot.BotId);
        }

        int target = Math.Max(chosen.Count, slots);
        foreach (Ranked ranked in byPreference)
        {
            if (chosen.Count >= target) break;
            if (used.Add(ranked.Bot.BotId)) chosen.Add(ranked.Bot);
        }

        return chosen;
    }

    /// <summary>
    /// The preference of a candidate: its affinity toward the members summed, plus a tenth of its trust offset (permille from neutral 500) summed,
    /// plus <see cref="PlayerbotCooperationOptions.GuildBonus"/> as points for a guildmate.
    /// </summary>
    internal static int Preference(int affinitySum, int trustOffsetSum, bool guildmate, PlayerbotCooperationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return affinitySum + (trustOffsetSum / 10) + (guildmate ? (int)MathF.Round(options.GuildBonus * 100f) : 0);
    }
}
