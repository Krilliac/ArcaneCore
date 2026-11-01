using System.Reflection;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Chat;
using ArcaneCore.World.Playerbots.Chat.Inference;
using ArcaneCore.World.Tests.Playerbots.Chat;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The rollback of a quiesce the feature host gave up on: <see cref="ManagedPlayerbotFeature.AttachFrom"/> runs on the world thread while
/// <c>StopServicesAsync</c> is still running. The stop takes all three chat fields before it awaits anything, so a rollback that builds a
/// fresh chain is never disposed by the old stop, and the attach moves the blocking dispose of a stale chain off the world thread.
/// </summary>
public sealed class PlayerbotChatStopAttachRaceTests
{
    private static readonly PlayerbotChatProviderOptions Local = new() { Kind = PlayerbotChatProviderKind.InProcess, Model = "tiny" };

    private static readonly BotChatPrompt Prompt = PlayerbotChatPrompts.Build(ChatSamples.Ask("hello there"), []);

    private static readonly BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    private static WorldTestHost Start()
        => ManagedPlayerbotLifecycleTests.Start(new InMemoryAccountStore(), new InMemoryCharacterStore(), new ManagedPlayerbotLifecycleTests.MemoryManagedPlayerbotStore(), manualClock: true);

    private static void SetField(ManagedPlayerbotFeature feature, string name, object? value)
        => typeof(ManagedPlayerbotFeature).GetField(name, Private)!.SetValue(feature, value);

    [Fact]
    public async Task ARollbackAttachDuringTheStop_KeepsItsNewChatChainAlive_WhenTheStopFinishes()
    {
        await using WorldTestHost host = Start();
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        PlayerbotChat oldChat = feature.Chat!;
        InProcessBotChatClient oldInProcess = feature.ChatInProcess!;
        Assert.NotNull(oldChat);
        Assert.NotNull(oldInProcess);

        // The stop starts (it runs on to its first incomplete await, having taken the three chat fields), then the rollback attaches again on the world thread.
        Task stop = (Task)typeof(ManagedPlayerbotFeature).GetMethod("StopServicesAsync", Private)!.Invoke(feature, null)!;
        await host.World.InvokeAsync(() =>
        {
            feature.AttachFrom(host.World, null, new FeatureSubscriptions("chat-stop-attach-race"));
            return true;
        });
        await stop.WaitAsync(TimeSpan.FromSeconds(60));

        PlayerbotChat? chat = feature.Chat;
        InProcessBotChatClient? inProcess = feature.ChatInProcess;
        Assert.NotNull(chat);
        Assert.NotNull(inProcess);
        Assert.NotSame(oldChat, chat);
        Assert.NotSame(oldInProcess, inProcess);
        Assert.True(chat.IsActive || !feature.BotOptions.Chat.Enabled, "the new chat service was not disposed by the old stop");

        // The new provider still serves: a disposed one answers Unavailable ("disposed"), a live one without a model Rejected ("unloaded").
        BotChatResult result = await inProcess.CompleteAsync(Local, null, Prompt, CancellationToken.None);
        Assert.NotEqual(BotChatOutcome.Unavailable, result.Outcome);
    }

    [Fact]
    public async Task AttachReturnsPromptly_WhenTheStaleClientsModelLoadIsStillRunning()
    {
        await using WorldTestHost host = Start();
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        PlayerbotChat oldChat = feature.Chat!;
        using var release = new ManualResetEventSlim();
        var options = new PlayerbotOptions { Enabled = true };
        options.Chat.Enabled = true;
        options.Chat.Providers = [Local];
        options.Chat.InProcess.ModelPath = "stale.gguf";
        // The load of the stale client sits in its loader (a model mapping or a warm-up that takes long); Dispose would wait for it up to 30 s.
        using var stale = new InProcessBotChatClient(new FakeBotChatClient(), options, Path.GetTempPath(), null,
            loader: (_, _) =>
            {
                release.Wait(TimeSpan.FromSeconds(120));
                throw new InvalidDataException("test loader released");
            });

        try
        {
            // An interrupted stop: the chat service is gone, the provider is left behind.
            SetField(feature, "_chat", null);
            SetField(feature, "_chatInProcess", stale);
            SetField(feature, "_chatClient", null);

            await host.World.InvokeAsync(() =>
            {
                feature.AttachFrom(host.World, null, new FeatureSubscriptions("chat-stale-attach"));
                return true;
            }).WaitAsync(TimeSpan.FromSeconds(10)); // the old code blocked here for the stale client's load (up to 30 s)

            Assert.NotNull(feature.Chat);
            Assert.NotSame(stale, feature.ChatInProcess);
        }
        finally
        {
            release.Set();
            await oldChat.DisposeAsync();
        }
    }
}
