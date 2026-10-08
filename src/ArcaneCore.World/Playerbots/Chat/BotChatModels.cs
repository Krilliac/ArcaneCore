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
    IReadOnlyList<string> RecentEvents);

/// <summary>One line a player said to a bot, with everything a provider needs to answer it.</summary>
internal sealed record BotChatAsk(
    Guid BotId,
    ObjectGuid BotGuid,
    BotChatPersona Persona,
    ObjectGuid SenderGuid,
    string SenderName,
    ChatType Channel,
    string Text,
    bool FromMaster,
    bool InviteAllowed);

/// <summary>A reply on its way back to the world thread: said by the bot on <see cref="Channel"/>, and the master's order, if any.</summary>
internal sealed record BotChatReply(
    Guid BotId,
    ObjectGuid SenderGuid,
    string SenderName,
    ChatType Channel,
    string? Text,
    PlayerbotPartyCommand? Command,
    bool AcknowledgeCommand,
    string Provider);

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

/// <summary>
/// A model provider's transport (the Anthropic Messages API or an OpenAI-compatible chat completions endpoint). Called only from the
/// chat worker, never on the world thread. <paramref name="apiKey"/> is null for a keyless endpoint; implementations never log it.
/// </summary>
public interface IBotChatClient
{
    Task<BotChatResult> CompleteAsync(PlayerbotChatProviderOptions provider, string? apiKey, BotChatPrompt prompt, CancellationToken cancellationToken);
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
