using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Party;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>One guild member as the promotion planner sees it.</summary>
internal readonly record struct PlayerbotGuildMemberFacts(uint CharacterId, string Name, byte Rank, byte Level, bool IsLeader);

/// <summary>
/// Bot guild membership (docs/areas/playbots-social.md): the guild's bots invite other bots up to <see cref="PlayerbotSocialGuildOptions.TargetSize"/>,
/// every bot answers the invitations it receives, and a bot guild's master hands out ranks. The rules that need no world are static
/// (and tested alone); the rest runs on the world thread through the ordinary guild packets of the bot's own session.
/// <para>
/// <b>Answering.</b> An invitation is read from the server's state (<see cref="GuildManager.InvitedTo"/>), the inviter's name from the
/// SMSG_GUILD_INVITE packet (which the bounded capture queue may have dropped). The invitation of a founding bot's guild is accepted
/// (CMSG_GUILD_ACCEPT, mangoszero GuildAcceptAction.cpp); one from another managed bot that was not expected is declined; one from a real
/// player is accepted by <see cref="PlayerbotSocialGuildOptions.InvitePolicy"/> and <c>World:Playerbots:Party:Allowlist</c>
/// (<see cref="PlayerbotGroupInvites.Allows"/>), and declined (CMSG_GUILD_DECLINE) otherwise. With no packet and no expectation the answer is
/// a decline: fail closed.
/// </para>
/// </summary>
internal sealed class PlayerbotGuildMembership(ILogger logger)
{
    /// <summary>The level at which an Initiate becomes a Member (the default rank names, vmangos CreateDefaultGuildRanks).</summary>
    internal const byte MemberLevel = 10;

    /// <summary>The level at which a Member becomes a Veteran.</summary>
    internal const byte VeteranLevel = 40;

    /// <summary>The default rank ids (vmangos Guild.cpp CreateDefaultGuildRanks): Guild Master, Officer, Veteran, Member, Initiate.</summary>
    internal const byte RankOfficer = Guild.OfficerRank, RankVeteran = 2, RankMember = 3, RankInitiate = 4;

    private readonly Dictionary<uint, (int GuildId, long SinceMs)> _pending = [];
    private readonly Dictionary<Guid, (string Inviter, string Guild)> _seenInvites = [];
    private readonly Dictionary<(int Guild, uint Candidate), long> _refused = [];
    private readonly Dictionary<int, long> _lastRecruit = [];
    private readonly Dictionary<int, long> _lastPromotion = [];
    private readonly Dictionary<uint, (int GuildId, long SinceMs)> _offered = [];
    private readonly Dictionary<uint, long> _offeredAt = [];
    private readonly Dictionary<int, long> _lastAffinityInvite = [];

    // --- rules without a world ----------------------------------------------------------------------------------------

    /// <summary>The guilds led by a registered bot: the bot guilds (a guild whose leader is stopped still counts; one founded by a player does not).</summary>
    internal static int CountBotGuilds(IEnumerable<Guild> guilds, IReadOnlySet<uint> registered)
    {
        ArgumentNullException.ThrowIfNull(guilds);
        ArgumentNullException.ThrowIfNull(registered);
        int count = 0;
        foreach (Guild guild in guilds)
            if (registered.Contains(guild.LeaderId)) count++;
        return count;
    }

    /// <summary>The invitations a guild may still send: members plus pending invitations never exceed <paramref name="targetSize"/>.</summary>
    internal static int InviteRoom(int members, int pending, int targetSize) => Math.Max(0, targetSize - members - pending);

    /// <summary>The bots a charter founding needs: the founder and one signer for each signature the server wants (<see cref="GuildOptions.EffectiveMinPetitionSigns"/>).</summary>
    internal static int CharterCandidatesNeeded(GuildOptions guildOptions)
    {
        ArgumentNullException.ThrowIfNull(guildOptions);
        return guildOptions.EffectiveMinPetitionSigns + 1;
    }

    /// <summary>How a guild is founded now: <see cref="PlayerbotGuildCreation.Auto"/> is a client create when the server honours it, a charter otherwise.</summary>
    internal static PlayerbotGuildCreation ResolveCreation(PlayerbotGuildCreation configured, GuildOptions guildOptions)
    {
        ArgumentNullException.ThrowIfNull(guildOptions);
        return configured switch
        {
            PlayerbotGuildCreation.Charter => PlayerbotGuildCreation.Charter,
            PlayerbotGuildCreation.ClientCreate => PlayerbotGuildCreation.ClientCreate,
            _ => guildOptions.AllowClientGuildCreate ? PlayerbotGuildCreation.ClientCreate : PlayerbotGuildCreation.Charter,
        };
    }

    /// <summary>Whether a real player's guild invitation (or charter) is accepted: the party invite policy, the allowlist always.</summary>
    internal static bool AcceptsRealPlayer(PlayerbotInvitePolicy policy, IReadOnlyCollection<string> allowlist, string inviter, bool onBotsFriendList)
        => PlayerbotGroupInvites.Allows(policy, allowlist, inviter, sameGuild: false, onBotsFriendList);

    /// <summary>The officers a guild of <paramref name="members"/> members gets: one for every ten, rounded up.</summary>
    internal static int OfficersWanted(int members) => members <= 0 ? 0 : (members + 9) / 10;

    /// <summary>
    /// The next promotion of a bot guild's master (one step; the server moves a member up one rank), or null. Officers first: with fewer
    /// than <see cref="OfficersWanted"/>, the highest-level non-leader below officer who has a Member's level climbs one rank; otherwise an
    /// Initiate of level 10 or more becomes a Member and a Member of level 40 or more a Veteran. Nobody is ever promoted past Officer, and a
    /// guild whose ranks were changed from the five defaults is left alone.
    /// </summary>
    internal static PlayerbotGuildMemberFacts? NextPromotion(IReadOnlyList<PlayerbotGuildMemberFacts> members, int rankCount)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (rankCount != Guild.MinRanks) return null;
        int officers = 0;
        foreach (PlayerbotGuildMemberFacts member in members)
            if (member.Rank == RankOfficer && !member.IsLeader) officers++;
        if (officers < OfficersWanted(members.Count))
        {
            PlayerbotGuildMemberFacts? best = null;
            foreach (PlayerbotGuildMemberFacts member in members)
            {
                if (member.IsLeader || member.Rank <= RankOfficer || member.Level < MemberLevel) continue;
                if (best is null || member.Level > best.Value.Level
                    || (member.Level == best.Value.Level && (member.Rank < best.Value.Rank
                        || (member.Rank == best.Value.Rank && string.CompareOrdinal(member.Name, best.Value.Name) < 0))))
                    best = member;
            }

            if (best is not null) return best;
        }

        PlayerbotGuildMemberFacts? next = null;
        foreach (PlayerbotGuildMemberFacts member in members)
        {
            bool due = !member.IsLeader && ((member.Rank == RankInitiate && member.Level >= MemberLevel) || (member.Rank == RankMember && member.Level >= VeteranLevel));
            if (due && (next is null || string.CompareOrdinal(member.Name, next.Value.Name) < 0)) next = member;
        }

        return next;
    }

    // --- state --------------------------------------------------------------------------------------------------------

    /// <summary>The invitations of this lane that are out: bot character id to guild.</summary>
    internal int PendingCount => _pending.Count;

    /// <summary>Whether a recruiting invitation to <paramref name="characterId"/> is out.</summary>
    internal bool IsPending(uint characterId) => _pending.ContainsKey(characterId);

    internal int PendingFor(int guildId)
    {
        int count = 0;
        foreach ((int guild, _) in _pending.Values)
            if (guild == guildId) count++;
        foreach ((int guild, _) in _offered.Values)
            if (guild == guildId) count++;
        return count;
    }

    /// <summary>
    /// Whether a guildless bot takes the invitation of an inviter, a player or a bot: its affinity toward the inviter
    /// (<c>EffectiveAffinity</c>) reaches <paramref name="acceptAffinity"/> and the inviter is no rival.
    /// </summary>
    internal static bool AcceptsByAffinity(PlayerbotSocialMemory memory, Guid botId, ulong inviterGuid, int acceptAffinity)
        => memory.TryGet(botId, inviterGuid, out PlayerbotRelation relation) && !memory.IsRival(relation) && memory.EffectiveAffinity(relation) >= acceptAffinity;

    /// <summary>
    /// Whether <paramref name="relation"/> (the inviter's memory of a guildless character) earns a guild invitation: affinity at least
    /// <see cref="PlayerbotSocialGuildOptions.GuildInviteAffinity"/> and familiarity at least <see cref="PlayerbotSocialGuildOptions.GuildInviteMinFamiliarity"/>,
    /// no rival.
    /// </summary>
    internal static bool WorthInviting(PlayerbotSocialMemory memory, PlayerbotSocialGuildOptions options, PlayerbotRelation relation)
        => !memory.IsRival(relation) && memory.EffectiveAffinity(relation) >= options.GuildInviteAffinity && relation.Familiarity >= options.GuildInviteMinFamiliarity;

    /// <summary>A packet of SMSG_GUILD_INVITE drained from a bot (the inviter's name and the guild's).</summary>
    internal void NoteInvite(Guid botId, string inviter, string guild) => _seenInvites[botId] = (inviter, guild);

    /// <summary>Whether an invitation packet of <paramref name="botId"/> was noted and not yet answered (tests wait on it instead of a delay).</summary>
    internal bool HasSeenInvite(Guid botId) => _seenInvites.ContainsKey(botId);

    // --- the world thread ---------------------------------------------------------------------------------------------

    /// <summary>Answer every pending invitation of the running bots (always, whatever <see cref="PlayerbotSocialGuildOptions.Enabled"/> says).</summary>
    internal void AnswerInvites(PlayerbotSocialPass pass)
    {
        GuildManager guilds = pass.Social.Guilds;
        foreach (PlayerbotSocialBot bot in pass.Bots)
        {
            int guildId = guilds.InvitedTo(bot.Player);
            if (guildId == 0)
            {
                _pending.Remove(bot.Player.Guid.Low);
                _seenInvites.Remove(bot.Id);
                continue;
            }

            bool accept = DecideInvite(pass, bot, guildId);
            bool sent = Send(bot, accept ? WorldOpcode.CmsgGuildAccept : WorldOpcode.CmsgGuildDecline);
            _pending.Remove(bot.Player.Guid.Low);
            _seenInvites.Remove(bot.Id);
            if (accept)
            {
                if (guilds.GetGuildOf(bot.Player) is { } joined)
                    logger.LogDebug("Playerbot {Bot} joined the guild {Guild}", bot.Player.Name, joined.Name);
                else if (!bot.Session.IsInChaosProbe)
                    PlayerbotAnomalies.Report(bot.Player, "social", "guild-action-failed",
                        $"guild invite acceptance for guild {guildId} failed (sent={sent})");
            }
        }
    }

    private bool DecideInvite(PlayerbotSocialPass pass, PlayerbotSocialBot bot, int guildId)
    {
        if (_pending.TryGetValue(bot.Player.Guid.Low, out (int GuildId, long SinceMs) expected) && expected.GuildId == guildId) return true;
        if (!_seenInvites.TryGetValue(bot.Id, out (string Inviter, string Guild) seen)) return false; // no packet, no expectation: decline
        if (pass.Social.Guilds.Get(guildId) is not { } guild || !string.Equals(guild.Name, seen.Guild, StringComparison.OrdinalIgnoreCase)) return false;
        if (pass.World.FindOnlinePlayer(seen.Inviter) is not { } inviter) return false;
        // The operator's policy comes first: None declines everyone (the allowlist apart); affinity only widens a policy that allows something.
        if (pass.Options.Guilds.InvitePolicy != PlayerbotInvitePolicy.None && pass.Options.Guilds.AffinityInvites && pass.Recorder is { Enabled: true } recorder
            && AcceptsByAffinity(recorder.Memory, bot.Id, inviter.Guid.Value, pass.Options.Guilds.GuildAcceptAffinity)) return true;
        if (inviter.Session is WorldSession { IsManaged: true }) return false; // another managed bot that no founding of ours expected
        return AcceptsRealPlayer(pass.Options.Guilds.InvitePolicy, pass.Bot.Party.Allowlist, inviter.Name,
            PlayerbotGroupInvites.OnBotsFriendList(pass.Social, bot.Player, inviter));
    }

    /// <summary>Answer the signature list a real player's charter offer showed a bot: sign by the invite policy.</summary>
    internal bool AcceptsCharterOf(PlayerbotSocialPass pass, PlayerbotSocialBot bot, Player owner)
        => AcceptsRealPlayer(pass.Options.Guilds.InvitePolicy, pass.Bot.Party.Allowlist, owner.Name,
            PlayerbotGroupInvites.OnBotsFriendList(pass.Social, bot.Player, owner));

    /// <summary>
    /// Each bot guild with an online, non-scripted master or officer invites one eligible bot of its team at most every
    /// <see cref="PlayerbotSocialTimings.RecruitSpacingMs"/>, while members plus pending invitations stay under the target size.
    /// </summary>
    internal void Recruit(PlayerbotSocialPass pass, PlayerbotGuildFounding founding)
    {
        PlayerbotSocialGuildOptions options = pass.Options.Guilds;
        GuildManager guilds = pass.Social.Guilds;
        ExpirePending(pass);
        if (!options.Enabled) return;
        foreach (Guild guild in guilds.All.ToArray())
        {
            if (!pass.Registered.Contains(guild.LeaderId)) continue;
            if (_lastRecruit.TryGetValue(guild.Id, out long last) && pass.NowMs - last < pass.Timings.RecruitSpacingMs) continue;
            if (InviteRoom(guild.MemberCount, PendingFor(guild.Id), options.TargetSize) <= 0) continue;
            PlayerbotSocialBot? inviter = InviterOf(pass, guild);
            if (inviter is null) continue;
            PlayerbotSocialBot? candidate = CandidateFor(pass, founding, guild, inviter);
            if (candidate is null) continue;
            _lastRecruit[guild.Id] = pass.NowMs;
            bool sent = Send(inviter, WorldOpcode.CmsgGuildInvite, PlayerbotSocialPackets.GuildInvite(candidate.Player.Name));
            if (guilds.InvitedTo(candidate.Player) == guild.Id)
            {
                _pending[candidate.Player.Guid.Low] = (guild.Id, pass.NowMs);
            }
            else
            {
                _refused[(guild.Id, candidate.Player.Guid.Low)] = pass.NowMs;
                if (!inviter.Session.IsInChaosProbe)
                    PlayerbotAnomalies.Report(inviter.Player, "social", "guild-action-failed",
                        $"guild recruitment invite from {inviter.Player.Name} to {candidate.Player.Name} for '{guild.Name}' failed (sent={sent})");
            }
        }

        if (_refused.Count > 256) _refused.Clear();
    }

    /// <summary>The "invites off" store (set by the social feature; null: nobody has opted out). A player in it gets no invitation by affinity.</summary>
    internal PlayerbotInviteOptOut? InviteOptOut { get; set; }

    /// <summary>
    /// A bot-led guild's master or officer (a guild whose leader is no registered bot is left alone, as in <see cref="Recruit"/>) invites a guildless same-team character (player or bot) it likes (<see cref="WorthInviting"/>), at most
    /// one invitation per character per <see cref="PlayerbotSocialGuildOptions.GuildInviteCooldownDays"/> and one per guild per
    /// <see cref="PlayerbotSocialTimings.RecruitSpacingMs"/>, while the guild has room. The guild invite goes through CMSG_GUILD_INVITE, so the
    /// server's rights check applies; the invited bot answers it by <see cref="AcceptsByAffinity"/>.
    /// </summary>
    internal void InviteByAffinity(PlayerbotSocialPass pass)
    {
        PlayerbotSocialGuildOptions options = pass.Options.Guilds;
        GuildManager guilds = pass.Social.Guilds;
        foreach ((uint id, (int guildId, long since)) in _offered.ToArray())
        {
            Player? invited = pass.World.FindOnlinePlayer(new ObjectGuid(id));
            if (guilds.Get(guildId) is null || invited is null || guilds.InvitedTo(invited) != guildId || pass.NowMs - since >= pass.Timings.PendingInviteTimeoutMs)
                _offered.Remove(id);
        }

        if (!options.Enabled || !options.AffinityInvites || pass.Recorder is not { Enabled: true } recorder) return;
        long cooldownMs = options.GuildInviteCooldownDays * 86_400_000L;
        foreach (Guild guild in guilds.All.ToArray())
        {
            if (!pass.Registered.Contains(guild.LeaderId)) continue; // a guild led by a real player is theirs to recruit for (as in Recruit)
            if (_lastAffinityInvite.TryGetValue(guild.Id, out long last) && pass.NowMs - last < pass.Timings.RecruitSpacingMs) continue;
            if (InviteRoom(guild.MemberCount, PendingFor(guild.Id), options.TargetSize) <= 0) continue;
            if (InviterOf(pass, guild) is not { } inviter) continue;
            PlayerbotRelation? best = null;
            Player? target = null;
            foreach (PlayerbotRelation relation in recorder.Memory.Relations(inviter.Id))
            {
                if (!WorthInviting(recorder.Memory, options, relation)) continue;
                if (InviteOptOut is { } optOut && pass.World.FindOnlinePlayer(new ObjectGuid(relation.OtherGuid)) is { Session: WorldSession { IsManaged: false } } asked
                    && optOut.IsOptedOut(asked.Guid.Value, asked.AccountId)) continue; // "invites off": a real player who opted out is never asked unasked
                if (pass.World.FindOnlinePlayer(new ObjectGuid(relation.OtherGuid)) is not { IsAlive: true } other || other.Team != inviter.Player.Team) continue;
                if (other.IsGameMaster || guilds.GetGuildOf(other) is not null || guilds.InvitedTo(other) != 0) continue;
                if (_offeredAt.TryGetValue(other.Guid.Low, out long at) && pass.NowMs - at < cooldownMs) continue;
                if (best is null || recorder.Memory.EffectiveAffinity(relation) > recorder.Memory.EffectiveAffinity(best)
                    || (recorder.Memory.EffectiveAffinity(relation) == recorder.Memory.EffectiveAffinity(best) && relation.OtherGuid < best.OtherGuid))
                {
                    best = relation;
                    target = other;
                }
            }

            if (target is null) continue;
            _lastAffinityInvite[guild.Id] = pass.NowMs;
            PlayerbotSocialPackets.Send(inviter.Session, WorldOpcode.CmsgGuildInvite, PlayerbotSocialPackets.GuildInvite(target.Name));
            _offeredAt[target.Guid.Low] = pass.NowMs; // the target's week starts when it was asked, whether or not the server took the invitation
            if (guilds.InvitedTo(target) != guild.Id) continue;
            _offered[target.Guid.Low] = (guild.Id, pass.NowMs);
        }

        if (_offeredAt.Count > 1024)
            foreach ((uint id, long at) in _offeredAt.ToArray())
                if (pass.NowMs - at >= cooldownMs) _offeredAt.Remove(id);
    }

    private static PlayerbotSocialBot? InviterOf(PlayerbotSocialPass pass, Guild guild)
    {
        if (pass.ByCharacter.TryGetValue(guild.LeaderId, out PlayerbotSocialBot? leader) && guild.HasRight(Guild.GuildMasterRank, GuildRights.Invite)) return leader;
        foreach (GuildMember member in guild.Members)
            if (member.Rank == Guild.OfficerRank && guild.HasRight(member.Rank, GuildRights.Invite)
                && pass.ByCharacter.TryGetValue(member.CharacterId, out PlayerbotSocialBot? officer))
                return officer;
        return null;
    }

    private PlayerbotSocialBot? CandidateFor(PlayerbotSocialPass pass, PlayerbotGuildFounding founding, Guild guild, PlayerbotSocialBot inviter)
    {
        var eligible = new List<PlayerbotSocialBot>();
        foreach (PlayerbotSocialBot bot in pass.Bots)
        {
            if (bot.Player.Team != inviter.Player.Team || pass.Social.Guilds.GetGuildOf(bot.Player) is not null || pass.Social.Guilds.InvitedTo(bot.Player) != 0) continue;
            uint id = bot.Player.Guid.Low;
            if (_pending.ContainsKey(id) || founding.IsReserved(id)) continue;
            if (_refused.TryGetValue((guild.Id, id), out long at) && pass.NowMs - at < 5 * 60_000L) continue;
            eligible.Add(bot);
        }

        return eligible.Count == 0 ? null : eligible[pass.Random.Next(eligible.Count)];
    }

    private void ExpirePending(PlayerbotSocialPass pass)
    {
        foreach ((uint id, (int guildId, long since)) in _pending.ToArray())
        {
            bool gone = pass.Social.Guilds.Get(guildId) is null;
            if (gone || pass.NowMs - since >= pass.Timings.PendingInviteTimeoutMs) _pending.Remove(id);
        }
    }

    /// <summary>
    /// A bot guild's master promotes one member a minute at most (<see cref="NextPromotion"/>) through CMSG_GUILD_PROMOTE, which the server
    /// checks as it checks a player's (the Promote right, nobody at or above the master's rank + 1).
    /// </summary>
    internal void Promote(PlayerbotSocialPass pass)
    {
        foreach (Guild guild in pass.Social.Guilds.All.ToArray())
        {
            if (!pass.Registered.Contains(guild.LeaderId) || !pass.ByCharacter.TryGetValue(guild.LeaderId, out PlayerbotSocialBot? master)) continue;
            if (_lastPromotion.TryGetValue(guild.Id, out long last) && pass.NowMs - last < pass.Timings.PromotionSpacingMs) continue;
            var facts = new List<PlayerbotGuildMemberFacts>(guild.MemberCount);
            foreach (GuildMember member in guild.Members)
            {
                string? name = pass.Social.Characters.Find(member.CharacterId)?.Name;
                if (name is null) continue;
                byte level = pass.World.FindOnlinePlayer(member.Guid)?.Level ?? member.Level;
                facts.Add(new PlayerbotGuildMemberFacts(member.CharacterId, name, member.Rank, level, member.CharacterId == guild.LeaderId));
            }

            if (NextPromotion(facts, guild.Ranks.Count) is not { } target) continue;
            _lastPromotion[guild.Id] = pass.NowMs;
            bool sent = Send(master, WorldOpcode.CmsgGuildPromote, PlayerbotSocialPackets.GuildPromote(target.Name));
            byte? postRank = guild.Members.FirstOrDefault(m => m.CharacterId == target.CharacterId)?.Rank;
            if ((!sent || postRank is null || postRank.Value >= target.Rank) && !master.Session.IsInChaosProbe)
            {
                PlayerbotAnomalies.Report(master.Player, "social", "guild-action-failed",
                    $"guild promotion of {target.Name} in '{guild.Name}' failed (before={target.Rank}, after={postRank?.ToString() ?? "missing"}, sent={sent})");
            }
        }
    }

    /// <summary>Forget what is kept for guilds that no longer exist.</summary>
    internal void Prune(PlayerbotSocialPass pass)
    {
        foreach (int id in _lastRecruit.Keys.ToArray())
            if (pass.Social.Guilds.Get(id) is null) _lastRecruit.Remove(id);
        foreach (int id in _lastPromotion.Keys.ToArray())
            if (pass.Social.Guilds.Get(id) is null) _lastPromotion.Remove(id);
        foreach (Guid id in _seenInvites.Keys.ToArray())
            if (!pass.BotIds.Contains(id)) _seenInvites.Remove(id);
    }

    private static bool Send(PlayerbotSocialBot bot, WorldOpcode opcode, byte[]? payload = null)
        => PlayerbotSocialPackets.Send(bot.Session, opcode, payload ?? []);
}
