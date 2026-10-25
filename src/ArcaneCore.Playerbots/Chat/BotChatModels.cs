using ArcaneCore.Game;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots.Party;

namespace ArcaneCore.World.Playerbots.Chat;

/// <summary>
/// What a bot is and does, captured on the world thread when a player talks to it (the background worker never touches the world).
/// <see cref="Facts"/> holds the rest of its real state (health, gear, bags, needs, objective, role, guild, what is near); null where a
/// caller has none (tests, a minimal host).
/// </summary>
internal sealed record BotChatPersona(
    string Name,
    Race Race,
    Class Class,
    Gender Gender,
    byte Level,
    string? Zone,
    string? Subzone,
    PlayerbotGoalKind? Goal,
    string? QuestTitle,
    string? MasterName,
    bool InGroup,
    IReadOnlyList<string> RecentEvents,
    string? DebugSummary = null,
    BotChatFacts? Facts = null,
    BotChatRelation? Relation = null,
    float Mood = 0f);

/// <summary>How a bot feels about the person it is talking to (<see cref="BotChatRelation"/>.Affinity).</summary>
internal enum BotChatAffinityBand : byte
{
    Dislike,
    Neutral,
    Like,
    Close,
}

/// <summary>
/// What a bot remembers of the player it is talking to, as bands and bot-side text only (never the player's own words): how often they met,
/// how the bot feels about them, the role they like to play, the topic key of the last talk and the bot's own short summary of it, and the
/// bot's sociability and chattiness bands (0 low, 1 middle, 2 high; <see cref="BotSocial.PlayerbotTraits"/>).
/// </summary>
internal sealed record BotChatRelation(
    int Familiarity,
    int AffinityScore,
    BotChatAffinityBand Affinity,
    bool Rival,
    BotChatRole? PreferredRole,
    string? LastTopic,
    string? Summary,
    int SociabilityBand,
    int ChattinessBand);

/// <summary>A bot's role in a group, from its class and talent build.</summary>
internal enum BotChatRole : byte
{
    Damage,
    Tank,
    Healer,
}

/// <summary>Something a bot lacks, as it would say it in chat (<see cref="BotChatFacts.Needs"/>).</summary>
internal enum BotChatNeed : byte
{
    /// <summary>No food in its bags (a class that eats to rest).</summary>
    Food,

    /// <summary>No drink in its bags (a mana class).</summary>
    Water,

    /// <summary>Two or fewer free bag slots.</summary>
    BagSpace,

    /// <summary>Level 40 or more without riding.</summary>
    Mount,

    /// <summary>Health below a third (out of combat).</summary>
    Heal,

    /// <summary>Mana below a fifth (a mana class).</summary>
    Mana,

    /// <summary>Less money than a level's worth of training (a few silver a level).</summary>
    Gold,

    /// <summary>A group for an elite or dungeon quest (the bot's quest names one).</summary>
    Group,
}

/// <summary>One kind of item a bot carries more of than it needs (<see cref="BotChatFacts.Surplus"/>).</summary>
internal sealed record BotChatItem(uint Entry, string Name, int Count, uint Quality, uint SellPrice);

/// <summary>
/// The state behind a bot's words, read on the world thread (<see cref="PlayerbotPersonaFacts.Build"/>): nothing here is invented, every
/// name is an item, quest, zone, creature or player the server knows. Percentages are 0..100; <see cref="ManaPct"/> is null for a class
/// without mana.
/// </summary>
internal sealed record BotChatFacts
{
    public int HealthPct { get; init; } = 100;

    public int? ManaPct { get; init; }

    public bool Dead { get; init; }

    public bool InCombat { get; init; }

    /// <summary>The average item level of the equipped gear (0 when nothing is worn).</summary>
    public int AverageItemLevel { get; init; }

    /// <summary>The best equipped items (rare or better first, then the highest item level), at most three names.</summary>
    public IReadOnlyList<string> NotableItems { get; init; } = [];

    public int BagFree { get; init; }

    public int BagTotal { get; init; }

    /// <summary>Food items in the bags (what the bot could hand to a bot that has none).</summary>
    public int Food { get; init; }

    /// <summary>Drink items in the bags.</summary>
    public int Drink { get; init; }

    public uint Money { get; init; }

    /// <summary>What the bot lacks, most pressing first.</summary>
    public IReadOnlyList<BotChatNeed> Needs { get; init; } = [];

    /// <summary>Goods the bot carries spare (trade goods, cloth, food or drink beyond its own use, gear it cannot or does not wear).</summary>
    public IReadOnlyList<BotChatItem> Surplus { get; init; } = [];

    /// <summary>The current quest's open objective and progress ("Kobold Vermin slain 4/10"), or null.</summary>
    public string? Objective { get; init; }

    /// <summary>Whether every objective of the current quest is done (it only needs handing in).</summary>
    public bool QuestReady { get; init; }

    /// <summary>The bot's other quests in its log (titles, at most five), for "can you help with ...".</summary>
    public IReadOnlyList<string> Quests { get; init; } = [];

    public BotChatRole Role { get; init; }

    /// <summary>The talent build's name ("protection", "holy", "frost"), or null below level 10.</summary>
    public string? Spec { get; init; }

    public string? Guild { get; init; }

    /// <summary>Real players near the bot (names, at most three).</summary>
    public IReadOnlyList<string> NearbyPlayers { get; init; } = [];

    /// <summary>A notable creature near the bot or its target (an elite, a rare, a boss), or null.</summary>
    public string? NearbyCreature { get; init; }

    /// <summary>The dungeon, raid or battleground the bot is in, or null in the open world.</summary>
    public string? Instance { get; init; }

    public bool InBattleground { get; init; }

    /// <summary>The members of the bot's group (names, the bot excluded).</summary>
    public IReadOnlyList<string> GroupMembers { get; init; } = [];

    /// <summary>Whether the bot leads its group and has room in it (it may invite).</summary>
    public bool CanInvite { get; init; }

    /// <summary>A place a player asked about ("where is X"), resolved from the server's area data; null when not asked or not found.</summary>
    public BotChatPlace? Place { get; init; }

    /// <summary>The dungeon that suits the bot's level (for LFG talk), or null.</summary>
    public string? SuitedDungeon { get; init; }

    /// <summary>The player being answered stands within trade range of the bot on its map (set per speaker).</summary>
    public bool SpeakerNearby { get; init; }

    /// <summary>The player being answered is in the bot's group (set per speaker).</summary>
    public bool SpeakerInGroup { get; init; }
}

/// <summary>A place resolved from the area data: its name, the zone it lies in (null for a zone), and its continent or instance.</summary>
internal sealed record BotChatPlace(string Name, string? Zone, string? Map, bool SameZone);

/// <summary>
/// Something a reply asks the bot to do besides talk (a model's <c>intent</c>, or the built-in provider's reading of a line), carried
/// out on the world thread only through the bot's own safe paths: an order only for its party master, an invitation or a trade only
/// when the bot itself would (its invite policy, its group's room, trade range).
/// </summary>
internal enum BotChatAction : byte
{
    None,
    Follow,
    Stay,
    Attack,
    Stop,
    Come,

    /// <summary>Invite the speaker into the bot's group (the bot leads it and has room, or has no group; its invite policy allows the speaker).</summary>
    Invite,

    /// <summary>Open a trade window with the speaker (in trade range; a group member or someone the bot's invite policy allows).</summary>
    OpenTrade,

    /// <summary>Share the bot's quests with its party (the party master's request only).</summary>
    ShareQuest,

    /// <summary>Post an LFG line in the LookingForGroup channel (the bot is free and in the channel).</summary>
    JoinLfg,
}

/// <summary>
/// What a bot is asked to say on its own, with nobody having spoken to it (a guild announcement,
/// <see cref="PlayerbotChat.TryAnnounce"/>): <see cref="TemplateKey"/> picks the built-in lines, <see cref="Instruction"/> tells a
/// model what the line is about, <see cref="Values"/> fill the topic placeholders (<c>{guild}</c>, <c>{who}</c>, <c>{newlevel}</c>,
/// <c>{boss}</c>, <c>{instance}</c>, <c>{battleground}</c>) and <see cref="GuildName"/> names the guild. Nothing here is a GUID or an account.
/// </summary>
internal sealed record BotChatTopic(string TemplateKey, string Instruction, IReadOnlyDictionary<string, string> Values, string GuildName);

/// <summary>
/// One line a player said to a bot, with everything a provider needs to answer it. <see cref="SenderAccountId"/> is for the safety
/// records and the automatic mute only (0 when unknown); it never reaches a provider, and neither do the GUIDs. A <see cref="Topic"/>
/// makes it an announcement the bot starts itself: <see cref="Text"/> is then a plain sentence of what happened.
/// <para>
/// <see cref="Speech"/> is where and how the answer is said (the reply channel, the world channel's name, the target, the priority and,
/// for a bot speaker, the bot-to-bot turn); null keeps the old rule (the answer goes back on <see cref="Channel"/>, a guild announcement
/// in its guild). <see cref="SenderIsBot"/>: another managed bot spoke (the line is never flagged against it, and only small talk is
/// answered); <see cref="Addressed"/>: the line named the bot or was a whisper (false for a question put to a whole guild).
/// </para>
/// </summary>
internal sealed record BotChatAsk(
    Guid BotId,
    ObjectGuid BotGuid,
    BotChatPersona Persona,
    ObjectGuid SenderGuid,
    string SenderName,
    ChatType Channel,
    string Text,
    bool FromMaster,
    bool InviteAllowed,
    int SenderAccountId = 0,
    BotChatTopic? Topic = null,
    bool FromGm = false,
    BotSpeech? Speech = null,
    bool SenderIsBot = false,
    bool Addressed = true,
    bool TradeAllowed = false)
{
    /// <summary>
    /// An announcement a listener asked to keep away from models ("ai off"): the built-in line is said even while a model provider is
    /// configured and <c>Safety:RequireOptIn</c> is off. Honoured by <c>PlayerbotChat.AnnounceAsync</c>; every other path ignores it.
    /// </summary>
    public bool BuiltinOnly { get; init; }
}

/// <summary>
/// A reply on its way back to the world thread: said by the bot on <see cref="Channel"/>, and the master's order, if any.
/// <see cref="Guild"/> is the guild a guild-chat announcement is for (null otherwise): it is not said in another one.
/// <see cref="Speech"/> is the envelope the line is said with (<see cref="BotChatAsk.Speech"/>, or the line of <see cref="PlayerbotChat.TrySpeak"/>);
/// null for the old replies, which go back on <see cref="Channel"/>. <see cref="Action"/> is a social act the reply asks for
/// (<see cref="BotChatAction"/>: an invitation, a trade, a quest share, an LFG post), checked again and carried out on the world thread.
/// </summary>
internal sealed record BotChatReply(
    Guid BotId,
    ObjectGuid SenderGuid,
    string SenderName,
    ChatType Channel,
    string? Text,
    PlayerbotPartyCommand? Command,
    bool AcknowledgeCommand,
    string Provider,
    string? Guild = null,
    BotSpeech? Speech = null,
    BotChatAction Action = BotChatAction.None);

/// <summary>One earlier line of a conversation: the player's (<see cref="FromPlayer"/>) or the bot's.</summary>
public sealed record BotChatTurn(bool FromPlayer, string Text);

/// <summary>
/// A model request: the system prompt in two parts (the static rules, identical for every bot and request, sent first so a provider
/// can cache it, then the bot's own facts), and the conversation ending with the player's line.
/// </summary>
public sealed record BotChatPrompt(string StaticSystem, string DynamicSystem, IReadOnlyList<BotChatTurn> Messages)
{
    /// <summary>
    /// How urgent the answer is (the in-process model's scheduler serves a player before bot-to-bot talk before ambient chatter, and
    /// drops stale ambient lines). Never sent to a provider.
    /// </summary>
    public BotChatWorkload Workload { get; init; } = BotChatWorkload.Interactive;
}

/// <summary>Who is waiting on a model request, most urgent first.</summary>
public enum BotChatWorkload
{
    /// <summary>A real player addressed the bot.</summary>
    Interactive,

    /// <summary>Another bot spoke to it.</summary>
    BotToBot,

    /// <summary>Nobody in particular: guild chatter, announcements, a question put to a whole channel.</summary>
    Ambient,
}

/// <summary>How a model request ended.</summary>
public enum BotChatOutcome
{
    /// <summary>A reply (<see cref="BotChatResult.Text"/>).</summary>
    Ok,

    /// <summary>HTTP 429: the provider's rate limit.</summary>
    RateLimited,

    /// <summary>HTTP 5xx or 529: the provider is down or overloaded.</summary>
    Unavailable,

    /// <summary>HTTP 401/403: the key is wrong or lacks permission.</summary>
    Unauthorized,

    /// <summary>Another HTTP 4xx: the request was refused (a bad model id, a bad parameter).</summary>
    Rejected,

    /// <summary>The model declined to answer (a refusal stop reason or content filter).</summary>
    Refused,

    /// <summary>The request did not finish within the timeout.</summary>
    Timeout,

    /// <summary>The connection failed.</summary>
    NetworkError,

    /// <summary>The response could not be read or held no text.</summary>
    BadResponse,

    /// <summary>
    /// A thinking model returned no text: the content was empty while the tokens went to reasoning (or the reply was cut at the limit).
    /// Fix: <c>ReasoningEffort</c> = <c>none</c> on the provider, or a larger <c>MaxTokens</c>.
    /// </summary>
    ReasoningOnly,
}

/// <summary>A model request's result and the tokens it reports (0 when the provider reports none).</summary>
public sealed record BotChatResult(
    BotChatOutcome Outcome,
    string? Text = null,
    int InputTokens = 0,
    int OutputTokens = 0,
    int CacheWriteTokens = 0,
    int CacheReadTokens = 0,
    TimeSpan? RetryAfter = null)
{
    public static BotChatResult Fail(BotChatOutcome outcome, TimeSpan? retryAfter = null) => new(outcome, RetryAfter: retryAfter);
}

/// <summary>How a moderation step judged a line.</summary>
public enum BotChatModerationVerdict
{
    /// <summary>Nothing flagged: the line may go to the provider.</summary>
    Clean,

    /// <summary>Flagged (<see cref="BotChatModeration.Category"/>): the line goes to no provider.</summary>
    Flagged,

    /// <summary>The step failed (an error, a timeout, an unreadable answer): this provider is skipped.</summary>
    Failed,
}

/// <summary>A moderation step's verdict, the first flagged category (when the endpoint names one) and the tokens it used.</summary>
public sealed record BotChatModeration(BotChatModerationVerdict Verdict, string? Category = null, int InputTokens = 0, int OutputTokens = 0);

/// <summary>
/// A model provider's transport (the Anthropic Messages API or an OpenAI-compatible chat completions endpoint). Called only from the
/// chat worker, never on the world thread. <c>apiKey</c> is null for a keyless endpoint; implementations never log it.
/// </summary>
public interface IBotChatClient
{
    Task<BotChatResult> CompleteAsync(PlayerbotChatProviderOptions provider, string? apiKey, BotChatPrompt prompt, CancellationToken cancellationToken);

    /// <summary>
    /// The <see cref="PlayerbotChatModerationKind.Endpoint"/> step: an OpenAI-compatible <c>/moderations</c> request for
    /// <paramref name="text"/>. A transport without one fails the step (the provider is then skipped).
    /// </summary>
    Task<BotChatModeration> ModerateAsync(PlayerbotChatProviderOptions provider, string? apiKey, string text, CancellationToken cancellationToken)
        => Task.FromResult(new BotChatModeration(BotChatModerationVerdict.Failed));
}

/// <summary>Where provider keys come from (the process environment; a test seam).</summary>
public interface IBotChatEnvironment
{
    string? Get(string variable);
}

/// <summary>The process environment.</summary>
public sealed class ProcessBotChatEnvironment : IBotChatEnvironment
{
    public string? Get(string variable) => Environment.GetEnvironmentVariable(variable);
}
