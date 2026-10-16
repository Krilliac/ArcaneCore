using System.Collections.Concurrent;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Tests.Characters.Creation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests;

/// <summary>
/// A stop requested while the world is still starting (2026-10-16 22:43: Ctrl-C about 90 s into the bot restore). The host rethrew the cancelled start out of
/// <c>RunAsync</c>, the crash handler turned it into a fail-fast, and the host disposed the container under a world thread and a save queue that were never
/// stopped: the retained character snapshots were retried into a disposed provider and lost. These run the real generic host through <see cref="WorldStartup.RunAsync"/>.
/// </summary>
public sealed class WorldStopDuringStartupTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task AStopRequestedWhileTheBotsStart_StopsEveryStartedServiceInOrder_AndReturnsTheExitCode_NotAnUnhandledCancellation()
    {
        await using var rig = new Rig(async (lifetime, saves, ct) =>
        {
            await saves.QueueAndFailOnceAsync(7);
            lifetime.StopApplication(); // Ctrl-C: the host's start token is linked to ApplicationStopping
            await Task.Delay(Timeout.Infinite, ct); // the bot restore, waiting on a login when the token fires
        });

        int code = await WorldStartup.RunAsync(rig.Host, () => Task.CompletedTask, new StringWriter(), () => 0).WaitAsync(Deadline);

        Assert.Equal(0, code);
        Assert.True(rig.Before.Stopped, "a hosted service started before the world must be stopped too (the WAL checkpointer's final checkpoint)");
        Assert.False(rig.After.Started, "nothing registered after the world (the listener) may start once the stop was requested");
        Assert.Equal(new[] { 7 }, rig.Saves.Written.ToArray());
    }

    [Fact]
    public async Task AGenuineStartFailureAfterTheWorldStarted_StillSurfaces_ButTheQueuedSnapshotsAreWrittenBeforeTheContainerIsDisposed()
    {
        await using var rig = new Rig(async (_, saves, _) =>
        {
            await saves.QueueAndFailOnceAsync(7);
            throw new InvalidOperationException("the bot restore broke");
        });

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => WorldStartup.RunAsync(rig.Host, () => Task.CompletedTask, new StringWriter(), () => 0).WaitAsync(Deadline));

        Assert.Equal("the bot restore broke", failure.Message);
        Assert.Equal(new[] { 7 }, rig.Saves.Written.ToArray());
    }

    [Fact]
    public async Task ACancellationWithoutAStopRequest_IsAFailedStart_NotAShutdown()
    {
        await using var rig = new Rig((_, _, _) => throw new OperationCanceledException("a start timeout, not a stop request"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => WorldStartup.RunAsync(rig.Host, () => Task.CompletedTask, new StringWriter(), () => 0).WaitAsync(Deadline));
        Assert.False(rig.Before.Stopped); // the host's own failed-start behaviour: nothing was turned into a stop
    }

    [Fact]
    public async Task AStopBeforeTheFeaturesAttach_StopsNoFeature_AndAStopAfterThat_IsTheSameStop()
    {
        var feature = new StopCounter();
        await using ServiceProvider services = new ServiceCollection()
            .AddSingleton<ICharacterStore>(new InMemoryCharacterStore())
            .AddSingleton<IWorldFeature>(feature)
            .BuildServiceProvider();
        IServiceScopeFactory scopes = services.GetRequiredService<IServiceScopeFactory>();
        var queue = new CharacterSaveQueue(scopes, NullLogger<CharacterSaveQueue>.Instance);
        using var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, queue, NullLogger<WorldRuntime>.Instance);
        var host = new WorldHost(world, queue, new CharacterDirectory(), scopes, NullLogger<WorldHost>.Instance) { ListenerExpected = false };

        Task first = host.StopAsync(CancellationToken.None);
        await first.WaitAsync(Deadline);

        Assert.Equal(0, feature.Stops); // the host stops every service when the stop came during its start, the world host included
        Assert.Same(first, host.StopAsync(CancellationToken.None));
    }

    [Fact]
    public void ASaveQueueDisposedWithASnapshotNotWritten_SaysSo_Once()
    {
        var capture = new LogCapture();
        using var loggers = new LoggerFactory([capture]);
        using var services = new ServiceCollection().BuildServiceProvider();
        var queue = new CharacterSaveQueue(services.GetRequiredService<IServiceScopeFactory>(), new Logger<CharacterSaveQueue>(loggers));
        queue.Enqueue(new CharacterState(9, 0, 12, 0, 0, 83.5f, 0, 1, 0)); // never started, never stopped

        queue.Dispose();
        queue.Dispose(); // the container disposes it once per registration that resolved it (CharacterSaveQueue and ICharacterSaveQueue)

        Assert.Single(capture.Lines, line => line.StartsWith("Critical", StringComparison.Ordinal) && line.Contains("1 snapshot(s) not written", StringComparison.Ordinal));
    }

    private sealed class StopCounter : IWorldFeature
    {
        private int _stops;

        public int Stops => Volatile.Read(ref _stops);

        public void Attach(WorldRuntime world)
        {
        }

        public Task StopAsync()
        {
            Interlocked.Increment(ref _stops);
            return Task.CompletedTask;
        }
    }

    /// <summary>A store whose first write of a character fails "database is locked" (retained, retried later); every later write succeeds and is recorded.</summary>
    private sealed class Saves(CharacterSaveQueue queue, InMemoryCharacterStore store)
    {
        private readonly TaskCompletionSource _failed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public ConcurrentQueue<int> Written { get; } = new();

        public Task Save(CharacterState state)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                _failed.TrySetResult();
                throw new Microsoft.Data.Sqlite.SqliteException("SQLite Error 5: 'database is locked'.", 5);
            }

            Written.Enqueue(state.Id);
            return Task.CompletedTask;
        }

        /// <summary>Queue a snapshot (a bot's login) and return once its first write failed: it is retained and only a retry can write it.</summary>
        public async Task QueueAndFailOnceAsync(int characterId)
        {
            store.SaveOverride = Save;
            queue.Enqueue(new CharacterState(characterId, 0, 12, 0, 0, 83.5f, 0, 1, 0));
            await _failed.Task.WaitAsync(Deadline);
        }
    }

    private sealed class Recorder : IHostedService
    {
        public bool Started { get; private set; }
        public bool Stopped { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Stopped = true;
            return Task.CompletedTask;
        }
    }

    /// <summary>A generic host as production registers it around the world: a service before <see cref="WorldHost"/>, the world host, a service after it.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly InMemoryCharacterStore _store = new();

        public Rig(Func<IHostApplicationLifetime, Saves, CancellationToken, Task> startupWork)
        {
            Host = new HostBuilder().ConfigureServices(services =>
            {
                services.AddSingleton<ICharacterStore>(_store);
                services.AddSingleton(sp => new CharacterSaveQueue(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance));
                services.AddSingleton(sp => new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, sp.GetRequiredService<CharacterSaveQueue>(), NullLogger<WorldRuntime>.Instance));
                services.AddSingleton(sp => new Saves(sp.GetRequiredService<CharacterSaveQueue>(), _store));
                services.AddSingleton<IHostedService>(Before);
                services.AddSingleton<IHostedService>(sp =>
                {
                    CharacterSaveQueue queue = sp.GetRequiredService<CharacterSaveQueue>();
                    IHostApplicationLifetime lifetime = sp.GetRequiredService<IHostApplicationLifetime>();
                    Saves saves = sp.GetRequiredService<Saves>();
                    Saves = saves;
                    return new WorldHost(sp.GetRequiredService<WorldRuntime>(), queue, new CharacterDirectory(), sp.GetRequiredService<IServiceScopeFactory>(),
                        NullLogger<WorldHost>.Instance, applicationLifetime: lifetime)
                    {
                        ListenerExpected = false,
                        StartupWorkForTesting = ct => startupWork(lifetime, saves, ct),
                    };
                });
                services.AddSingleton<IHostedService>(After);
            }).Build();
        }

        public IHost Host { get; }
        public Recorder Before { get; } = new();
        public Recorder After { get; } = new();
        public Saves Saves { get; private set; } = null!;

        public ValueTask DisposeAsync()
        {
            Host.Dispose(); // RunAsync already disposed it; idempotent
            return ValueTask.CompletedTask;
        }
    }
}
