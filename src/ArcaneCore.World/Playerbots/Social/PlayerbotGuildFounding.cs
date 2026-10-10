using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Game.Npc;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Chat;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>
/// Bots founding guilds (docs/areas/playbots-social.md), only through the packets a client sends and only while
/// <see cref="PlayerbotSocialGuildOptions.Enabled"/> is on, fewer guilds than <see cref="PlayerbotSocialGuildOptions.MaxBotGuilds"/> are
/// led by registered bots and the registry of bots has been read once (fail closed). At most one charter founding is in flight and attempts
/// are at least <see cref="PlayerbotSocialTimings.FoundingSpacingMs"/> apart. The founder and the members of one founding are unguilded,
/// running, non-scripted bots of one team.
/// <list type="bullet">
/// <item><b>ClientCreate</b> (vmangos GuildHandler.cpp:47-73): the founder sends CMSG_GUILD_CREATE with a screened name
/// (<see cref="PlayerbotGuildNames"/>); the server answers synchronously, so success is the founder being guilded. Needs two bots; the others
/// are invited afterwards (<see cref="PlayerbotGuildMembership.Recruit"/>).</item>
/// <item><b>Charter</b> (vmangos PetitionsHandler.cpp): the founder buys a charter (CMSG_PETITION_BUY, 1000 copper) only where a petitioner
/// that is also a tabard designer is already within its interaction range (the bot is never moved there: travel belongs to the brain's
/// goals); offers it (CMSG_OFFER_PETITION) to the bots reserved as signers, each of which signs (CMSG_PETITION_SIGN, no NPC or range
/// needed, PetitionsHandler.cpp:218-398) when it reads the signature list; and, when <see cref="PetitionManager.IsComplete"/> says so, turns it in
/// (CMSG_TURN_IN_PETITION). The state is read back from <see cref="PetitionManager.GetByOwner"/> at every pass, so a founding survives a
/// restart; one that stalls for <see cref="PlayerbotSocialTimings.CharterStallMs"/> releases its reservations.</item>
/// </list>
/// A new guild's master then sets the message of the day from a template, screened like every other line (CMSG_GUILD_MOTD).
/// </summary>
internal sealed class PlayerbotGuildFounding(ILogger logger)
{
    private sealed class Charter(uint founder, long startedMs)
    {
        public uint Founder { get; } = founder;

        public long StartedMs { get; } = startedMs;

        public HashSet<uint> Signers { get; } = [];

        public Dictionary<uint, long> OfferedAt { get; } = [];
    }

    private Charter? _charter;
    private bool _attempted;
    private long _lastAttemptMs;
    private readonly HashSet<int> _motd = [];

    /// <summary>Whether a charter founding is in flight.</summary>
    internal bool HasCharterInFlight => _charter is not null;

    /// <summary>Whether <paramref name="characterId"/> is the founder or a reserved signer of the charter in flight (recruiting leaves it alone).</summary>
    internal bool IsReserved(uint characterId) => _charter is { } charter && (charter.Founder == characterId || charter.Signers.Contains(characterId));

    /// <summary>The guilds waiting for their message of the day.</summary>
    internal int MotdPending => _motd.Count;

    /// <summary>One pass: carry the charter in flight forward, or start a founding when everything allows it.</summary>
    internal void Run(PlayerbotSocialPass pass, PlayerbotGuildMembership membership)
    {
        PlayerbotSocialGuildOptions options = pass.Options.Guilds;
        if (_charter is not null)
        {
            if (!options.Enabled) Release("guilds switched off");
            else ProgressCharter(pass);
        }

        if (!options.Enabled || !pass.RegistryLoaded || _charter is not null) return;
        if (_attempted && pass.NowMs - _lastAttemptMs < pass.Timings.FoundingSpacingMs) return;
        if (PlayerbotGuildMembership.CountBotGuilds(pass.Social.Guilds.All, pass.Registered) >= options.MaxBotGuilds) return;
        List<PlayerbotSocialBot> candidates = Candidates(pass, membership);
        if (PlayerbotGuildMembership.ResolveCreation(options.Creation, pass.Social.Guilds.Options) == PlayerbotGuildCreation.ClientCreate) TryClientCreate(pass, candidates);
        else TryCharter(pass, candidates);
    }

    private static List<PlayerbotSocialBot> Candidates(PlayerbotSocialPass pass, PlayerbotGuildMembership membership)
    {
        var candidates = new List<PlayerbotSocialBot>();
        foreach (PlayerbotSocialBot bot in pass.Bots)
        {
            if (pass.Social.Guilds.GetGuildOf(bot.Player) is not null || pass.Social.Guilds.InvitedTo(bot.Player) != 0) continue;
            if (membership.IsPending(bot.Player.Guid.Low) || !bot.Player.IsAlive) continue;
            candidates.Add(bot);
        }

        return candidates;
    }

    private static List<PlayerbotSocialBot> OfTeam(List<PlayerbotSocialBot> candidates, Team team)
        => [.. candidates.Where(bot => bot.Player.Team == team)];

    // --- client create ------------------------------------------------------------------------------------------------

    private void TryClientCreate(PlayerbotSocialPass pass, List<PlayerbotSocialBot> candidates)
    {
        List<Team> teams = [.. new[] { Team.Alliance, Team.Horde }.Where(team => OfTeam(candidates, team).Count >= 2)];
        if (teams.Count == 0) return;
        List<PlayerbotSocialBot> pool = OfTeam(candidates, teams[pass.Random.Next(teams.Count)]);
        PlayerbotSocialBot founder = pool[pass.Random.Next(pool.Count)];
        _attempted = true;
        _lastAttemptMs = pass.NowMs;
        if (PlayerbotGuildNames.TryGenerate(pass.NameRules(), pass.Random) is not { } name)
        {
            logger.LogWarning("Playerbot guilds: no guild name passed the screening; no guild is founded this time");
            return;
        }

        PlayerbotSocialPackets.Send(founder.Session, WorldOpcode.CmsgGuildCreate, PlayerbotSocialPackets.GuildCreate(name));
        if (pass.Social.Guilds.GetGuildOf(founder.Player) is { } guild)
        {
            _motd.Add(guild.Id);
            logger.LogInformation("Playerbot {Bot} founded the guild {Guild}", founder.Player.Name, guild.Name);
        }
        else
        {
            logger.LogInformation("Playerbot {Bot} could not found the guild {Guild} (the server refused CMSG_GUILD_CREATE)", founder.Player.Name, name);
        }
    }

    // --- charter ------------------------------------------------------------------------------------------------------

    private void TryCharter(PlayerbotSocialPass pass, List<PlayerbotSocialBot> candidates)
    {
        PetitionManager petitions = pass.Social.Petitions;
        int signs = pass.Social.Guilds.Options.EffectiveMinPetitionSigns;
        int needed = PlayerbotGuildMembership.CharterCandidatesNeeded(pass.Social.Guilds.Options);
        foreach (Team team in new[] { Team.Alliance, Team.Horde })
        {
            List<PlayerbotSocialBot> pool = OfTeam(candidates, team);
            if (pool.Count < needed) continue;
            // A founder that already owns a charter takes it up again (a restart); otherwise one that can buy where it stands.
            PlayerbotSocialBot? founder = pool.FirstOrDefault(bot => petitions.GetByOwner(bot.Player.Guid.Low) is not null)
                ?? pool.Where(bot => bot.Player.Money >= PetitionConstants.CharterCost && FindPetitioner(pass, bot) is not null)
                    .OrderBy(_ => pass.Random.Next()).FirstOrDefault();
            if (founder is null) continue;
            Petition? petition = petitions.GetByOwner(founder.Player.Guid.Low);
            if (petition is null)
            {
                _attempted = true;
                _lastAttemptMs = pass.NowMs;
                if (FindPetitioner(pass, founder) is not { } npc) continue;
                if (PlayerbotGuildNames.TryGenerate(pass.NameRules(), pass.Random) is not { } name)
                {
                    logger.LogWarning("Playerbot guilds: no guild name passed the screening; no charter is bought this time");
                    return;
                }

                PlayerbotSocialPackets.Send(founder.Session, WorldOpcode.CmsgPetitionBuy, PlayerbotSocialPackets.PetitionBuy(npc.Guid, name));
                petition = petitions.GetByOwner(founder.Player.Guid.Low);
                if (petition is null)
                {
                    logger.LogInformation("Playerbot {Bot} could not buy a charter for {Guild}", founder.Player.Name, name);
                    return;
                }
            }
            else
            {
                _attempted = true;
                _lastAttemptMs = pass.NowMs;
            }

            var charter = new Charter(founder.Player.Guid.Low, pass.NowMs);
            foreach (PetitionSignature signature in petition.Signatures)
                if (pool.Any(bot => bot.Player.Guid.Low == signature.PlayerId)) charter.Signers.Add(signature.PlayerId);
            foreach (PlayerbotSocialBot bot in pool.OrderBy(_ => pass.Random.Next()))
            {
                if (charter.Signers.Count >= signs) break;
                if (bot != founder) charter.Signers.Add(bot.Player.Guid.Low);
            }

            _charter = charter;
            logger.LogInformation("Playerbot {Bot} is founding the guild {Guild} by charter ({Signers} signatures wanted)", founder.Player.Name, petition.Name, signs);
            return;
        }
    }

    /// <summary>The petitioner (and tabard designer) NPC the bot can use from where it stands, or null: the server's own interaction check (QuestNpcServices.InteractableNpc).</summary>
    private static NpcInfo? FindPetitioner(PlayerbotSocialPass pass, PlayerbotSocialBot bot)
    {
        if (pass.Quests is not { } quests || bot.Player.Map is not { } map) return null;
        NpcInfo? nearest = null;
        float best = float.MaxValue;
        foreach (ObjectGuid guid in bot.Player.VisibleObjects)
        {
            if (map.FindObject(guid) is not Creature creature) continue;
            uint both = (uint)(NpcFlags.Petitioner | NpcFlags.TabardDesigner);
            if ((creature.NpcFlags & both) != both) continue;
            if (quests.InteractableNpc(bot.Player, creature.Guid, NpcFlags.Petitioner) is not { } npc) continue;
            float distance = MathF.Sqrt(MathF.Pow(creature.X - bot.Player.X, 2) + MathF.Pow(creature.Y - bot.Player.Y, 2) + MathF.Pow(creature.Z - bot.Player.Z, 2));
            if (distance < best)
            {
                best = distance;
                nearest = npc;
            }
        }

        return nearest;
    }

    private void ProgressCharter(PlayerbotSocialPass pass)
    {
        Charter charter = _charter!;
        PetitionManager petitions = pass.Social.Petitions;
        if (!pass.ByCharacter.TryGetValue(charter.Founder, out PlayerbotSocialBot? founder)) { Release("the founder is gone"); return; }
        if (pass.Social.Guilds.GetGuildOf(founder.Player) is not null) { Release("the founder joined a guild"); return; }
        if (petitions.GetByOwner(charter.Founder) is not { } petition) { Release("the charter is gone"); return; }
        if (pass.NowMs - charter.StartedMs >= pass.Timings.CharterStallMs) { Release("the charter stalled"); return; }

        if (petitions.IsComplete(petition))
        {
            PlayerbotSocialPackets.Send(founder.Session, WorldOpcode.CmsgTurnInPetition, PlayerbotSocialPackets.TurnIn(petition.CharterGuid));
            if (pass.Social.Guilds.GetGuildOf(founder.Player) is { } guild)
            {
                _motd.Add(guild.Id);
                logger.LogInformation("Playerbot {Bot} founded the guild {Guild} by charter", founder.Player.Name, guild.Name);
                _charter = null;
            }
            else
            {
                Release("the turn-in was refused");
            }

            return;
        }

        foreach (uint signer in charter.Signers.ToArray())
        {
            if (petition.ForPlayer(signer) is not null) continue;
            if (!pass.ByCharacter.TryGetValue(signer, out PlayerbotSocialBot? bot) || pass.Social.Guilds.GetGuildOf(bot.Player) is not null
                || pass.Social.Guilds.InvitedTo(bot.Player) != 0)
            {
                charter.Signers.Remove(signer);
                continue;
            }

            if (charter.OfferedAt.TryGetValue(signer, out long offered) && pass.NowMs - offered < pass.Timings.OfferSpacingMs) continue;
            charter.OfferedAt[signer] = pass.NowMs;
            PlayerbotSocialPackets.Send(founder.Session, WorldOpcode.CmsgOfferPetition, PlayerbotSocialPackets.PetitionOffer(petition.CharterGuid, bot.Player.Guid));
        }

        // A signer lost on the way is replaced by another free bot of the team.
        int signs = pass.Social.Guilds.Options.EffectiveMinPetitionSigns;
        if (charter.Signers.Count >= signs) return;
        foreach (PlayerbotSocialBot bot in pass.Bots.OrderBy(_ => pass.Random.Next()))
        {
            if (charter.Signers.Count >= signs) break;
            uint id = bot.Player.Guid.Low;
            if (id == charter.Founder || charter.Signers.Contains(id) || bot.Player.Team != founder.Player.Team) continue;
            if (pass.Social.Guilds.GetGuildOf(bot.Player) is not null || pass.Social.Guilds.InvitedTo(bot.Player) != 0) continue;
            charter.Signers.Add(id);
        }
    }

    private void Release(string reason)
    {
        if (_charter is null) return;
        logger.LogInformation("Playerbot guild charter released: {Reason}", reason);
        _charter = null;
    }

    /// <summary>
    /// A bot read the signature list of a charter offered to it (SMSG_PETITION_SHOW_SIGNATURES): it signs when the charter belongs to the
    /// founding in flight and the bot is one of its signers, or when its owner is a real player the invite policy accepts. Any other
    /// charter (another bot's, an unknown one, a finished one) is left unsigned.
    /// </summary>
    internal void OnShowSignatures(PlayerbotSocialPass pass, PlayerbotGuildMembership membership, PlayerbotSocialBot bot, ObjectGuid item, ObjectGuid owner, uint petitionId)
    {
        PetitionManager petitions = pass.Social.Petitions;
        if (petitions.Get((int)petitionId) is not { } petition || petition.CharterGuid != item || petition.OwnerGuid != owner || petitions.IsComplete(petition)) return;
        if (pass.Social.Guilds.GetGuildOf(bot.Player) is not null || petition.ForPlayer(bot.Player.Guid.Low) is not null) return;
        bool ours = _charter is { } charter && charter.Founder == petition.OwnerId && charter.Signers.Contains(bot.Player.Guid.Low);
        bool sign = ours;
        if (!ours && pass.World.FindOnlinePlayer(owner) is { Session: WorldSession { IsManaged: false } } realOwner)
            sign = membership.AcceptsCharterOf(pass, bot, realOwner);
        if (sign) PlayerbotSocialPackets.Send(bot.Session, WorldOpcode.CmsgPetitionSign, PlayerbotSocialPackets.PetitionSign(item));
    }

    // --- message of the day -------------------------------------------------------------------------------------------

    /// <summary>The master of each new guild sets the message of the day once, from the <c>guild.motd</c> templates (screened, at most 128 characters).</summary>
    internal void SetMotds(PlayerbotSocialPass pass)
    {
        if (_motd.Count > 64) _motd.Clear();
        foreach (int id in _motd.ToArray())
        {
            if (pass.Social.Guilds.Get(id) is not { } guild) { _motd.Remove(id); continue; }
            if (!pass.ByCharacter.TryGetValue(guild.LeaderId, out PlayerbotSocialBot? master)) continue; // later, when the master runs
            _motd.Remove(id);
            if (MotdFor(pass, guild) is { } text)
                PlayerbotSocialPackets.Send(master.Session, WorldOpcode.CmsgGuildMotd, PlayerbotSocialPackets.GuildMotd(text));
        }
    }

    private static string? MotdFor(PlayerbotSocialPass pass, Guild guild)
    {
        string[] lines = [.. PlayerbotSocialTemplates.Lines["guild.motd"].OrderBy(_ => pass.Random.Next())];
        PlayerbotChatSafetyFilter safety = pass.Chat?.Safety.Filter() ?? PlayerbotChatSafetyFilter.Default;
        foreach (string line in lines)
        {
            string? text = PlayerbotChatPrompts.Sanitize(line.Replace("{guild}", guild.Name, StringComparison.Ordinal));
            if (text is null || CharterNameRules.CodePointCount(text) > Guild.MaxMotdLength) continue;
            SafetyVerdict verdict = safety.Screen(text, PlayerbotChatSafetyCategories.All);
            if (verdict.Flagged || verdict.TimedOut) continue;
            if (pass.WordFilter is { } filter && !string.Equals(filter.Apply(text), text, StringComparison.Ordinal)) continue;
            return text;
        }

        return null;
    }
}
