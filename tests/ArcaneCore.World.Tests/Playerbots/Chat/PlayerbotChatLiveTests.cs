using ArcaneCore.World.Playerbots.Chat;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Chat;

/// <summary>
/// Opt-in live checks: one tiny real request per provider (a few dozen tokens). Skipped unless ARCANECORE_TEST_LIVE_LLM=1 and that
/// provider's key or endpoint is set, so the ordinary suite never touches the network and never spends money.
/// </summary>
public sealed class PlayerbotChatLiveTests
{
    private static readonly BotChatPrompt Prompt = new(PlayerbotChatPrompts.StaticSystem,
        "You are Livetest, a level 12 male dwarf hunter. You are in Loch Modan. You are speaking with Nathan by whisper.",
        [new BotChatTurn(true, "hi, what are you up to?")]);

    private sealed class LiveFactAttribute : FactAttribute
    {
        public LiveFactAttribute(string variable)
        {
            if (Environment.GetEnvironmentVariable("ARCANECORE_TEST_LIVE_LLM") != "1")
                Skip = "Set ARCANECORE_TEST_LIVE_LLM=1 (and " + variable + ") to send one tiny live request.";
            else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
                Skip = "Set " + variable + " to send this provider's live request.";
        }
    }

    private static async Task AssertAnswersAsync(PlayerbotChatProviderOptions provider)
    {
        Assert.Null(provider.Problem());
        string? key = provider.EffectiveKeyVariable is { } variable ? Environment.GetEnvironmentVariable(variable) : null;
        using var client = new HttpBotChatClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        BotChatResult result = await client.CompleteAsync(provider, key, Prompt, deadline.Token);
        Assert.Equal(BotChatOutcome.Ok, result.Outcome);
        (string? reply, _) = PlayerbotChatPrompts.ReadAnswer(result.Text);
        Assert.False(string.IsNullOrWhiteSpace(reply), "the answer held no reply: " + result.Text);
        Assert.True(reply!.Length <= PlayerbotChatPrompts.MaxReplyLength);
    }

    [LiveFact("ANTHROPIC_API_KEY")]
    public Task Anthropic_AnswersOneTinyRequest() => AssertAnswersAsync(new PlayerbotChatProviderOptions
    {
        Kind = PlayerbotChatProviderKind.Anthropic, MaxTokens = 60,
    });

    [LiveFact("OPENAI_API_KEY")]
    public Task OpenAI_AnswersOneTinyRequest() => AssertAnswersAsync(new PlayerbotChatProviderOptions
    {
        Kind = PlayerbotChatProviderKind.OpenAICompatible, BaseUrl = "https://api.openai.com/v1",
        Model = Environment.GetEnvironmentVariable("ARCANECORE_TEST_OPENAI_MODEL") ?? "gpt-4o-mini",
        ApiKeyEnvironmentVariable = "OPENAI_API_KEY", TokenLimitParameter = "max_completion_tokens", MaxTokens = 60,
    });

    [LiveFact("OPENROUTER_API_KEY")]
    public Task OpenRouter_AnswersOneTinyRequest() => AssertAnswersAsync(new PlayerbotChatProviderOptions
    {
        Kind = PlayerbotChatProviderKind.OpenAICompatible, BaseUrl = "https://openrouter.ai/api/v1",
        Model = Environment.GetEnvironmentVariable("ARCANECORE_TEST_OPENROUTER_MODEL") ?? "openai/gpt-4o-mini",
        ApiKeyEnvironmentVariable = "OPENROUTER_API_KEY", MaxTokens = 60,
        Headers = { ["HTTP-Referer"] = "https://github.com/Krilliac/ArcaneCore", ["X-Title"] = "ArcaneCore tests" },
    });

    /// <summary>A local OpenAI-compatible server (Ollama, LM Studio): ARCANECORE_TEST_LOCAL_LLM_URL (e.g. http://localhost:11434/v1) and _MODEL.</summary>
    [LiveFact("ARCANECORE_TEST_LOCAL_LLM_URL")]
    public Task LocalServer_AnswersOneTinyRequest() => AssertAnswersAsync(new PlayerbotChatProviderOptions
    {
        Kind = PlayerbotChatProviderKind.OpenAICompatible, BaseUrl = Environment.GetEnvironmentVariable("ARCANECORE_TEST_LOCAL_LLM_URL")!,
        Model = Environment.GetEnvironmentVariable("ARCANECORE_TEST_LOCAL_LLM_MODEL") ?? "qwen3:0.6b", ApiKeyEnvironmentVariable = "", MaxTokens = 200,
    });
}
