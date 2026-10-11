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
/// <c>World:Playerbots:Social</c>: bot guilds and social life (<see cref="PlayerbotSocialFeature"/>, docs/areas/playbots-social.md).
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

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Social";
        if (Guilds is null) throw new InvalidOperationException($"{section}:Guilds is missing.");
        if (GuildChat is null) throw new InvalidOperationException($"{section}:GuildChat is missing.");
        if (Emotes is null) throw new InvalidOperationException($"{section}:Emotes is missing.");
        Guilds.Validate();
        GuildChat.Validate();
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

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Social:Guilds";
        if (MaxBotGuilds is < 0 or > MaxBotGuildsCeiling) throw new InvalidOperationException($"{section}: MaxBotGuilds must be 0..{MaxBotGuildsCeiling}.");
        if (TargetSize is < MinTargetSize or > MaxTargetSize)
            throw new InvalidOperationException($"{section}: TargetSize must be {MinTargetSize}..{MaxTargetSize}.");
        if (!Enum.IsDefined(InvitePolicy)) throw new InvalidOperationException($"{section}: InvitePolicy must be None, GuildOrFriends or Anyone.");
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
    /// bot and player in 30 minutes, once per player in 10 minutes, once per bot in 5 minutes and twelve times a minute in all (off by default).
    /// A player who ignores the bot is never greeted.
    /// </summary>
    public bool Enabled { get; set; }
}
