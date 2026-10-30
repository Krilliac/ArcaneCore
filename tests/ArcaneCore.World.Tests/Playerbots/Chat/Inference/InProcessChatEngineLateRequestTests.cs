using System.Reflection;
using ArcaneCore.World.Playerbots.Chat.Inference;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Chat.Inference;

/// <summary>A request posted after the engine thread has exited must complete at once instead of waiting for a drain that never runs.</summary>
public sealed class InProcessChatEngineLateRequestTests
{
    [Fact]
    public async Task A_request_posted_after_the_engine_thread_exited_fails_at_once()
    {
        TinyModel tiny = TinyModel.Create("llama", 8, 2, 1, 16, context: 2048);
        string path = tiny.Builder.WriteTemp();
        try
        {
            using InProcessChatEngine engine = InProcessChatEngine.Over(ChatModel.Load(path), 2048, 1, 1);
            StopEngineThread(engine);

            var settings = new ChatGenerationSettings(MaxNewTokens: 4, BudgetMs: 60_000, Temperature: 0f, TopK: 1, TopP: 1f, RepetitionPenalty: 1f, Seed: 1, StopAtNewline: false);
            using var cts = new CancellationTokenSource();
            ChatGenerationResult result = await engine.GenerateAsync([1, 2], settings, cts.Token).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(ChatGenerationStatus.Failed, result.Status);
        }
        finally
        {
            GgufBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task A_prefix_warm_posted_after_the_engine_thread_exited_completes_with_zero_tokens()
    {
        TinyModel tiny = TinyModel.Create("llama", 8, 2, 1, 16, context: 2048);
        string path = tiny.Builder.WriteTemp();
        try
        {
            using InProcessChatEngine engine = InProcessChatEngine.Over(ChatModel.Load(path), 2048, 1, 1);
            StopEngineThread(engine);

            Assert.Equal(0, await engine.WarmPrefixAsync([1, 2]).WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            GgufBuilder.Delete(path);
        }
    }

    /// <summary>
    /// Stop the engine thread the way a fault does, then lift the flag the caller's fast check reads: the request has passed that
    /// check just before the thread died (the race).
    /// </summary>
    private static void StopEngineThread(InProcessChatEngine engine)
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        Type type = typeof(InProcessChatEngine);
        FieldInfo stopping = type.GetField("_stopping", Private)!;
        stopping.SetValue(engine, true);
        ((SemaphoreSlim)type.GetField("_wake", Private)!.GetValue(engine)!).Release();
        Assert.True(((Thread)type.GetField("_loop", Private)!.GetValue(engine)!).Join(TimeSpan.FromSeconds(30)));
        stopping.SetValue(engine, false);
    }
}
