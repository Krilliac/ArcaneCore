using System.Globalization;
using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Social;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Playerbots.Groups;

/// <summary>What became of a bot's offer to join another bot's group (<see cref="PlayerbotGroupCoordinator.OfferLfg"/>).</summary>
internal enum PlayerbotLfgOfferOutcome : byte
{
    /// <summary>The caller waits for no group any more, or the hearer cannot offer (recruiting is off, it is the caller itself).</summary>
    NotWaiting,

    /// <summary>The hearer does not fit the group (team, map, level, goal, grouped, busy) or declined: the roll was lost or the rules refuse it.</summary>
    Declined,

    /// <summary>The hearer already offered for this goal.</summary>
    AlreadyPledged,

    /// <summary>The coordinator holds a plan with the hearer in the group: it joins at the next scan.</summary>
    Pledged,
}

/// <summary>The answer to <see cref="PlayerbotGroupCoordinator.OfferLfg"/>: the outcome and, when pledged, the role the hearer would play.</summary>
internal readonly record struct PlayerbotLfgOffer(PlayerbotLfgOfferOutcome Outcome, PlayerbotGroupRole Role = PlayerbotGroupRole.Damage);

/// <summary>A forming dungeon or raid group a guild may be asked to help fill (<see cref="PlayerbotGroupCoordinator.FormingCalls"/>).</summary>
internal readonly record struct PlayerbotFormingCall(Guid CallerBotId, PlayerbotGroupGoal Goal, int Have);

/// <summary>
/// Recruiting (docs/areas/playbots-groups.md, Cooperation): a waiting group short of members, or of the tank or healer it needs, asks
/// nearby free bots that lack the goal. After <see cref="PlayerbotCooperationOptions.RecruitWaitSeconds"/> every eligible bot within
/// <see cref="PlayerbotCooperationOptions.RecruitRadiusYards"/> is weighed by <see cref="PlayerbotRecruitment"/> (deterministic per bot and
/// group); a bot that answered the caller's LFG line (<see cref="OfferLfg"/>), or a guildmate who heard the caller's guild call
/// (<see cref="NoteGuildCall"/>), is weighed at once. The recruits join through the ordinary invitations of <see cref="Form"/>, and the group
/// shares its quest with them (<see cref="ShareQuests"/>); a recruit that cannot take the quest still helps.
/// </summary>
public sealed partial class PlayerbotGroupCoordinator
{
    /// <summary>The least time between two recruiting rounds for one goal and team (the pool is large; the answer changes slowly).</summary>
    internal const uint RecruitRetryMs = 6_000;

    /// <summary>How long an offer to a bot's LFG line, or a guild's call, stays good.</summary>
    internal const uint PledgeLifeMs = 120_000;

    private const int MaxPledges = 512;

    private readonly Dictionary<Guid, ((PlayerbotGroupGoalKind, uint) Key, uint Until)> _pledges = [];
    private readonly Dictionary<((PlayerbotGroupGoalKind, uint) Key, Team Team), uint> _recruitTried = [];
    private readonly Dictionary<(int Guild, (PlayerbotGroupGoalKind, uint) Key), uint> _guildCalls = [];
    private readonly List<(Guid BotId, PlayerbotGroupRole Role)> _recruitSays = [];

    /// <summary>
    /// The candidates that refused a waiting group (its <see cref="Waiting.GroupId"/>), with the time of the refusal (world ms): not asked again
    /// for <see cref="PlayerbotCooperationOptions.RefusalMemoryMinutes"/> from then, rolled afresh (in the pair's next refusal window) after it.
    /// </summary>
    private readonly Dictionary<(int GroupId, Guid BotId), uint> _recruitRefused = [];

    private uint RefusalPeriodMs => (uint)Coop.RefusalMemoryMinutes * 60_000u;

    /// <summary>When the candidate refused the waiting group (world ms), 0 when it holds no refusal (tests).</summary>
    internal uint RefusedAtMs(Guid botId, int groupId) => _recruitRefused.GetValueOrDefault((groupId, botId));

    private const int MaxRefusals = 16_384;
    private uint _capLoggedMs;
    private bool _capEverLogged;

    private PlayerbotCooperationOptions Coop => _options.Groups.Cooperation;

    /// <summary>Whether <paramref name="botId"/> offered to join a group for <paramref name="key"/> and the offer still stands (tests and status).</summary>
    internal bool IsPledged(Guid botId, (PlayerbotGroupGoalKind, uint) key) => IsPledged(botId, key, Now);

    private bool IsPledged(Guid botId, (PlayerbotGroupGoalKind, uint) key, uint now)
        => _pledges.TryGetValue(botId, out var pledge) && pledge.Key == key && unchecked((int)(pledge.Until - now)) > 0;

    /// <summary>The stable id of the waiting group <paramref name="botId"/> is in (0: it waits for none; tests and status).</summary>
    internal int WaitingGroupIdOf(Guid botId) => _waiting.TryGetValue(botId, out Waiting? waiting) ? waiting.GroupId : 0;

    /// <summary>The waiting group a bot that starts waiting for <paramref name="goal"/> joins: the id of the bots already waiting for it on its team, else a new one.</summary>
    private int WaitingGroupIdFor(PlayerbotGroupGoal goal, Team team)
    {
        foreach (Waiting other in _waiting.Values)
            if (other.Team == team && other.Goal.Key == goal.Key) return other.GroupId;
        return _nextWaitingGroupId++;
    }

    private bool HasPledge((PlayerbotGroupGoalKind, uint) key, uint now)
    {
        if (_pledges.Count == 0) return false;
        foreach (var pledge in _pledges.Values)
            if (pledge.Key == key && unchecked((int)(pledge.Until - now)) > 0) return true;
        return false;
    }

    /// <summary>The recruits who joined and have not yet said so, then forgotten: (bot, role). The voice says one line each (world thread).</summary>
    internal IReadOnlyList<(Guid BotId, PlayerbotGroupRole Role)> TakeRecruitSays()
    {
        if (_recruitSays.Count == 0) return [];
        (Guid, PlayerbotGroupRole)[] taken = [.. _recruitSays];
        _recruitSays.Clear();
        return taken;
    }

    /// <summary>
    /// The dungeon and raid groups still forming (waiting bots of one goal and team short of the group the goal asks for), with the bot waiting
    /// longest as the caller. Guild chat asks for help only for these (<see cref="PlayerbotGuildChat"/>).
    /// </summary>
    internal IReadOnlyList<PlayerbotFormingCall> FormingCalls()
    {
        if (_waiting.Count == 0 || !_options.Groups.Enabled || !Coop.Recruit || !Coop.GuildCalls) return [];
        var oldest = new Dictionary<((PlayerbotGroupGoalKind, uint), Team), (Guid BotId, Waiting Waiting, int Have)>();
        foreach ((Guid botId, Waiting waiting) in _waiting)
        {
            if (!waiting.Goal.InInstance || !_bots.TryGetValue(botId, out PlayerbotGroupBot bot) || bot.Session.Player is not { } player) continue;
            var key = (waiting.Goal.Key, player.Team);
            if (!oldest.TryGetValue(key, out var seen)) oldest[key] = (botId, waiting, 1);
            else
            {
                bool older = waiting.SinceMs != seen.Waiting.SinceMs ? unchecked((int)(waiting.SinceMs - seen.Waiting.SinceMs)) < 0 : botId.CompareTo(seen.BotId) < 0;
                oldest[key] = older ? (botId, waiting, seen.Have + 1) : (seen.BotId, seen.Waiting, seen.Have + 1);
            }
        }

        var calls = new List<PlayerbotFormingCall>();
        foreach (var entry in oldest.Values)
            if (entry.Have < entry.Waiting.Goal.Size) calls.Add(new PlayerbotFormingCall(entry.BotId, entry.Waiting.Goal, entry.Have));
        calls.Sort(static (a, b) => a.CallerBotId.CompareTo(b.CallerBotId));
        return calls;
    }

    /// <summary>
    /// A bot said "anyone for &lt;dungeon&gt;?" in the chat of guild <paramref name="guildId"/> for the group of <paramref name="key"/>: the
    /// guild's other bots count as having heard it, and are weighed at the next scan without the wait.
    /// </summary>
    internal void NoteGuildCall(int guildId, (PlayerbotGroupGoalKind, uint) key)
    {
        if (guildId <= 0 || !Coop.GuildCalls) return;
        if (_guildCalls.Count >= MaxPledges && !_guildCalls.ContainsKey((guildId, key)))
        {
            (int, (PlayerbotGroupGoalKind, uint)) oldest = default;
            uint oldestUntil = 0;
            bool any = false;
            foreach (var (callKey, until) in _guildCalls)
                if (!any || unchecked((int)(until - oldestUntil)) < 0) { oldest = callKey; oldestUntil = until; any = true; }
            _guildCalls.Remove(oldest);
        }

        _guildCalls[(guildId, key)] = unchecked(Now + PledgeLifeMs);
    }

    /// <summary>Whether guild <paramref name="guildId"/> has an open call for <paramref name="key"/> (tests and status).</summary>
    internal bool HasGuildCall(int guildId, (PlayerbotGroupGoalKind, uint) key)
        => _guildCalls.TryGetValue((guildId, key), out uint until) && unchecked((int)(until - Now)) > 0;

    private HashSet<int>? CallGuilds((PlayerbotGroupGoalKind, uint) key, uint now)
    {
        if (_guildCalls.Count == 0) return null;
        HashSet<int>? guilds = null;
        foreach (((int guild, (PlayerbotGroupGoalKind, uint) callKey), uint until) in _guildCalls)
            if (callKey == key && unchecked((int)(until - now)) > 0) (guilds ??= []).Add(guild);
        return guilds;
    }

    private void PruneRecruiting(uint now)
    {
        if (_pledges.Count > 0)
            foreach (var (botId, pledge) in _pledges)
                if (unchecked((int)(pledge.Until - now)) <= 0 || !_bots.ContainsKey(botId)) _pledges.Remove(botId);
        if (_guildCalls.Count > 0)
            foreach (var (callKey, until) in _guildCalls)
                if (unchecked((int)(until - now)) <= 0) _guildCalls.Remove(callKey);
        if (_recruitTried.Count > 0)
            foreach (var (triedKey, at) in _recruitTried)
                if (unchecked(now - at) >= RecruitRetryMs * 10) _recruitTried.Remove(triedKey);
        if (_recruitRefused.Count > 0)
        {
            // A refusal lives as long as its waiting group: once no bot waits in that group any more it is forgotten.
            var live = new HashSet<int>();
            foreach (Waiting waiting in _waiting.Values) live.Add(waiting.GroupId);
            uint period = RefusalPeriodMs;
            foreach (var (refusal, refusedAt) in _recruitRefused)
                if (!live.Contains(refusal.GroupId) || unchecked(now - refusedAt) >= period) _recruitRefused.Remove(refusal);
            if (_recruitRefused.Count > MaxRefusals) _recruitRefused.Clear();
        }
    }

    private void ResetRecruiting()
    {
        _pledges.Clear();
        ResetMentoring();
        _invitedPlayers.Clear();
        _recruitTried.Clear();
        _recruitRefused.Clear();
        _lfgPosts.Clear();
        _guildCalls.Clear();
        _recruitSays.Clear();
        _expiredVolunteers.Clear();
        _capLoggedMs = 0;
        _capEverLogged = false;
    }

    /// <summary>The cap on running groups keeps waiting bots from forming one: said once a minute at most, in the log and the group events.</summary>
    private void NoteCapBlocked()
    {
        if (_waiting.Count == 0) return;
        uint now = Now;
        if (_capEverLogged && unchecked(now - _capLoggedMs) < 60_000) return;
        _capEverLogged = true;
        _capLoggedMs = now;
        int running = OwnGroupCount;
        Note(string.Create(CultureInfo.InvariantCulture, $"the group cap ({_options.Groups.MaxGroups}) holds back {_waiting.Count} waiting bots ({running} groups running)"));
        logger.LogInformation("Playerbot groups: the cap of {Max} groups ({Running} running) holds back {Waiting} waiting bots; raise World:Playerbots:Groups:MaxGroups for more",
            _options.Groups.MaxGroups, running, _waiting.Count);
    }

    // --- the waiting group's candidates ---------------------------------------------------------------------------------

    /// <summary>
    /// The waiting bots of one goal and team that can still form it: alive, on the meeting map, high enough, near the meeting point. A bot
    /// that shares a guild with the bot waiting longest carries a <see cref="PlayerbotGroupCandidate.Bonus"/> (the guild bonus of matching).
    /// </summary>
    private List<PlayerbotGroupCandidate> BuildCandidates(IEnumerable<(Guid BotId, Waiting Waiting)> wanting, PlayerbotGroupGoal goal)
    {
        var candidates = new List<PlayerbotGroupCandidate>();
        foreach ((Guid botId, Waiting waiting) in wanting)
        {
            if (!_bots.TryGetValue(botId, out PlayerbotGroupBot bot) || bot.Session.Player is not { IsAlive: true } player || player.MapId != goal.MeetingMap) continue;
            if (player.Level < goal.MinLevel) continue;
            if (Vector3.Distance(new Vector3(player.X, player.Y, player.Z), goal.Meeting) > MaxMeetingYards) continue;
            candidates.Add(Candidate(botId, player, waiting.SinceMs) with { Qualifying = waiting.Goal.Anchors });
        }

        if (candidates.Count < 2 || !Coop.Recruit || GuildsOrNull() is not { } guilds) return candidates;
        PlayerbotGroupCandidate anchor = Oldest(candidates);
        if (!TryPlayer(anchor.BotId, out Player? anchorPlayer) || guilds.GetGuildOf(anchorPlayer) is not { } anchorGuild) return candidates;
        for (int i = 0; i < candidates.Count; i++)
        {
            PlayerbotGroupCandidate candidate = candidates[i];
            if (TryPlayer(candidate.BotId, out Player? mate) && guilds.GetGuildOf(mate)?.Id == anchorGuild.Id)
                candidates[i] = candidate with { Bonus = 1 };
        }

        return candidates;
    }

    private static PlayerbotGroupCandidate Oldest(List<PlayerbotGroupCandidate> candidates)
    {
        PlayerbotGroupCandidate oldest = candidates[0];
        for (int i = 1; i < candidates.Count; i++)
        {
            PlayerbotGroupCandidate c = candidates[i];
            int by = unchecked((int)(c.NeedSinceMs - oldest.NeedSinceMs));
            if (by < 0 || (by == 0 && c.BotId.CompareTo(oldest.BotId) < 0)) oldest = c;
        }

        return oldest;
    }

    private bool TryPlayer(Guid botId, out Player player)
    {
        if (_bots.TryGetValue(botId, out PlayerbotGroupBot bot) && bot.Session.Player is { } found)
        {
            player = found;
            return true;
        }

        player = null!;
        return false;
    }

    private GuildManager? GuildsOrNull()
    {
        try
        {
            return services.GetService<SocialFeature>()?.Context.Guilds;
        }
        catch (InvalidOperationException ex)
        {
            FaultLedger.Shared.Record("playerbot.groups", ex);
            return null;
        }
    }

    // --- recruiting ----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <see cref="Match"/>'s recruiting step: once per <see cref="RecruitRetryMs"/> per goal and team (at once when a bot offered). Null when
    /// nothing is to be done now; otherwise the plan with the recruits and their ids.
    /// </summary>
    private PlayerbotGroupPlan? RecruitFor(List<PlayerbotGroupCandidate> waiting, int size, PlayerbotGroupGoal goal, Team team, out List<Guid> recruited)
    {
        recruited = [];
        if (!Coop.Recruit || waiting.Count == 0) return null;
        uint now = Now;
        bool pledged = HasPledge(goal.Key, now);
        var tried = (goal.Key, team);
        if (!pledged && _recruitTried.TryGetValue(tried, out uint at) && unchecked(now - at) < RecruitRetryMs) return null;
        _recruitTried[tried] = now;
        return PlanWithRecruits(waiting, size, goal, team, Guid.Empty, out recruited);
    }

    /// <summary>
    /// A group plan from <paramref name="waiting"/> plus recruits, or null. <paramref name="offerBy"/> (not empty): a dry run for one bot that
    /// answered the caller's line, weighed at once with the answer bonus; it comes to nothing unless the plan holds it. Otherwise the whole
    /// pool is weighed when the wait is over, else only the bots that offered or heard a guild call. A pledged bot is not rolled again.
    /// </summary>
    private PlayerbotGroupPlan? PlanWithRecruits(List<PlayerbotGroupCandidate> waiting, int size, PlayerbotGroupGoal goal, Team team, Guid offerBy, out List<Guid> recruited)
    {
        recruited = [];
        PlayerbotCooperationOptions coop = Coop;
        if (!coop.Recruit || waiting.Count == 0 || GroupManagerOrNull() is not { } groups) return null;
        // The anchor is the longest-waiting waiter that qualifies (in band or with a direct reason); none: nothing to recruit for.
        if (PlayerbotGroupContent.QualifyingAnchor(waiting) is not { } anchorCandidate || !TryPlayer(anchorCandidate.BotId, out Player? anchor)) return null;
        uint now = Now;
        bool offer = offerBy != Guid.Empty;
        bool waited = unchecked(now - anchorCandidate.NeedSinceMs) >= (uint)coop.RecruitWaitSeconds * 1000u;
        HashSet<int>? callGuilds = coop.GuildCalls ? CallGuilds(goal.Key, now) : null;
        if (!offer && !waited && !HasPledge(goal.Key, now) && callGuilds is null) return null;

        (int Tanks, int Healers) missing = PlayerbotRecruitment.MissingRoles(waiting, size, _options.Groups);
        int slots = Math.Max(0, size - waiting.Count);
        if (slots == 0 && missing.Tanks + missing.Healers == 0) return null;

        PlayerbotSocialMemory? memory = Social is { Enabled: true } recorder ? recorder.Memory : null;
        GuildManager? guilds = GuildsOrNull();
        HashSet<int>? memberGuilds = null;
        if (guilds is not null)
            foreach (PlayerbotGroupCandidate member in waiting)
                if (TryPlayer(member.BotId, out Player? memberPlayer) && guilds.GetGuildOf(memberPlayer) is { } memberGuild) (memberGuilds ??= []).Add(memberGuild.Id);

        float radius = coop.RecruitRadiusYards;
        float radiusSq = radius * radius;
        var anchorAt = new Vector3(anchor.X, anchor.Y, anchor.Z);
        int groupId = _waiting.TryGetValue(anchorCandidate.BotId, out Waiting? anchorWaiting) ? anchorWaiting.GroupId : 0; // stable while the group waits
        uint refusalPeriod = RefusalPeriodMs;
        var ranked = new List<PlayerbotRecruitment.Ranked>();
        int botCount = offer ? 1 : _orderedBots.Count; // an offer weighs the one bot that made it, found directly
        for (int i = 0; i < botCount; i++)
        {
            PlayerbotGroupBot bot;
            if (offer)
            {
                if (!_bots.TryGetValue(offerBy, out bot)) break;
            }
            else
            {
                bot = _orderedBots[i];
            }

            if (!bot.Free || bot.Scripted || bot.Brain.Stay is not null || _memberOf.ContainsKey(bot.BotId) || _waiting.ContainsKey(bot.BotId)) continue;
            if (bot.Session.Player is not { IsInWorld: true, IsAlive: true } player || player.Combat.IsInCombat) continue;
            if (player.Team != team || player.MapId != goal.MeetingMap || player.Level < goal.MinLevel) continue;
            if (Math.Abs(player.Level - anchor.Level) > _options.Groups.LevelRange) continue;
            float dx = player.X - anchorAt.X, dy = player.Y - anchorAt.Y;
            float distSq = (dx * dx) + (dy * dy);
            if (distSq > radiusSq) continue;
            bool pledged = !offer && IsPledged(bot.BotId, goal.Key, now);
            int guildId = guilds?.GetGuildOf(player)?.Id ?? 0;
            bool called = guildId != 0 && callGuilds is not null && callGuilds.Contains(guildId);
            if (!offer && !waited && !pledged && !called) continue;
            if (PlayerbotMageService.IsServing(player) || PlayerbotMageService.IsActive(player)) continue;
            if (IsSetAside(bot.BotId, goal.Key) || (!offer && _recruitRefused.TryGetValue((groupId, bot.BotId), out uint refusedAt) && unchecked(now - refusedAt) < refusalPeriod)) continue;
            if (!PlayerbotRecruitment.GoalYields(bot.Brain.Goal, bot.Brain.Goal == PlayerbotGoalKind.Quest ? RemainingObjectives(player, bot.Brain.QuestId) : 0)) continue;
            if (groups.GetGroup(player.Guid) is not null || groups.GetInvite(player.Guid) is not null) continue;

            // What the candidate thinks of the group, and what the group thinks of it (a bot that holds the other as a rival never sits down with it).
            int affinityToAnchor = 0, worst = 0, affinitySum = 0, trustSum = 0;
            bool rival = false, known = false;
            if (memory is not null)
            {
                foreach (PlayerbotGroupCandidate member in waiting)
                {
                    if (memory.TryGet(bot.BotId, member.Guid, out PlayerbotRelation toMember))
                    {
                        int affinity = memory.EffectiveAffinity(toMember);
                        rival |= memory.IsRival(toMember);
                        worst = known ? Math.Min(worst, affinity) : affinity;
                        known = true;
                        affinitySum += affinity;
                        trustSum += toMember.TrustPermille - 500;
                        if (member.BotId == anchorCandidate.BotId) affinityToAnchor = affinity;
                    }

                    if (memory.TryGet(member.BotId, player.Guid.Value, out PlayerbotRelation fromMember)
                        && (memory.IsRival(fromMember) || memory.EffectiveAffinity(fromMember) <= coop.RefuseAffinity))
                        rival = true;
                }
            }

            bool fillsRole = (missing.Tanks > 0 && PlayerbotGroupContent.CanTank(player.Class)) || (missing.Healers > 0 && PlayerbotGroupContent.CanHeal(player.Class));
            bool guildmate = guildId != 0 && memberGuilds is not null && memberGuilds.Contains(guildId);
            var facts = new PlayerbotRecruitFacts(PlayerbotTraits.For(player.Guid.Value).Sociability, affinityToAnchor, worst, rival, fillsRole, guildmate,
                AnsweredCall: offer || pledged || called);
            if (PlayerbotRecruitment.Refuses(coop, facts)) continue;
            if (!pledged && !PlayerbotRecruitment.Accepts(coop, player.Guid.Value, PlayerbotRecruitment.RollGroup(groupId, PlayerbotRecruitment.RefusalWindow(now, refusalPeriod, player.Guid.Value, groupId)), facts))
            {
                if (!offer) _recruitRefused[(groupId, bot.BotId)] = now; // decided: not asked again for this group
                continue;
            }

            int preference = PlayerbotRecruitment.Preference(affinitySum, trustSum, guildmate, coop) + (pledged ? 1000 : 0);
            ranked.Add(new PlayerbotRecruitment.Ranked(Candidate(bot.BotId, player, now) with { Qualifying = PlayerbotGroupContent.QualifiesAtLevel(goal, player.Level) }, preference, distSq,
                PlayerbotGroupContent.CanTank(player.Class), PlayerbotGroupContent.CanHeal(player.Class)));
        }

        if (ranked.Count == 0) return null;
        List<PlayerbotGroupCandidate> picks = PlayerbotRecruitment.Order(ranked, missing, slots);
        if (picks.Count == 0) return null;
        var all = new List<PlayerbotGroupCandidate>(waiting.Count + picks.Count);
        all.AddRange(waiting);
        all.AddRange(picks);
        if (PlayerbotGroupContent.Match(all, size, _options.Groups, goal.InInstance) is not { } plan) return null;
        foreach ((PlayerbotGroupCandidate member, PlayerbotGroupRole _, byte _) in plan.Members)
            if (!waiting.Exists(w => w.BotId == member.BotId)) recruited.Add(member.BotId);
        return recruited.Count == 0 ? null : plan;
    }

    /// <summary>
    /// A bot heard <paramref name="posterBotId"/>, which waits for a group, call for more in a world channel (docs/areas/playbots.md, Bot
    /// voice): it offers to come when it fits the group (the rules of recruiting, with <see cref="PlayerbotCooperationOptions.LfgAnswerBonus"/>)
    /// and the coordinator can build a group with it. Pledged: the bot is held for the group and joins at the next scan without the wait or a
    /// second roll; the voice then answers "inv me". Anything else: no answer is given.
    /// </summary>
    internal PlayerbotLfgOffer OfferLfg(Guid hearerBotId, Guid posterBotId, long postId = 0)
    {
        try
        {
            return OfferLfgCore(hearerBotId, posterBotId, postId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("playerbot.groups", ex);
            logger.LogFault("playerbot.group-lfg", ex, "Playerbot LFG answer failed");
            return new(PlayerbotLfgOfferOutcome.Declined);
        }
    }

    /// <summary>
    /// What one LFG post costs, computed once for all its hearers however many ticks they hear it over (keyed by the poster and the post's
    /// own identity, <c>postId</c>: the voice board's time of the post): the caller, the waiting bots of its goal and team as candidates, and
    /// the anchor's level. <see cref="Answers"/> counts the places held for the post, for the post's life (<see cref="LfgPostLifeMs"/>).
    /// </summary>
    private sealed class LfgLine
    {
        internal Guid Poster;
        internal long PostId;
        internal uint CreatedMs;
        internal Waiting? Caller;
        internal Team Team;
        internal int Size;
        internal int AnchorLevel;
        internal int Answers;
        internal List<PlayerbotGroupCandidate> Candidates = [];
    }

    private const int MaxLfgPosts = 64;
    private readonly List<LfgLine> _lfgPosts = [];

    /// <summary>How many times an LFG line's shared data was built (tests: one line heard by many bots builds it once).</summary>
    internal int LfgLineBuilds { get; private set; }

    private PlayerbotLfgOffer OfferLfgCore(Guid hearerBotId, Guid posterBotId, long postId)
    {
        PlayerbotCooperationOptions coop = Coop;
        if (_world is null || !_options.Groups.Enabled || !coop.Recruit || !coop.AnswerBotCalls || hearerBotId == posterBotId
            || !_waiting.TryGetValue(posterBotId, out Waiting? caller) || !_bots.TryGetValue(posterBotId, out PlayerbotGroupBot posterBot) || posterBot.Session.Player is not { } poster)
            return new(PlayerbotLfgOfferOutcome.NotWaiting);
        uint now = Now;
        if (IsPledged(hearerBotId, caller.Goal.Key, now)) return new(PlayerbotLfgOfferOutcome.AlreadyPledged);

        // The hearer's own cheap rejects first, with nothing allocated: free, alive and out of combat, of the poster's team, on the meeting map, high enough.
        if (!_bots.TryGetValue(hearerBotId, out PlayerbotGroupBot hearer) || !hearer.Free || hearer.Scripted || hearer.Brain.Stay is not null
            || _memberOf.ContainsKey(hearerBotId) || _waiting.ContainsKey(hearerBotId)
            || hearer.Session.Player is not { IsInWorld: true, IsAlive: true } hearerPlayer || hearerPlayer.Combat.IsInCombat
            || hearerPlayer.Team != poster.Team || hearerPlayer.MapId != caller.Goal.MeetingMap || hearerPlayer.Level < caller.Goal.MinLevel)
            return new(PlayerbotLfgOfferOutcome.Declined);

        LfgLine line = LfgPostFor(posterBotId, postId, caller, poster, now);
        if (line.Candidates.Count == 0 || line.Answers >= coop.MaxLfgAnswersPerLine || Math.Abs(hearerPlayer.Level - line.AnchorLevel) > _options.Groups.LevelRange)
            return new(PlayerbotLfgOfferOutcome.Declined);

        PlayerbotGroupPlan? plan = PlanWithRecruits(line.Candidates, line.Size, caller.Goal, line.Team, hearerBotId, out List<Guid> recruited);
        if (plan is null || !recruited.Contains(hearerBotId)) return new(PlayerbotLfgOfferOutcome.Declined);
        if (_pledges.Count >= MaxPledges && !MakeRoomForPledge()) return new(PlayerbotLfgOfferOutcome.Declined);
        _pledges[hearerBotId] = (caller.Goal.Key, unchecked(now + PledgeLifeMs));
        line.Answers++;
        PlayerbotGroupRole role = PlayerbotGroupRole.Damage;
        foreach ((PlayerbotGroupCandidate member, PlayerbotGroupRole memberRole, byte _) in plan.Members)
            if (member.BotId == hearerBotId) role = memberRole;
        return new(PlayerbotLfgOfferOutcome.Pledged, role);
    }

    /// <summary>How long an LFG post's entry (and its answer count) lives: as long as the post on the voice board.</summary>
    private uint LfgPostLifeMs => (uint)_options.Chat.Voice.PostLifeSeconds * 1000u;

    private void PruneLfgPosts(uint now)
    {
        uint life = LfgPostLifeMs;
        for (int i = _lfgPosts.Count - 1; i >= 0; i--)
            if (unchecked(now - _lfgPosts[i].CreatedMs) > life) _lfgPosts.RemoveAt(i);
    }

    /// <summary>The places held for a post (tests): its answer count after the expired entries are dropped, or -1 when it has no entry.</summary>
    internal int LfgAnswersOf(Guid poster, long postId)
    {
        PruneLfgPosts(Now);
        foreach (LfgLine entry in _lfgPosts)
            if (entry.Poster == poster && entry.PostId == postId) return entry.Answers;
        return -1;
    }

    /// <summary>
    /// The entry of the post (at most <see cref="MaxLfgPosts"/>, the oldest dropped): found without allocating; made, or its data built again
    /// (the answer count kept), when the caller's waiting entry is another or a candidate has stopped waiting.
    /// </summary>
    private LfgLine LfgPostFor(Guid posterBotId, long postId, Waiting caller, Player poster, uint now)
    {
        PruneLfgPosts(now);
        LfgLine? line = null;
        for (int i = _lfgPosts.Count - 1; i >= 0; i--)
        {
            LfgLine entry = _lfgPosts[i];
            if (entry.Poster == posterBotId && entry.PostId == postId) line = entry;
        }

        if (line is null)
        {
            if (_lfgPosts.Count >= MaxLfgPosts) _lfgPosts.RemoveAt(0);
            line = new LfgLine { Poster = posterBotId, PostId = postId, CreatedMs = now };
            _lfgPosts.Add(line);
            BuildLfgLine(line, caller, poster);
            return line;
        }

        bool stale = !ReferenceEquals(line.Caller, caller);
        for (int i = 0; !stale && i < line.Candidates.Count; i++)
            stale = !_waiting.ContainsKey(line.Candidates[i].BotId);
        if (stale) BuildLfgLine(line, caller, poster);
        return line;
    }

    private void BuildLfgLine(LfgLine line, Waiting caller, Player poster)
    {
        LfgLineBuilds++;
        line.Caller = caller;
        line.Team = poster.Team;
        line.AnchorLevel = 0;
        var wanting = new List<(Guid BotId, Waiting Waiting)>();
        int size = 0;
        foreach ((Guid botId, Waiting waiting) in _waiting)
        {
            if (waiting.Goal.Key != caller.Goal.Key || !TryPlayer(botId, out Player? other) || other.Team != line.Team) continue;
            wanting.Add((botId, waiting));
            size = Math.Max(size, waiting.Goal.Size);
        }

        line.Size = size;
        line.Candidates = BuildCandidates(wanting, caller.Goal);
        if (PlayerbotGroupContent.QualifyingAnchor(line.Candidates) is { } qualifying && TryPlayer(qualifying.BotId, out Player? anchor)) line.AnchorLevel = anchor.Level;
        else line.Candidates = [];
    }

    /// <summary>
    /// The pledge table is full: drops the oldest pledge whose goal has no waiting group any more (nothing for it to join). False when
    /// every pledge still has a live group: the new offer is not taken.
    /// </summary>
    private bool MakeRoomForPledge()
    {
        var live = new HashSet<(PlayerbotGroupGoalKind, uint)>();
        foreach (Waiting waiting in _waiting.Values) live.Add(waiting.Goal.Key);
        Guid oldest = Guid.Empty;
        uint oldestUntil = 0;
        foreach (var (botId, pledge) in _pledges)
        {
            if (live.Contains(pledge.Key)) continue;
            if (oldest == Guid.Empty || unchecked((int)(pledge.Until - oldestUntil)) < 0) { oldest = botId; oldestUntil = pledge.Until; }
        }

        return oldest != Guid.Empty && _pledges.Remove(oldest);
    }

    /// <summary>
    /// How many kills and items the quest still asks of the player: <see cref="int.MaxValue"/> when it is no quest of the log yet (the bot is on
    /// its way to take it), 0 when every objective is done (the bot is on its way to hand it in). A bot within
    /// <see cref="PlayerbotRecruitment.NearlyDoneObjectives"/> of finishing its quest is not pulled away from it.
    /// </summary>
    private int RemainingObjectives(Player player, uint questId)
    {
        if (questId == 0 || services.GetService<QuestNpcFeature>()?.Services is not { } quests || quests.StateOf(player) is not { Loaded: true } state
            || quests.Quests.Get(questId) is not { } quest || state.Quests.Get(questId) is not { } status) return int.MaxValue;
        if (status.Status != QuestStatus.Incomplete) return 0;
        int left = 0;
        for (int index = 0; index < quest.ReqCreatureOrGOId.Count; index++)
            if (quest.ReqCreatureOrGOId[index] != 0)
                left += Math.Max(0, (int)quest.ReqCreatureOrGOCount[index] - (int)status.CreatureOrGOCount[index]);
        for (int index = 0; index < quest.ReqItemId.Count; index++)
            if (quest.ReqItemId[index] != 0)
                left += Math.Max(0, (int)quest.ReqItemCount[index] - (int)player.Inventory.GetItemCount(quest.ReqItemId[index]));
        return left;
    }

    /// <summary>After <see cref="Form"/>: the recruits who were not pledged say so (the voice drains <see cref="TakeRecruitSays"/>), and every pledge of theirs ends.</summary>
    private void NoteRecruited(PlayerbotGroupPlan plan, List<Guid> recruited)
    {
        foreach ((PlayerbotGroupCandidate member, PlayerbotGroupRole role, byte _) in plan.Members)
        {
            if (!recruited.Contains(member.BotId)) continue;
            if (!_pledges.Remove(member.BotId) && _recruitSays.Count < 64) _recruitSays.Add((member.BotId, role));
            Note($"{member.Name} is recruited as {role.ToString().ToLowerInvariant()}");
        }
    }
}
