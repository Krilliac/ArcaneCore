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

    private static void UndoSubscriptions(ManagedPlayerbotFeature feature)
        => (typeof(ManagedPlayerbotFeature).GetField("_subscriptions", Private)!.GetValue(feature) as IDisposable)?.Dispose();

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

        // The stop is held where the chat's DisposeAsync would wait on its workers: after it took the chat fields, before it disposes anything.
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        feature.StopHoldForTests = () =>
        {
            reached.SetResult();
            return release.Task;
        };
        Task stop = (Task)typeof(ManagedPlayerbotFeature).GetMethod("StopServicesAsync", Private)!.Invoke(feature, null)!;
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(stop.IsCompleted, "the stop must be held part way");

        // The rollback attaches again on the world thread while the stop is still inside its await.
        await host.World.InvokeAsync(() =>
        {
            UndoSubscriptions(feature); // a rollback attaches after the detach undid the subscriptions (one session policy at a time)
            feature.AttachFrom(host.World, null, new FeatureSubscriptions("chat-stop-attach-race"));
            return true;
        });
        release.SetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(60));
        feature.StopHoldForTests = null;

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
    public async Task ACanceledQuiesceAfterAdmission_CannotCaptureTheServicesRetainedByRollback()
    {
        await using WorldTestHost host = Start();
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        string[] fields = ["_policy", "_navigationExecutor", "_chat", "_chatInProcess", "_chatClient"];
        object?[] original = fields.Select(name => typeof(ManagedPlayerbotFeature).GetField(name, Private)!.GetValue(feature)).ToArray();
        Assert.NotNull(feature.Chat);
        Assert.NotNull(feature.ChatInProcess);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        feature.StopCaptureHoldForTests = () => { reached.TrySetResult(); return release.Task; };
        using var cancel = new CancellationTokenSource();
        Task quiesce = feature.QuiesceAsync(cancel.Token);
        try
        {
            // The first token check passed, but service capture has not been admitted onto the world thread.
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await cancel.CancelAsync();
            await host.World.InvokeAsync(() =>
            {
                UndoSubscriptions(feature);
                feature.AttachFrom(host.World, null, new FeatureSubscriptions("canceled-stop-rollback"));
                return true;
            });
            for (int i = 0; i < fields.Length; i++)
                Assert.Same(original[i], typeof(ManagedPlayerbotFeature).GetField(fields[i], Private)!.GetValue(feature));

            release.TrySetResult();
            Exception? error = await Record.ExceptionAsync(() => quiesce.WaitAsync(TimeSpan.FromSeconds(10)));
            // A late stop must neither take nor dispose anything from the bundle rollback retained.
            for (int i = 0; i < fields.Length; i++)
                Assert.Same(original[i], typeof(ManagedPlayerbotFeature).GetField(fields[i], Private)!.GetValue(feature));
            Assert.IsAssignableFrom<OperationCanceledException>(error);
            Assert.False(feature.IsDetaching);
            BotChatResult result = await feature.ChatInProcess!.CompleteAsync(Local, null, Prompt, CancellationToken.None);
            Assert.NotEqual(BotChatOutcome.Unavailable, result.Outcome);
        }
        finally
        {
            feature.StopCaptureHoldForTests = null;
            release.TrySetResult();
            await cancel.CancelAsync();
            try { await quiesce.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task CancellationDuringCaptureResultDelivery_StillDisposesTheOldBundle_AndKeepsRollbackAlive()
    {
        await using WorldTestHost host = Start();
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        PlayerbotChat oldChat = feature.Chat!;
        InProcessBotChatClient oldInProcess = feature.ChatInProcess!;
        IDisposable? oldClient = typeof(ManagedPlayerbotFeature).GetField("_chatClient", Private)!.GetValue(feature) as IDisposable;
        PlayerbotChat? replacementChat = null;
        InProcessBotChatClient? replacementInProcess = null;
        using var cancel = new CancellationTokenSource();
        feature.StopCapturedForTests = () =>
        {
            Assert.True(host.World.IsWorldThread);
            cancel.Cancel(); // the source fields are gone, but the posted callback has not returned its owned snapshot yet
            UndoSubscriptions(feature);
            feature.AttachFrom(host.World, null, new FeatureSubscriptions("captured-result-rollback"));
            replacementChat = feature.Chat;
            replacementInProcess = feature.ChatInProcess;
        };
        try
        {
            Exception? error = await Record.ExceptionAsync(() => feature.QuiesceAsync(cancel.Token).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Null(error);
            Assert.NotNull(replacementChat);
            Assert.NotNull(replacementInProcess);
            Assert.NotSame(oldChat, replacementChat);
            Assert.NotSame(oldInProcess, replacementInProcess);
            Assert.Same(replacementChat, feature.Chat);
            Assert.Same(replacementInProcess, feature.ChatInProcess);
            Assert.Equal(BotChatOutcome.Unavailable, (await oldInProcess.CompleteAsync(Local, null, Prompt, CancellationToken.None)).Outcome);
            Assert.NotEqual(BotChatOutcome.Unavailable, (await replacementInProcess.CompleteAsync(Local, null, Prompt, CancellationToken.None)).Outcome);
        }
        finally
        {
            feature.StopCapturedForTests = null;
            await oldChat.DisposeAsync();
            oldInProcess.Dispose();
            oldClient?.Dispose();
        }
    }

    [Fact]
    public async Task AnEarlyStopFailure_StillDisposesTheRemainingOldBundle_WithoutTouchingRollback()
    {
        await using WorldTestHost host = Start();
        ManagedPlayerbotFeature feature = host.WorldServices.GetRequiredService<ManagedPlayerbotFeature>();
        PlayerbotChat oldChat = feature.Chat!;
        InProcessBotChatClient oldInProcess = feature.ChatInProcess!;
        IDisposable? oldClient = typeof(ManagedPlayerbotFeature).GetField("_chatClient", Private)!.GetValue(feature) as IDisposable;
        var failure = new InvalidOperationException("service-stop-hold-failed");
        PlayerbotChat? replacementChat = null;
        InProcessBotChatClient? replacementInProcess = null;
        feature.StopHoldForTests = async () =>
        {
            await host.World.InvokeAsync(() =>
            {
                UndoSubscriptions(feature);
                feature.AttachFrom(host.World, null, new FeatureSubscriptions("failed-stop-rollback"));
                replacementChat = feature.Chat;
                replacementInProcess = feature.ChatInProcess;
                return true;
            });
            throw failure;
        };
        try
        {
            Task stop = (Task)typeof(ManagedPlayerbotFeature).GetMethod("StopServicesAsync", Private)!.Invoke(feature, null)!;
            Exception? error = await Record.ExceptionAsync(() => stop.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Same(failure, error);
            Assert.NotNull(replacementInProcess);
            Assert.Same(replacementChat, feature.Chat);
            Assert.Same(replacementInProcess, feature.ChatInProcess);
            Assert.Equal(BotChatOutcome.Unavailable, (await oldInProcess.CompleteAsync(Local, null, Prompt, CancellationToken.None)).Outcome);
            Assert.NotEqual(BotChatOutcome.Unavailable, (await replacementInProcess.CompleteAsync(Local, null, Prompt, CancellationToken.None)).Outcome);
        }
        finally
        {
            feature.StopHoldForTests = null;
            await oldChat.DisposeAsync();
            oldInProcess.Dispose();
            oldClient?.Dispose();
        }
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
                UndoSubscriptions(feature); // a rollback attaches after the detach undid the subscriptions (one session policy at a time)
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
