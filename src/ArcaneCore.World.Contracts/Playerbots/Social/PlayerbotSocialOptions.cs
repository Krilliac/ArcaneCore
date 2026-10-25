namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>How a bot founds a guild (<see cref="PlayerbotSocialGuildOptions.Creation"/>).</summary>
public enum PlayerbotGuildCreation
{
    /// <summary><see cref="ClientCreate"/> when <c>World:Guild:AllowClientGuildCreate</c> is on, otherwise <see cref="Charter"/>.</summary>
    Auto,

    /// <summary>A guild charter: bought at a petitioner, signed by other bots, turned in (CMSG_PETITION_BUY, _SIGN, CMSG_TURN_IN_PETITION).</summary>
    Charter,

    /// <summary>CMSG_GUILD_CREATE (vmangos HandleGuildCreateOpcode honours it); needs <c>World:Guild:AllowClientGuildCreate</c>.</summary>
    ClientCreate,
}

/// <summary>
/// <c>World:Playerbots:Social</c>: bot guilds and social life (<c>PlayerbotSocialFeature</c>, docs/areas/playbots-social.md).
/// Managed bots found guilds through the ordinary guild and charter packets, invite each other, answer real players' invitations by the
/// party invite policy, say a short line in guild chat now and then through the bot chat service (its providers and safety
/// screening), and, when asked to, wave at or greet real players nearby. Every key is live: <c>.reload config</c> changes this
/// object in place and the feature reads it at every pass.
/// </summary>
public sealed class PlayerbotSocialOptions
{
    /// <summary>Guild founding, membership and ranks (<c>World:Playerbots:Social:Guilds</c>).</summary>
    public PlayerbotSocialGuildOptions Guilds { get; set; } = new();

    /// <summary>Guild chat (<c>World:Playerbots:Social:GuildChat</c>).</summary>
    public PlayerbotSocialGuildChatOptions GuildChat { get; set; } = new();

    /// <summary>Waves and greetings for nearby real players (<c>World:Playerbots:Social:Emotes</c>).</summary>
    public PlayerbotSocialEmoteOptions Emotes { get; set; } = new();

    /// <summary>Each managed bot's persisted memory of the characters it has dealt with (<c>World:Playerbots:Social:Memory</c>).</summary>
    public PlayerbotSocialMemoryOptions Memory { get; set; } = new();

    /// <summary>The War Mode server mode, memory side (<c>World:Playerbots:Social:WarMode</c>; off by default).</summary>
    public PlayerbotWarModeOptions WarMode { get; set; } = new();

    /// <summary>Friend and ignore lists driven by affinity (<c>World:Playerbots:Social:Lists</c>).</summary>
    public PlayerbotSocialListsOptions Lists { get; set; } = new();

    /// <summary>Answering calls for help and lending a hand to strangers (<c>World:Playerbots:Social:Helping</c>).</summary>
    public PlayerbotSocialHelpingOptions Helping { get; set; } = new();

    /// <summary>Thanking whoever helped a bot: an emote, a line, a favour, a tip (<c>World:Playerbots:Social:Gratitude</c>).</summary>
    public PlayerbotSocialGratitudeOptions Gratitude { get; set; } = new();

    /// <summary>The short-term mood of each bot (<c>World:Playerbots:Social:Mood</c>).</summary>
    public PlayerbotSocialMoodOptions Mood { get; set; } = new();

    /// <summary>Bots starting conversations and commenting on their surroundings (<c>World:Playerbots:Social:Initiative</c>).</summary>
    public PlayerbotSocialInitiativeOptions Initiative { get; set; } = new();

    /// <summary>Cross-faction greetings, rude emotes at rivals and stance-aware replies to emotes (<c>World:Playerbots:Social:Rivalry</c>).</summary>
    public PlayerbotSocialRivalryOptions Rivalry { get; set; } = new();

    /// <summary>Bots and the world's events: festival gathering, celebration and talk (<c>World:Playerbots:Social:Events</c>).</summary>
    public PlayerbotSocialEventsOptions Events { get; set; } = new();

    /// <summary>
    /// The most milliseconds of one world tick the social pass may use (0..1000, default 4; 0 = no limit). The pass is spread over as many
    /// ticks as that takes, so a large fleet no longer stalls one tick once a second; a pass that takes longer than its interval simply
    /// carries on (the next one starts when it has finished). The per-bot lanes stop between bots once the budget is spent (a tick may
    /// overshoot by one bot); the lanes that work on the whole population run whole.
    /// </summary>
    public double PassBudgetMs { get; set; } = 4.0;

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Social";
        if (!double.IsFinite(PassBudgetMs) || PassBudgetMs is < 0 or > 1000) throw new InvalidOperationException($"{section}: PassBudgetMs must be 0..1000.");
        if (Guilds is null) throw new InvalidOperationException($"{section}:Guilds is missing.");
        if (GuildChat is null) throw new InvalidOperationException($"{section}:GuildChat is missing.");
        if (Emotes is null) throw new InvalidOperationException($"{section}:Emotes is missing.");
        if (Memory is null) throw new InvalidOperationException($"{section}:Memory is missing.");
        if (WarMode is null) throw new InvalidOperationException($"{section}:WarMode is missing.");
        if (Lists is null) throw new InvalidOperationException($"{section}:Lists is missing.");
        if (Helping is null) throw new InvalidOperationException($"{section}:Helping is missing.");
        if (Gratitude is null) throw new InvalidOperationException($"{section}:Gratitude is missing.");
        Helping.Validate();
        Gratitude.Validate();
        if (Mood is null) throw new InvalidOperationException($"{section}:Mood is missing.");
        Mood.Validate();
        if (Initiative is null) throw new InvalidOperationException($"{section}:Initiative is missing.");
        Initiative.Validate();
        if (Rivalry is null) throw new InvalidOperationException($"{section}:Rivalry is missing.");
        Rivalry.Validate();
        if (Events is null) throw new InvalidOperationException($"{section}:Events is missing.");
        Events.Validate();
        Lists.Validate();
        Guilds.Validate();
        GuildChat.Validate();
        Memory.Validate();
        WarMode.Validate();
    }
}

/// <summary><c>World:Playerbots:Social:Guilds</c>: which guilds bots found, how big they grow and whom they accept.</summary>
public sealed class PlayerbotSocialGuildOptions
{
    /// <summary>The most bot guilds a world may hold.</summary>
    public const int MaxBotGuildsCeiling = 64;

    /// <summary>The smallest <see cref="TargetSize"/>.</summary>
    public const int MinTargetSize = 2;

    /// <summary>The largest <see cref="TargetSize"/>.</summary>
    public const int MaxTargetSize = 500;

    /// <summary>
    /// Let bots found guilds and invite other bots (on by default). Off: no new guild forms and no bot is invited; a bot still answers
    /// the invitations it receives (by <see cref="InvitePolicy"/>) and the guilds that exist go on.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The most guilds led by a registered bot at once (0..64; 0 founds none). Lowering it disbands nothing: it only stops new foundings.
    /// </summary>
    public int MaxBotGuilds { get; set; } = 4;

    /// <summary>The most members of a bot guild, pending invitations counted (2..500).</summary>
    public int TargetSize { get; set; } = 25;

    /// <summary>
    /// Who may invite a bot into a guild, and whose charter a bot signs (<see cref="PlayerbotInvitePolicy.GuildOrFriends"/> by default):
    /// the same rules as <c>World:Playerbots:Party:InvitePolicy</c>, and the names in <c>World:Playerbots:Party:Allowlist</c> are always accepted.
    /// Another bot's invitation into a guild the founding bots are building is always accepted.
    /// </summary>
    public PlayerbotInvitePolicy InvitePolicy { get; set; } = PlayerbotInvitePolicy.GuildOrFriends;

    /// <summary>How a guild is founded (<see cref="PlayerbotGuildCreation.Auto"/> by default).</summary>
    public PlayerbotGuildCreation Creation { get; set; } = PlayerbotGuildCreation.Auto;

    /// <summary>
    /// Let a guild's master or officer invite a guildless, same-team character it knows and likes (<see cref="GuildInviteAffinity"/>,
    /// <see cref="GuildInviteMinFamiliarity"/>; at most one invitation per character per <see cref="GuildInviteCooldownDays"/>), and let a
    /// guildless bot accept the invitation of a character it likes (<see cref="GuildAcceptAffinity"/>) (on by default).
    /// </summary>
    public bool AffinityInvites { get; set; } = true;

    /// <summary>The affinity an officer needs toward a guildless character before it invites it (-100..100).</summary>
    public int GuildInviteAffinity { get; set; } = 60;

    /// <summary>The familiarity an officer needs with a guildless character before it invites it (0..1000).</summary>
    public int GuildInviteMinFamiliarity { get; set; } = 8;

    /// <summary>Days before the same character is invited again (0..365).</summary>
    public int GuildInviteCooldownDays { get; set; } = 7;

    /// <summary>The affinity toward the inviter at which a guildless bot accepts a guild invitation; it declines below it (-100..100).</summary>
    public int GuildAcceptAffinity { get; set; } = 30;

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Social:Guilds";
        if (GuildInviteAffinity is < -100 or > 100) throw new InvalidOperationException($"{section}: GuildInviteAffinity must be -100..100.");
        if (GuildInviteMinFamiliarity is < 0 or > 1000) throw new InvalidOperationException($"{section}: GuildInviteMinFamiliarity must be 0..1000.");
        if (GuildInviteCooldownDays is < 0 or > 365) throw new InvalidOperationException($"{section}: GuildInviteCooldownDays must be 0..365.");
        if (GuildAcceptAffinity is < -100 or > 100) throw new InvalidOperationException($"{section}: GuildAcceptAffinity must be -100..100.");
        if (MaxBotGuilds is < 0 or > MaxBotGuildsCeiling) throw new InvalidOperationException($"{section}: MaxBotGuilds must be 0..{MaxBotGuildsCeiling}.");
        if (TargetSize is < MinTargetSize or > MaxTargetSize)
            throw new InvalidOperationException($"{section}: TargetSize must be {MinTargetSize}..{MaxTargetSize}.");
        if (!Enum.IsDefined(InvitePolicy)) throw new InvalidOperationException($"{section}: InvitePolicy must be None, GuildOrFriends, Anyone or Affinity.");
        if (!Enum.IsDefined(Creation)) throw new InvalidOperationException($"{section}: Creation must be Auto, Charter or ClientCreate.");
    }
}

/// <summary><c>World:Playerbots:Social:GuildChat</c>: what bots say in their guild's chat.</summary>
public sealed class PlayerbotSocialGuildChatOptions
{
    /// <summary>The most guild lines per guild and hour.</summary>
    public const int MaxMessagesPerHour = 60;

    /// <summary>
    /// Let bots say a short line in guild chat (on by default): a welcome, congratulations on a level, a note on a boss or a battleground
    /// won, and some small talk. The lines go through the bot chat service (<c>World:Playerbots:Chat</c>: its providers, spend caps and safety
    /// screening), so nothing is said while <c>Chat:Enabled</c> is off; and only while a real player who can read guild chat is online.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The most lines the bots of one guild say per hour (0..60; 0 keeps them silent).</summary>
    public int MessagesPerHour { get; set; } = 4;

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Social:GuildChat";
        if (MessagesPerHour is < 0 or > MaxMessagesPerHour) throw new InvalidOperationException($"{section}: MessagesPerHour must be 0..{MaxMessagesPerHour}.");
    }
}

/// <summary><c>World:Playerbots:Social:Emotes</c>: waves and greetings.</summary>
public sealed class PlayerbotSocialEmoteOptions
{
    /// <summary>
    /// Let an idle bot wave at (CMSG_TEXT_EMOTE) or greet with /say a real player of its faction standing within 8 yards, at most once per
    /// bot and player in 30 minutes, once per player in 10 minutes, once per bot in 5 minutes and twelve times a minute in all (on by default).
    /// A player who ignores the bot is never greeted.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
