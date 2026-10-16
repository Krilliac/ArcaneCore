namespace ArcaneCore.World.Playerbots.Chat;

/// <summary>
/// <c>World:Playerbots:Chat:InProcess</c>: the in-process chat model a provider entry of kind
/// <see cref="PlayerbotChatProviderKind.InProcess"/> answers with (docs/areas/playbots.md, In-process model). A GGUF file run on this
/// server's CPU by managed code (<see cref="Inference.InProcessBotChatClient"/>): no network, no key, no other process, no cost. No
/// model ships with the server; with no provider of that kind, nothing here is read. Read when the model loads (at startup): a change
/// takes a restart.
/// </summary>
public sealed class PlayerbotChatInProcessOptions
{
    /// <summary>The model file (GGUF, llama or qwen2 architecture, gpt2 BPE tokenizer; relative to the content root, or absolute), e.g. <c>models/chat/SmolLM2-360M-Instruct-Q8_0.gguf</c>.</summary>
    public string ModelPath { get; set; } = string.Empty;

    /// <summary>Threads one reply computes on (1..16; dedicated, BelowNormal priority, never the thread pool).</summary>
    public int Threads { get; set; } = 2;

    /// <summary>Replies written at once (1..4); each has its own threads and cache.</summary>
    public int MaxConcurrent { get; set; } = 1;

    /// <summary>Replies that may wait for a free slot beyond the running ones (0..64); past it the templates answer.</summary>
    public int MaxQueued { get; set; } = 2;

    /// <summary>The hard budget of one reply, prompt included, in wall-clock milliseconds (250..60000); a reply not done by then is dropped and the templates answer.</summary>
    public int BudgetMs { get; set; } = 6000;

    /// <summary>The most tokens of one reply (8..256).</summary>
    public int MaxNewTokens { get; set; } = 48;

    /// <summary>The context window per slot in tokens (256..32768; capped at the model's own): the prompt and the reply must fit.</summary>
    public int ContextTokens { get; set; } = 1024;

    /// <summary>Sampling temperature (0..2; 0 = always the most likely token).</summary>
    public double Temperature { get; set; } = 0.7;

    /// <summary>Sample among the k most likely tokens (1..256).</summary>
    public int TopK { get; set; } = 40;

    /// <summary>Then among the smallest set holding this much probability (0.05..1).</summary>
    public double TopP { get; set; } = 0.9;

    /// <summary>The penalty on tokens already in the recent text (1..2; 1 = none).</summary>
    public double RepetitionPenalty { get; set; } = 1.15;

    /// <summary>A fixed seed makes the same prompt give the same reply (unset: a fresh draw each time).</summary>
    public int? Seed { get; set; }

    /// <summary>The longest reply said (40..255 characters); a longer one is dropped and the templates answer.</summary>
    public int MaxReplyCharacters { get; set; } = 160;

    /// <summary>Budget overruns in a row that open the circuit (1..100): the model then rests for <see cref="CircuitBreakerSeconds"/> and the templates answer.</summary>
    public int CircuitBreakerOverruns { get; set; } = 3;

    /// <summary>How long an open circuit keeps the model out (10..3600 seconds).</summary>
    public int CircuitBreakerSeconds { get; set; } = 300;

    /// <summary>Check the values; <paramref name="chat"/> is the parent section (a model provider needs a ModelPath and a budget under its timeout).</summary>
    public void Validate(PlayerbotChatOptions chat)
    {
        ArgumentNullException.ThrowIfNull(chat);
        const string section = PlayerbotOptions.SectionName + ":Chat:InProcess";
        if (ModelPath is null || ModelPath.Length > 260 || ModelPath.Any(char.IsControl))
            throw new InvalidOperationException($"{section}: ModelPath must be at most 260 characters and contain no control characters.");
        if (Threads is < 1 or > 16) throw new InvalidOperationException($"{section}: Threads must be 1..16.");
        if (MaxConcurrent is < 1 or > 4) throw new InvalidOperationException($"{section}: MaxConcurrent must be 1..4.");
        if (MaxQueued is < 0 or > 64) throw new InvalidOperationException($"{section}: MaxQueued must be 0..64.");
        if (BudgetMs is < 250 or > 60_000) throw new InvalidOperationException($"{section}: BudgetMs must be 250..60000.");
        if (MaxNewTokens is < 8 or > 256) throw new InvalidOperationException($"{section}: MaxNewTokens must be 8..256.");
        if (ContextTokens is < 256 or > 32_768) throw new InvalidOperationException($"{section}: ContextTokens must be 256..32768.");
        if (!double.IsFinite(Temperature) || Temperature is < 0 or > 2) throw new InvalidOperationException($"{section}: Temperature must be 0..2.");
        if (TopK is < 1 or > Inference.ChatSampler.MaxTopK) throw new InvalidOperationException($"{section}: TopK must be 1..{Inference.ChatSampler.MaxTopK}.");
        if (!double.IsFinite(TopP) || TopP is < 0.05 or > 1) throw new InvalidOperationException($"{section}: TopP must be 0.05..1.");
        if (!double.IsFinite(RepetitionPenalty) || RepetitionPenalty is < 1 or > 2) throw new InvalidOperationException($"{section}: RepetitionPenalty must be 1..2.");
        if (MaxReplyCharacters is < 40 or > PlayerbotChatPrompts.MaxReplyLength) throw new InvalidOperationException($"{section}: MaxReplyCharacters must be 40..{PlayerbotChatPrompts.MaxReplyLength}.");
        if (CircuitBreakerOverruns is < 1 or > 100) throw new InvalidOperationException($"{section}: CircuitBreakerOverruns must be 1..100.");
        if (CircuitBreakerSeconds is < 10 or > 3600) throw new InvalidOperationException($"{section}: CircuitBreakerSeconds must be 10..3600.");
        if (!(chat.Providers ?? []).Any(p => p?.Kind == PlayerbotChatProviderKind.InProcess)) return;
        if (string.IsNullOrWhiteSpace(ModelPath))
            throw new InvalidOperationException($"{section}: ModelPath is required when a provider of Kind InProcess is configured.");
        // The chat's own request timeout cancels the wait: a budget past it would only ever end as a timeout.
        if (BudgetMs >= chat.TimeoutSeconds * 1000)
            throw new InvalidOperationException($"{section}: BudgetMs must be below {PlayerbotOptions.SectionName}:Chat:TimeoutSeconds x 1000 ({chat.TimeoutSeconds * 1000}).");
    }

    /// <summary>A provider entry of kind InProcess: its problem (null when valid). It takes no URL, key, moderation step or price.</summary>
    internal static string? ProviderProblem(PlayerbotChatProviderOptions provider)
    {
        if (!string.IsNullOrWhiteSpace(provider.BaseUrl)) return "an InProcess provider takes no BaseUrl";
        if (!string.IsNullOrEmpty(provider.ApiKeyEnvironmentVariable)) return "an InProcess provider takes no ApiKeyEnvironmentVariable";
        if (provider.Moderation != PlayerbotChatModerationKind.None) return "an InProcess provider supports Moderation None only (the local filter screens every line first)";
        if (provider.Model is null || provider.Model.Trim().Length > 200 || provider.Model.Any(char.IsControl))
            return "Model must be at most 200 characters without control characters";
        return null;
    }
}
