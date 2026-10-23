using System.Reflection;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Chat;
using ArcaneCore.World.Playerbots.Chat.Inference;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Chat.Inference;

/// <summary>
/// A model load that starts after <see cref="InProcessBotChatClient.Dispose"/> (the request that passed the disposed check in
/// <c>CompleteLocalAsync</c> just before Dispose ran) must not leave a live engine behind: the mapped model and the inference threads
/// have no other owner once the client is disposed.
/// </summary>
public sealed class InProcessBotChatClientDisposeTests
{
    private static readonly PlayerbotChatProviderOptions Local = new() { Kind = PlayerbotChatProviderKind.InProcess, Model = "tiny" };

    [Fact]
    public async Task A_load_that_starts_after_dispose_disposes_its_engine_instead_of_leaking_it()
    {
        TinyModel tiny = TinyModel.Create("llama", 8, 2, 1, 16, context: 2048);
        string path = tiny.Builder.WriteTemp();
        try
        {
            var options = new PlayerbotOptions { Enabled = false }; // no load at construction: Dispose finds _load null, as in the race
            options.Chat.Providers = [Local];
            options.Chat.InProcess.ModelPath = "tiny.gguf";
            InProcessChatEngine? built = null;
            var client = new InProcessBotChatClient(new FakeBotChatClient(), options, Path.GetTempPath(), null, null,
                (_, _) => built = InProcessChatEngine.Over(ChatModel.Load(path), 2048, 1, 1));

            client.Dispose();

            // The request that read _disposed == 0 before Dispose now reaches EnsureLoaded.
            MethodInfo ensureLoaded = typeof(InProcessBotChatClient).GetMethod("EnsureLoaded", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var load = (Task<InProcessChatEngine?>)ensureLoaded.Invoke(client, null)!;
            InProcessChatEngine? engine = await load.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Null(engine); // a disposed client hands out no engine
            Assert.Null(client.Engine);
            if (built is { } leaked) // the loader ran: the engine it built must have been disposed
                Assert.Throws<ObjectDisposedException>(() => { _ = leaked.WarmPrefixAsync([1]); });
        }
        finally
        {
            GgufBuilder.Delete(path);
        }
    }

    [Fact]
    public async Task A_load_that_finishes_after_dispose_began_returns_no_engine_and_disposes_it()
    {
        TinyModel tiny = TinyModel.Create("llama", 8, 2, 1, 16, context: 2048);
        string path = tiny.Builder.WriteTemp();
        try
        {
            var options = new PlayerbotOptions { Enabled = false };
            options.Chat.Providers = [Local];
            options.Chat.InProcess.ModelPath = "tiny.gguf";
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            InProcessChatEngine? built = null;
            var client = new InProcessBotChatClient(new FakeBotChatClient(), options, Path.GetTempPath(), null, null, (_, _) =>
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(30)));
                return built = InProcessChatEngine.Over(ChatModel.Load(path), 2048, 1, 1);
            });

            MethodInfo ensureLoaded = typeof(InProcessBotChatClient).GetMethod("EnsureLoaded", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var load = (Task<InProcessChatEngine?>)ensureLoaded.Invoke(client, null)!;
            Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));

            Task dispose = Task.Run(client.Dispose); // sets _disposed, then waits for the load that is still running
            FieldInfo disposed = typeof(InProcessBotChatClient).GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!;
            SpinWait.SpinUntil(() => (int)disposed.GetValue(client)! != 0, TimeSpan.FromSeconds(30));
            release.Set();

            InProcessChatEngine? engine = await load.WaitAsync(TimeSpan.FromSeconds(30));
            await dispose.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Null(engine); // the late engine is disposed inside Load, never handed out
            Assert.NotNull(built);
            Assert.Throws<ObjectDisposedException>(() => { _ = built!.WarmPrefixAsync([1]); });
        }
        finally
        {
            GgufBuilder.Delete(path);
        }
    }
}
