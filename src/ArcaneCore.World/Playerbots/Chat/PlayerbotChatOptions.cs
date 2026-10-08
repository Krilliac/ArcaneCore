using System.Net;

namespace ArcaneCore.World.Playerbots.Chat;

/// <summary>The chat lines a bot answers (<see cref="PlayerbotChatOptions.Channels"/>; a comma-separated list in configuration).</summary>
[Flags]
public enum PlayerbotChatChannels
{
    /// <summary>No line is answered (the bot keeps today's fixed replies).</summary>
    None = 0,

    /// <summary>A whisper to the bot.</summary>
    Whisper = 1,

    /// <summary>A party or raid line that names the bot.</summary>
    Party = 2,

    /// <summary>A /say line within hearing that names the bot.</summary>
    Say = 4,
}

/// <summary>How a <see cref="PlayerbotChatProviderOptions"/> entry produces a reply.</summary>
public enum PlayerbotChatProviderKind
{
    /// <summary>Template replies from the bot's own state: no network, no key, no cost. Always the last provider of the chain.</summary>
    Builtin,

    /// <summary>The Anthropic Messages API: <c>POST {BaseUrl}/v1/messages</c> with <c>x-api-key</c> and <c>anthropic-version</c>.</summary>
    Anthropic,

    /// <summary>
    /// An OpenAI-compatible chat completions endpoint: <c>POST {BaseUrl}/chat/completions</c> with <c>Authorization: Bearer</c>
    /// (OpenAI, OpenRouter, and local servers such as Ollama or LM Studio).
    /// </summary>
    OpenAICompatible,
}

/// <summary>A provider's optional moderation step before a player's line is sent to it (<see cref="PlayerbotChatProviderOptions.Moderation"/>).</summary>
public enum PlayerbotChatModerationKind
{
    /// <summary>No moderation step: the local filter only.</summary>
    None,

    /// <summary>
    /// An OpenAI-compatible moderation endpoint: <c>POST {ModerationBaseUrl or BaseUrl}/moderations</c> with <c>{"model", "input"}</c>;
    /// a result with <c>flagged: true</c> flags the line.
    /// </summary>
    Endpoint,

    /// <summary>A short classification request to the provider itself (one word, SAFE or UNSAFE) before the reply request.</summary>
    Classify,
}

/// <summary>
/// <c>World:Playerbots:Chat</c>: managed bots answer players who talk to them (docs/areas/playbots.md, Bot chat). A whisper, or a party
/// or say line that names the bot, is answered in character by the first provider of <see cref="Providers"/> that can answer now; the
/// <see cref="PlayerbotChatProviderKind.Builtin"/> template provider is always last, so a bot always has something to say. Every key
/// is live: <c>.reload config</c> changes this object in place.
/// </summary>
public sealed class PlayerbotChatOptions
{
    /// <summary>The most entries <see cref="Providers"/> may hold.</summary>
    public const int MaxProviders = 8;

    /// <summary>Let bots answer players' chat (on by default, with the built-in templates only). Off: the fixed replies of before.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The reply providers, tried in order until one answers (failover on an error, a timeout, a 429, a spent budget or a missing
    /// key). Empty by default: only the built-in templates answer. The built-in provider is appended when it is not listed, and when
    /// listed it must be last. See docs/areas/playbots.md for the provider keys (Kind, BaseUrl, Model, ApiKeyEnvironmentVariable,
    /// Headers, MaxRepliesPerHour, MaxTokens, TokenLimitParameter, InputUsdPerMillionTokens, OutputUsdPerMillionTokens, Moderation,
    /// ModerationBaseUrl, ModerationModel, ModerationApiKeyEnvironmentVariable).
    /// </summary>
    public PlayerbotChatProviderOptions[] Providers { get; set; } = [];

    /// <summary>The lines a bot answers: <c>Whisper</c>, <c>Party</c> and <c>Say</c> (comma-separated; all three by default).</summary>
    public PlayerbotChatChannels Channels { get; set; } = PlayerbotChatChannels.Whisper | PlayerbotChatChannels.Party | PlayerbotChatChannels.Say;

    /// <summary>Seconds after a reply before the same player gets another one from any bot (0..3600; lines in between are not answered).</summary>
    public int PerPlayerCooldownSeconds { get; set; } = 8;

    /// <summary>
    /// The most estimated US dollars the priced providers may spend per UTC day (0..10000; 0 = no daily cap). The estimate counts the
    /// tokens each reply reports at the provider's price; a provider without a known price is limited by its MaxRepliesPerHour only.
    /// </summary>
    public double MaxDailySpendUsd { get; set; } = 1.0;

    /// <summary>Earlier exchanges with the same player a bot remembers and sends with a model request (0..16).</summary>
    public int MemoryExchanges { get; set; } = 4;

    /// <summary>Seconds one model request may take before the next provider is tried (1..60).</summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>The most replies waiting for a provider at once (1..256); a line that finds the queue full is not answered.</summary>
    public int MaxQueuedRequests { get; set; } = 16;

    /// <summary>
    /// Let the party master's plain-language orders ("follow me", "wait here", "attack my target") drive the party commands (on by
    /// default). Only the master's lines are read for orders; the one-word commands work either way.
    /// </summary>
    public bool NaturalLanguageCommands { get; set; } = true;

    /// <summary>
    /// <c>Safety</c>: screening of players' lines and model replies, strikes and cut-offs, the AI disclosure and opt-out, and how little
    /// is sent to a model provider (docs/areas/playbots.md, Safety and provider policies).
    /// </summary>
    public PlayerbotChatSafetyOptions Safety { get; set; } = new();

    /// <summary>The providers in the order they are tried: the configured ones, then the built-in templates (the configured entry or a default one).</summary>
    internal IReadOnlyList<PlayerbotChatProviderOptions> EffectiveProviders()
    {
        PlayerbotChatProviderOptions[] configured = Providers ?? [];
        var chain = new List<PlayerbotChatProviderOptions>(configured.Length + 1);
        PlayerbotChatProviderOptions? builtin = null;
        foreach (PlayerbotChatProviderOptions provider in configured)
        {
            if (provider is null) continue;
            if (provider.Kind == PlayerbotChatProviderKind.Builtin) builtin ??= provider;
            else chain.Add(provider);
        }

        chain.Add(builtin ?? DefaultBuiltin);
        return chain;
    }

    private static readonly PlayerbotChatProviderOptions DefaultBuiltin = new() { Kind = PlayerbotChatProviderKind.Builtin };

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Chat";
        if (CheckProviders(Providers) is { } providers) throw new InvalidOperationException($"{section}:Providers {providers}");
        if (CheckChannels(Channels) is { } channels) throw new InvalidOperationException($"{section}:Channels {channels}");
        if (PerPlayerCooldownSeconds is < 0 or > 3600) throw new InvalidOperationException($"{section}: PerPlayerCooldownSeconds must be 0..3600.");
        if (CheckSpend(MaxDailySpendUsd) is { } spend) throw new InvalidOperationException($"{section}: MaxDailySpendUsd {spend}");
        if (MemoryExchanges is < 0 or > 16) throw new InvalidOperationException($"{section}: MemoryExchanges must be 0..16.");
        if (TimeoutSeconds is < 1 or > 60) throw new InvalidOperationException($"{section}: TimeoutSeconds must be 1..60.");
        if (MaxQueuedRequests is < 1 or > 256) throw new InvalidOperationException($"{section}: MaxQueuedRequests must be 1..256.");
        if (Safety is null) throw new InvalidOperationException($"{section}:Safety is missing.");
        Safety.Validate();
    }

    /// <summary>Whether a model provider (anything but the built-in templates) is configured.</summary>
    internal bool HasModelProvider => (Providers ?? []).Any(provider => provider is { Kind: not PlayerbotChatProviderKind.Builtin });

    internal static string? CheckChannels(PlayerbotChatChannels channels)
        => (channels & ~(PlayerbotChatChannels.Whisper | PlayerbotChatChannels.Party | PlayerbotChatChannels.Say)) == 0 ? null
            : "must be a combination of Whisper, Party and Say";

    internal static string? CheckSpend(double usd) => double.IsFinite(usd) && usd is >= 0 and <= 10_000 ? null : "must be 0..10000";

    /// <summary>The provider list's problem (null when valid): at most <see cref="MaxProviders"/>, each valid, the built-in one at most once and last.</summary>
    internal static string? CheckProviders(PlayerbotChatProviderOptions[]? providers)
    {
        if (providers is null) return "is missing";
        if (providers.Length > MaxProviders) return $"must hold at most {MaxProviders} entries";
        for (int i = 0; i < providers.Length; i++)
        {
            if (providers[i] is not { } provider) return $"entry {i} is empty";
            if (provider.Problem() is { } problem) return $"entry {i}: {problem}";
            if (provider.Kind == PlayerbotChatProviderKind.Builtin && i != providers.Length - 1) return $"entry {i}: the Builtin provider must be the last entry";
        }

        return null;
    }
}

/// <summary>
/// One entry of <c>World:Playerbots:Chat:Providers</c>. The key is read only from the environment variable named by
/// <see cref="ApiKeyEnvironmentVariable"/>, never from configuration, and is never logged or shown.
/// </summary>
public sealed class PlayerbotChatProviderOptions
{
    /// <summary>The default Anthropic model (Claude Haiku 4.5: the small, fast, cheap model).</summary>
    public const string DefaultAnthropicModel = "claude-haiku-4-5";

    /// <summary>The Anthropic API origin used when <see cref="BaseUrl"/> is empty.</summary>
    public const string DefaultAnthropicBaseUrl = "https://api.anthropic.com";

    /// <summary>Header names the provider sets itself, which <see cref="Headers"/> may not replace.</summary>
    private static readonly string[] ReservedHeaders = ["authorization", "x-api-key", "anthropic-version", "content-type", "content-length", "host"];

    /// <summary>How this provider answers: <c>Builtin</c>, <c>Anthropic</c> or <c>OpenAICompatible</c>.</summary>
    public PlayerbotChatProviderKind Kind { get; set; } = PlayerbotChatProviderKind.Builtin;

    /// <summary>
    /// The endpoint root: for Anthropic the origin (empty = https://api.anthropic.com; <c>/v1/messages</c> is appended), for
    /// OpenAICompatible the API base including its version (<c>/chat/completions</c> is appended), e.g. https://api.openai.com/v1,
    /// https://openrouter.ai/api/v1 or http://localhost:11434/v1. Unused by Builtin.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>The model id (empty = claude-haiku-4-5 for Anthropic; required for OpenAICompatible; unused by Builtin).</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// The environment variable that holds the key. Unset: ANTHROPIC_API_KEY for Anthropic, none for the others. Empty: the endpoint
    /// takes no key (a local server). A provider whose variable is not set is skipped.
    /// </summary>
    public string? ApiKeyEnvironmentVariable { get; set; }

    /// <summary>Extra request headers (e.g. OpenRouter's HTTP-Referer and X-Title); they may not replace the key or version headers.</summary>
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The most replies this provider gives per hour, over all bots (1..100000); past it the next provider answers.</summary>
    public int MaxRepliesPerHour { get; set; } = 120;

    /// <summary>The most tokens of one model reply (16..1024; unused by Builtin).</summary>
    public int MaxTokens { get; set; } = 150;

    /// <summary>OpenAICompatible only: the body field of the reply limit, <c>max_tokens</c> (default) or <c>max_completion_tokens</c> (newer OpenAI models).</summary>
    public string TokenLimitParameter { get; set; } = "max_tokens";

    /// <summary>US dollars per million input tokens for the spend estimate (unset = the built-in price of a known Anthropic model, else unpriced; 0 for a local model).</summary>
    public double? InputUsdPerMillionTokens { get; set; }

    /// <summary>US dollars per million output tokens for the spend estimate (unset = the built-in price of a known Anthropic model, else unpriced).</summary>
    public double? OutputUsdPerMillionTokens { get; set; }

    /// <summary>
    /// The moderation step before each player line is sent to this provider: <c>None</c> (default), <c>Endpoint</c> (an
    /// OpenAI-compatible <c>/moderations</c> endpoint) or <c>Classify</c> (a one-word classification request to this provider). A line
    /// it flags goes to no provider; when the step fails, this provider is skipped.
    /// </summary>
    public PlayerbotChatModerationKind Moderation { get; set; } = PlayerbotChatModerationKind.None;

    /// <summary>Endpoint moderation: the API base whose <c>/moderations</c> is called (empty = <see cref="BaseUrl"/>; required for Anthropic).</summary>
    public string ModerationBaseUrl { get; set; } = string.Empty;

    /// <summary>Endpoint moderation: the moderation model (empty = omni-moderation-latest).</summary>
    public string ModerationModel { get; set; } = string.Empty;

    /// <summary>Endpoint moderation: the environment variable of the moderation key (unset = this provider's key; empty = none).</summary>
    public string? ModerationApiKeyEnvironmentVariable { get; set; }

    /// <summary>The default moderation model of <see cref="PlayerbotChatModerationKind.Endpoint"/>.</summary>
    public const string DefaultModerationModel = "omni-moderation-latest";

    /// <summary>The model actually sent (the Anthropic default when <see cref="Model"/> is empty).</summary>
    internal string EffectiveModel => Kind == PlayerbotChatProviderKind.Anthropic && string.IsNullOrWhiteSpace(Model) ? DefaultAnthropicModel : Model.Trim();

    /// <summary>The variable the key is read from, or null when this provider takes no key.</summary>
    internal string? EffectiveKeyVariable => ApiKeyEnvironmentVariable is { } named
        ? (string.IsNullOrWhiteSpace(named) ? null : named.Trim())
        : Kind == PlayerbotChatProviderKind.Anthropic ? "ANTHROPIC_API_KEY" : null;

    /// <summary>The moderation model actually sent.</summary>
    internal string EffectiveModerationModel => string.IsNullOrWhiteSpace(ModerationModel) ? DefaultModerationModel : ModerationModel.Trim();

    /// <summary>The variable the moderation key is read from, or null when the moderation endpoint takes no key.</summary>
    internal string? EffectiveModerationKeyVariable => ModerationApiKeyEnvironmentVariable is { } named
        ? (string.IsNullOrWhiteSpace(named) ? null : named.Trim())
        : EffectiveKeyVariable;

    /// <summary>The moderation endpoint (<see cref="PlayerbotChatModerationKind.Endpoint"/> only; null otherwise).</summary>
    internal Uri? ModerationEndpoint
    {
        get
        {
            if (Moderation != PlayerbotChatModerationKind.Endpoint) return null;
            string root = !string.IsNullOrWhiteSpace(ModerationBaseUrl) ? ModerationBaseUrl.Trim()
                : Kind == PlayerbotChatProviderKind.OpenAICompatible ? BaseUrl.Trim() : string.Empty;
            return root.Length == 0 ? null : new Uri(root.TrimEnd('/') + "/moderations");
        }
    }

    /// <summary>The request URL of this provider (null for Builtin).</summary>
    internal Uri? Endpoint => Kind switch
    {
        PlayerbotChatProviderKind.Anthropic => new Uri((string.IsNullOrWhiteSpace(BaseUrl) ? DefaultAnthropicBaseUrl : BaseUrl.Trim()).TrimEnd('/') + "/v1/messages"),
        PlayerbotChatProviderKind.OpenAICompatible => new Uri(BaseUrl.Trim().TrimEnd('/') + "/chat/completions"),
        _ => null,
    };

    /// <summary>The (input, output) US dollars per million tokens, or null when this provider is unpriced. Builtin costs nothing.</summary>
    internal (double Input, double Output)? Price
    {
        get
        {
            if (Kind == PlayerbotChatProviderKind.Builtin) return (0, 0);
            (double Input, double Output)? known = Kind == PlayerbotChatProviderKind.Anthropic ? KnownAnthropicPrice(EffectiveModel) : null;
            double? input = InputUsdPerMillionTokens ?? known?.Input;
            double? output = OutputUsdPerMillionTokens ?? known?.Output;
            return input is { } i && output is { } o ? (i, o) : null;
        }
    }

    /// <summary>
    /// First-party Anthropic prices in US dollars per million tokens (input, output), as published on 2026-09-25. Cache writes are
    /// estimated at 1.25x and cache reads at 0.1x the input price.
    /// </summary>
    internal static (double Input, double Output)? KnownAnthropicPrice(string model) => model switch
    {
        "claude-haiku-4-5" => (1, 5),
        "claude-sonnet-5-5" or "claude-sonnet-5" => (2, 10),
        "claude-sonnet-4-6" => (3, 15),
        "claude-opus-5-5" => (4, 20),
        "claude-opus-5" or "claude-opus-4-8" or "claude-opus-4-7" or "claude-opus-4-6" => (5, 25),
        "claude-fable-5-1" or "claude-fable-5" => (10, 50),
        _ => null,
    };

    /// <summary>A short label for status lines and logs (kind and model; never the key).</summary>
    internal string Label => Kind == PlayerbotChatProviderKind.Builtin ? "Builtin" : $"{Kind} {EffectiveModel}";

    /// <summary>What identifies this provider's runtime state (hour window, cooldown) across a reload.</summary>
    internal string Signature => $"{Kind}|{BaseUrl.Trim()}|{EffectiveModel}|{EffectiveKeyVariable}";

    /// <summary>This entry's problem (null when valid).</summary>
    internal string? Problem()
    {
        if (!Enum.IsDefined(Kind)) return "Kind must be Builtin, Anthropic or OpenAICompatible";
        if (MaxRepliesPerHour is < 1 or > 100_000) return "MaxRepliesPerHour must be 1..100000";
        if (Kind == PlayerbotChatProviderKind.Builtin) return null;
        if (MaxTokens is < 16 or > 1024) return "MaxTokens must be 16..1024";
        if (Kind == PlayerbotChatProviderKind.OpenAICompatible && string.IsNullOrWhiteSpace(BaseUrl)) return "BaseUrl is required for OpenAICompatible";
        if (!string.IsNullOrWhiteSpace(BaseUrl))
        {
            if (!Uri.TryCreate(BaseUrl.Trim(), UriKind.Absolute, out Uri? root) || root.Scheme is not ("http" or "https")
                || !string.IsNullOrEmpty(root.Query) || !string.IsNullOrEmpty(root.Fragment) || !string.IsNullOrEmpty(root.UserInfo))
                return "BaseUrl must be an absolute http or https URL without query, fragment or credentials";
            if (root.Scheme == "http" && EffectiveKeyVariable is not null && !IsLoopback(root))
                return "BaseUrl must use https when a key is sent to a host that is not this machine";
        }

        if (Kind == PlayerbotChatProviderKind.OpenAICompatible && string.IsNullOrWhiteSpace(Model)) return "Model is required for OpenAICompatible";
        if (EffectiveModel.Length > 200 || EffectiveModel.Any(char.IsControl)) return "Model must be at most 200 characters without control characters";
        if (ApiKeyEnvironmentVariable is { Length: > 0 } variable
            && (variable.Trim().Length > 128 || !variable.Trim().All(c => char.IsAsciiLetterOrDigit(c) || c == '_')))
            return "ApiKeyEnvironmentVariable must be a variable name (letters, digits, underscores; at most 128)";
        if (TokenLimitParameter is not ("max_tokens" or "max_completion_tokens")) return "TokenLimitParameter must be max_tokens or max_completion_tokens";
        if (InputUsdPerMillionTokens is { } input && (!double.IsFinite(input) || input is < 0 or > 1000)) return "InputUsdPerMillionTokens must be 0..1000";
        if (OutputUsdPerMillionTokens is { } output && (!double.IsFinite(output) || output is < 0 or > 1000)) return "OutputUsdPerMillionTokens must be 0..1000";
        if (ModerationProblem() is { } moderation) return moderation;
        if (Headers is null || Headers.Count > 16) return "Headers must hold at most 16 entries";
        foreach ((string name, string value) in Headers)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 64 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                return "a header name must be letters, digits, '-' or '_' (at most 64)";
            if (ReservedHeaders.Contains(name.ToLowerInvariant())) return $"header {name} is set by the provider itself";
            if (value is null || value.Length > 512 || value.Any(char.IsControl)) return $"header {name} must be at most 512 characters without control characters";
        }

        return null;
    }

    private string? ModerationProblem()
    {
        if (!Enum.IsDefined(Moderation)) return "Moderation must be None, Endpoint or Classify";
        if (ModerationModel is null || ModerationModel.Trim().Length > 200 || ModerationModel.Any(char.IsControl))
            return "ModerationModel must be at most 200 characters without control characters";
        if (ModerationApiKeyEnvironmentVariable is { Length: > 0 } variable
            && (variable.Trim().Length > 128 || !variable.Trim().All(c => char.IsAsciiLetterOrDigit(c) || c == '_')))
            return "ModerationApiKeyEnvironmentVariable must be a variable name (letters, digits, underscores; at most 128)";
        if (ModerationBaseUrl is null) return "ModerationBaseUrl is missing";
        if (!string.IsNullOrWhiteSpace(ModerationBaseUrl))
        {
            if (!Uri.TryCreate(ModerationBaseUrl.Trim(), UriKind.Absolute, out Uri? root) || root.Scheme is not ("http" or "https")
                || !string.IsNullOrEmpty(root.Query) || !string.IsNullOrEmpty(root.Fragment) || !string.IsNullOrEmpty(root.UserInfo))
                return "ModerationBaseUrl must be an absolute http or https URL without query, fragment or credentials";
            if (root.Scheme == "http" && EffectiveModerationKeyVariable is not null && !IsLoopback(root))
                return "ModerationBaseUrl must use https when a key is sent to a host that is not this machine";
        }

        if (Moderation == PlayerbotChatModerationKind.Endpoint && ModerationEndpoint is null)
            return "Moderation Endpoint needs ModerationBaseUrl (or an OpenAICompatible BaseUrl)";
        return null;
    }

    private static bool IsLoopback(Uri uri)
        => uri.IsLoopback || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out IPAddress? address) && IPAddress.IsLoopback(address));

    /// <summary>A deep copy (reload snapshots and status).</summary>
    internal PlayerbotChatProviderOptions Clone() => new()
    {
        Kind = Kind, BaseUrl = BaseUrl, Model = Model, ApiKeyEnvironmentVariable = ApiKeyEnvironmentVariable,
        Headers = new Dictionary<string, string>(Headers ?? [], StringComparer.OrdinalIgnoreCase),
        MaxRepliesPerHour = MaxRepliesPerHour, MaxTokens = MaxTokens, TokenLimitParameter = TokenLimitParameter,
        InputUsdPerMillionTokens = InputUsdPerMillionTokens, OutputUsdPerMillionTokens = OutputUsdPerMillionTokens,
        Moderation = Moderation, ModerationBaseUrl = ModerationBaseUrl, ModerationModel = ModerationModel,
        ModerationApiKeyEnvironmentVariable = ModerationApiKeyEnvironmentVariable,
    };

    /// <summary>Whether two entries configure the same provider in every setting.</summary>
    internal bool SameAs(PlayerbotChatProviderOptions other)
        => Kind == other.Kind && BaseUrl == other.BaseUrl && Model == other.Model && ApiKeyEnvironmentVariable == other.ApiKeyEnvironmentVariable
            && MaxRepliesPerHour == other.MaxRepliesPerHour && MaxTokens == other.MaxTokens && TokenLimitParameter == other.TokenLimitParameter
            && InputUsdPerMillionTokens == other.InputUsdPerMillionTokens && OutputUsdPerMillionTokens == other.OutputUsdPerMillionTokens
            && Moderation == other.Moderation && ModerationBaseUrl == other.ModerationBaseUrl && ModerationModel == other.ModerationModel
            && ModerationApiKeyEnvironmentVariable == other.ModerationApiKeyEnvironmentVariable
            && (Headers ?? []).Count == (other.Headers ?? []).Count
            && (Headers ?? []).All(pair => (other.Headers ?? []).TryGetValue(pair.Key, out string? value) && value == pair.Value);
}

/// <summary>
/// The provider list as one reload value: equal when every entry configures the same provider (the reload compares values with
/// <see cref="object.Equals(object?)"/>), shown as the provider labels (never a key; keys are never in configuration).
/// </summary>
internal sealed class PlayerbotChatProviderList(PlayerbotChatProviderOptions[] items) : IEquatable<PlayerbotChatProviderList>
{
    public PlayerbotChatProviderOptions[] Items { get; } = items;

    public bool Equals(PlayerbotChatProviderList? other)
        => other is not null && Items.Length == other.Items.Length && Items.Zip(other.Items).All(pair => pair.First.SameAs(pair.Second));

    public override bool Equals(object? obj) => Equals(obj as PlayerbotChatProviderList);

    public override int GetHashCode() => Items.Length;

    public override string ToString() => Items.Length == 0 ? "[Builtin]" : "[" + string.Join(", ", Items.Select(p => p.Label)) + "]";
}
