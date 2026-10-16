using ArcaneCore.Game;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots.Party;

namespace ArcaneCore.World.Playerbots.Chat;

/// <summary>
/// What a bot is and does, captured on the world thread when a player talks to it (the background worker never touches the world).
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
    string? DebugSummary = null);

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
    bool Addressed = true);

/// <summary>
/// A reply on its way back to the world thread: said by the bot on <see cref="Channel"/>, and the master's order, if any.
/// <see cref="Guild"/> is the guild a guild-chat announcement is for (null otherwise): it is not said in another one.
/// <see cref="Speech"/> is the envelope the line is said with (<see cref="BotChatAsk.Speech"/>, or the line of <see cref="PlayerbotChat.TrySpeak"/>);
/// null for the old replies, which go back on <see cref="Channel"/>.
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
    BotSpeech? Speech = null);

/// <summary>One earlier line of a conversation: the player's (<see cref="FromPlayer"/>) or the bot's.</summary>
public sealed record BotChatTurn(bool FromPlayer, string Text);

/// <summary>
/// A model request: the system prompt in two parts (the static rules, identical for every bot and request, sent first so a provider
/// can cache it, then the bot's own facts), and the conversation ending with the player's line.
/// </summary>
public sealed record BotChatPrompt(string StaticSystem, string DynamicSystem, IReadOnlyList<BotChatTurn> Messages);

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
/// chat worker, never on the world thread. <paramref name="apiKey"/> is null for a keyless endpoint; implementations never log it.
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
