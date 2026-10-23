using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Game.Social;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Social;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots.Groups;

/// <summary>
/// Mentoring (docs/areas/playbots-groups.md, Cooperation): a bot well above a friend or guildmate that struggles with a quest (the quest
/// was set aside, failed twice, or the friend called for help) may team up with it as a group of two on the friend's quest objective, and
/// the group ends with the objective or after <see cref="PlayerbotCooperationOptions.MentorMaxMinutes"/>. It uses the ordinary group
/// machinery (<see cref="Form"/>, the invitations, <see cref="ShareQuests"/>). World thread only.
/// </summary>
public sealed partial class PlayerbotGroupCoordinator
{
    /// <summary>The <see cref="PlayerbotGroupGoal.Source"/> of a mentor group.</summary>
    internal const string MentorSource = "mentor";

    /// <summary>How often the struggling bots are looked over for a mentor.</summary>
    internal const uint MentorScanMs = 30_000;

    /// <summary>How long a call for help counts as a struggle.</summary>
    internal const uint HelpCallLifeMs = 120_000;

    private readonly Dictionary<Guid, (PlayerbotGroupGoal Goal, uint AtMs)> _struggles = [];
    private readonly Dictionary<Guid, uint> _helpCalls = [];
    private readonly Dictionary<Guid, uint> _mentored = [];
    private uint _mentorScanMs;

    /// <summary>A bot called for help in a fight (the feature tells the coordinator; world thread). Counts as a struggle for <see cref="HelpCallLifeMs"/>.</summary>
    internal void NoteHelpCall(Guid botId)
    {
        if (_helpCalls.Count < MaxPledges) _helpCalls[botId] = Now;
    }

    /// <summary>The groups that are mentor groups (tests, status).</summary>
    internal int MentorGroupCount => _groups.Count(g => g.Goal.Source == MentorSource);

    private void ResetMentoring()
    {
        _struggles.Clear();
        _helpCalls.Clear();
        _mentored.Clear();
        _mentorScanMs = 0;
    }

    /// <summary>The goal a bot set aside or failed, kept for the mentor that may come to help (called by <see cref="SetAside"/>).</summary>
    private void NoteStruggle(Guid botId, PlayerbotGroupGoal goal)
    {
        if (goal.QuestId != 0 && goal.Source != MentorSource) _struggles[botId] = (goal, Now);
    }

    /// <summary>The mentors' scan: every <see cref="MentorScanMs"/>, each struggling bot gets at most one mentor.</summary>
    private void Mentor()
    {
        PlayerbotCooperationOptions coop = Coop;
        uint now = Now;
        if (!coop.Mentoring || Social is not { Enabled: true } || unchecked(now - _mentorScanMs) < MentorScanMs) return;
        _mentorScanMs = now;
        foreach (var (botId, struggle) in _struggles)
            if (unchecked(now - struggle.AtMs) >= SetAsideMs || !_bots.ContainsKey(botId)) _struggles.Remove(botId);
        foreach (var (botId, at) in _helpCalls)
            if (unchecked(now - at) >= HelpCallLifeMs) _helpCalls.Remove(botId);
        foreach (var (botId, until) in _mentored)
            if (unchecked((int)(until - now)) <= 0) _mentored.Remove(botId);

        foreach (Guid lowId in _struggles.Keys.Concat(_helpCalls.Keys).Distinct().OrderBy(id => id).ToArray())
        {
            if (!QuestGroupSlotFree()) return;
            if (TryMentorFor(lowId, now, coop)) _mentored[lowId] = unchecked(now + SetAsideMs);
        }
    }

    private FriendsService? FriendsOrNull()
    {
        try
        {
            return services.GetService<SocialFeature>()?.Context.Friends;
        }
        catch (InvalidOperationException ex)
        {
            FaultLedger.Shared.Record("playerbot.groups", ex);
            return null;
        }
    }

    private bool TryMentorFor(Guid lowId, uint now, PlayerbotCooperationOptions coop)
    {
        if (_mentored.ContainsKey(lowId) || _memberOf.ContainsKey(lowId) || !_bots.TryGetValue(lowId, out PlayerbotGroupBot low) || !low.Free || low.Scripted) return false;
        if (low.Session.Player is not { IsInWorld: true, IsAlive: true } lowPlayer) return false; // a bot in a losing fight is the one that needs help
        PlayerbotGroupGoal? goal = _struggles.TryGetValue(lowId, out var struggle) ? struggle.Goal : _waiting.TryGetValue(lowId, out Waiting? waiting) ? waiting.Goal : null;
        if (goal is null || goal.QuestId == 0 || QuestState(lowPlayer) is not { } state || state.Quests.Get(goal.QuestId) is not { Status: QuestStatus.Incomplete }) return false;
        if (GroupManagerOrNull() is not { } groups || groups.GetGroup(lowPlayer.Guid) is not null || groups.GetInvite(lowPlayer.Guid) is not null) return false;
        PlayerbotSocialMemory memory = Social!.Memory;
        GuildManager? guilds = GuildsOrNull();
        var lowAt = new Vector3(lowPlayer.X, lowPlayer.Y, lowPlayer.Z);
        float radiusSq = coop.RecruitRadiusYards * coop.RecruitRadiusYards;
        Player? best = null;
        Guid bestId = Guid.Empty;
        int bestAffinity = int.MinValue;
        for (int i = 0; i < _orderedBots.Count; i++)
        {
            PlayerbotGroupBot bot = _orderedBots[i];
            if (bot.BotId == lowId || !bot.Free || bot.Scripted || bot.Brain.Stay is not null || _memberOf.ContainsKey(bot.BotId) || _waiting.ContainsKey(bot.BotId)) continue;
            if (bot.Session.Player is not { IsInWorld: true, IsAlive: true } mentor || mentor.Combat.IsInCombat) continue;
            if (mentor.Team != lowPlayer.Team || mentor.MapId != lowPlayer.MapId || mentor.Level < lowPlayer.Level + coop.MentorLevelGap) continue;
            if (Vector3.DistanceSquared(new Vector3(mentor.X, mentor.Y, mentor.Z), lowAt) > radiusSq) continue;
            if (PlayerbotMageService.IsServing(mentor) || PlayerbotMageService.IsActive(mentor)) continue;
            if (!PlayerbotRecruitment.GoalYields(bot.Brain.Goal, bot.Brain.Goal == PlayerbotGoalKind.Quest ? RemainingObjectives(mentor, bot.Brain.QuestId) : 0)) continue;
            if (groups.GetGroup(mentor.Guid) is not null || groups.GetInvite(mentor.Guid) is not null) continue;
            if (!memory.TryGet(bot.BotId, lowPlayer.Guid.Value, out PlayerbotRelation relation) || memory.IsRival(relation)) continue;
            int affinity = memory.EffectiveAffinity(relation);
            if (affinity < coop.MentorAffinity) continue;
            bool kin = (guilds is not null && guilds.GetGuildOf(mentor) is { } guild && guilds.GetGuildOf(lowPlayer)?.Id == guild.Id)
                || (FriendsOrNull()?.Get(mentor).Has(lowPlayer.Guid.Low, SocialFlags.Friend) ?? false);
            if (!kin) continue;
            // A fixed roll per (mentor, struggling bot, struggle): the same state gives the same answer whenever it is asked.
            double roll = PlayerbotRecruitment.Roll(mentor.Guid.Value, (int)(lowPlayer.Guid.Low ^ (_struggles.TryGetValue(lowId, out var s) ? s.AtMs / MentorScanMs : 0u)));
            if (roll >= coop.MentorChance * PlayerbotTraits.For(mentor.Guid.Value).Helpfulness) continue;
            if (affinity > bestAffinity || (affinity == bestAffinity && bot.BotId.CompareTo(bestId) < 0))
            {
                best = mentor;
                bestId = bot.BotId;
                bestAffinity = affinity;
            }
        }

        if (best is null) return false;
        PlayerbotGroupCandidate leader = Candidate(bestId, best, now), friend = Candidate(lowId, lowPlayer, now);
        var plan = new PlayerbotGroupPlan(leader,
        [
            (leader, PlayerbotGroupContent.Preferred(leader.Class, leader.TalentRole), 0),
            (friend, PlayerbotGroupContent.Preferred(friend.Class, friend.TalentRole), 0),
        ]);
        Unhold(lowId, null);
        Form(goal with { Size = 2, Source = MentorSource }, plan);
        Note($"{best.Name} mentors {lowPlayer.Name} on quest {goal.QuestId} ({goal})");
        return true;
    }

    /// <summary>The mentor group's own end: after <see cref="PlayerbotCooperationOptions.MentorMaxMinutes"/>. The objective ends it as for any quest group.</summary>
    private bool MentorTimedOut(BotGroup group, uint now)
        => group.Goal.Source == MentorSource && unchecked(now - group.FormedMs) > (uint)Coop.MentorMaxMinutes * 60_000u;
}
