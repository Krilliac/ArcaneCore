using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Reload;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Reload;
using ArcaneCore.World.Reload;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Reload;

/// <summary>The reload pipeline itself: build off the world thread, validate, swap on it, roll back.</summary>
public sealed class ReloadCoordinatorTests : IDisposable
{
    private readonly WorldRuntime _world = new(
        new WorldRuntimeOptions { TickIntervalMs = 5, AutosaveIntervalMs = 0 },
        new NullSaveQueue(),
        NullLogger<WorldRuntime>.Instance);

    public void Dispose() => _world.Dispose();

    private ReloadCoordinator Attached(HotReloadOptions? options = null, bool startWorld = true)
    {
        var coordinator = new ReloadCoordinator(NullLogger.Instance, options);
        coordinator.Attach(_world);
        if (startWorld)
        {
            _world.Start();
        }

        return coordinator;
    }

    [Fact]
    public async Task Build_RunsOffTheWorldThread_AndCommit_OnIt()
    {
        ReloadCoordinator coordinator = Attached();
        var fake = new FakeReloadable("fake");
        coordinator.Register(fake);

        ReloadResult result = await coordinator.ReloadAsync("fake");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.True(result.Changed);
        Assert.False(fake.BuiltOnWorldThread);
        Assert.True(fake.CommittedOnWorldThread);
        Assert.Contains("fake summary", result.Message);
    }

    [Fact]
    public async Task ABuildThatThrows_ReportsFailed_AndNeverCommits()
    {
        ReloadCoordinator coordinator = Attached();
        var fake = new FakeReloadable("fake") { BuildFailure = new InvalidOperationException("database offline") };
        coordinator.Register(fake);

        ReloadResult result = await coordinator.ReloadAsync("fake");

        Assert.Equal(ReloadStatus.Failed, result.Status);
        Assert.Contains("database offline", result.Message);
        Assert.Equal(0, fake.Commits);
    }

    [Fact]
    public async Task ValidationProblems_RejectTheReload_AndNeverCommit()
    {
        ReloadCoordinator coordinator = Attached();
        var fake = new FakeReloadable("fake") { Problems = ["row 7 is broken", "row 9 is broken"] };
        coordinator.Register(fake);

        ReloadResult result = await coordinator.ReloadAsync("fake");

        Assert.Equal(ReloadStatus.Rejected, result.Status);
        Assert.Equal(["row 7 is broken", "row 9 is broken"], result.Notes);
        Assert.Equal(0, fake.Commits);
    }

    [Fact]
    public async Task KeepCurrent_SkipsTheCommit_AndSaysWhy()
    {
        ReloadCoordinator coordinator = Attached();
        var fake = new FakeReloadable("fake") { KeepCurrentReason = "table is empty" };
        coordinator.Register(fake);

        ReloadResult result = await coordinator.ReloadAsync("fake");

        Assert.Equal(ReloadStatus.KeptCurrent, result.Status);
        Assert.False(result.Changed);
        Assert.Contains("table is empty", result.Message);
        Assert.Equal(0, fake.Commits);
    }

    [Fact]
    public async Task ACommitThatThrows_RollsBackTheStepsAlreadyApplied()
    {
        ReloadCoordinator coordinator = Attached();
        var live = new List<string> { "old" };
        var fake = new FakeReloadable("fake")
        {
            CommitAction = (world, tx) =>
            {
                tx.Step("first", () => live.Add("new1"), () => live.Remove("new1"));
                tx.Step("second", () => live.Add("new2"), () => live.Remove("new2"));
                tx.Step("third", () => throw new InvalidOperationException("swap exploded"), () => { });
            },
        };
        coordinator.Register(fake);

        ReloadResult result = await coordinator.ReloadAsync("fake");

        Assert.Equal(ReloadStatus.Failed, result.Status);
        Assert.Contains("swap exploded", result.Message);
        Assert.Contains("rolled back", result.Message);
        Assert.Equal(["old"], live);
    }

    [Fact]
    public async Task NotesAddedDuringTheCommit_ReachTheResult()
    {
        ReloadCoordinator coordinator = Attached();
        var fake = new FakeReloadable("fake") { CommitAction = (world, tx) => tx.Note("Port option can't be changed at reload") };
        coordinator.Register(fake);

        ReloadResult result = await coordinator.ReloadAsync("fake");

        Assert.Equal(ReloadStatus.Applied, result.Status);
        Assert.Equal(["Port option can't be changed at reload"], result.Notes);
    }

    [Fact]
    public async Task ASecondReload_WhileOneRuns_IsBusy()
    {
        ReloadCoordinator coordinator = Attached();
        var gate = new TaskCompletionSource();
        var fake = new FakeReloadable("slow") { BuildGate = gate.Task };
        coordinator.Register(fake);

        Task<ReloadResult> first = coordinator.ReloadAsync("slow");
        await fake.BuildStarted.Task;
        ReloadResult second = await coordinator.ReloadAsync("slow");
        gate.SetResult();
        ReloadResult firstResult = await first;

        Assert.Equal(ReloadStatus.Busy, second.Status);
        Assert.Equal(ReloadStatus.Applied, firstResult.Status);
        Assert.Equal(1, fake.Commits);
    }

    [Fact]
    public async Task ASwapTheWorldThreadNeverRuns_TimesOut_AndIsCancelledForGood()
    {
        ReloadCoordinator coordinator = Attached(new HotReloadOptions { CommitTimeoutMs = 100 }, startWorld: false);
        var fake = new FakeReloadable("fake");
        coordinator.Register(fake);

        ReloadResult result = await coordinator.ReloadAsync("fake");
        _world.Start(); // the world thread now drains the queued swap

        Assert.Equal(ReloadStatus.Failed, result.Status);
        Assert.Contains("world thread", result.Message);
        await Task.Delay(100);
        Assert.Equal(0, fake.Commits);
    }

    [Fact]
    public async Task ABuildThatTakesTooLong_IsAbandoned()
    {
        ReloadCoordinator coordinator = Attached(new HotReloadOptions { BuildTimeoutMs = 100 });
        var fake = new FakeReloadable("slow") { BuildGate = new TaskCompletionSource().Task };
        coordinator.Register(fake);

        ReloadResult result = await coordinator.ReloadAsync("slow");

        Assert.Equal(ReloadStatus.Failed, result.Status);
        Assert.Contains("timed out", result.Message);
        Assert.Equal(0, fake.Commits);
    }

    [Fact]
    public async Task UnknownName_IsRejected_WithTheKnownNames()
    {
        ReloadCoordinator coordinator = Attached();
        coordinator.Register(new FakeReloadable("alpha"));
        coordinator.Register(new FakeReloadable("beta"));

        ReloadResult result = await coordinator.ReloadAsync("gamma");

        Assert.Equal(ReloadStatus.Rejected, result.Status);
        Assert.Contains("alpha", result.Message);
        Assert.Contains("beta", result.Message);
    }

    [Fact]
    public void RegisteringTheSameNameTwice_Throws()
    {
        ReloadCoordinator coordinator = Attached();
        coordinator.Register(new FakeReloadable("alpha"));

        Assert.Throws<InvalidOperationException>(() => coordinator.Register(new FakeReloadable("ALPHA")));
    }

    [Fact]
    public async Task ReloadAll_RunsEveryIncludedReloadable_InNameOrder_AndSkipsTheExcluded()
    {
        ReloadCoordinator coordinator = Attached();
        var order = new List<string>();
        var b = new FakeReloadable("b") { CommitAction = (w, t) => order.Add("b") };
        var a = new FakeReloadable("a") { CommitAction = (w, t) => order.Add("a") };
        var config = new FakeReloadable("config") { InAll = false, CommitAction = (w, t) => order.Add("config") };
        coordinator.Register(b);
        coordinator.Register(a);
        coordinator.Register(config);

        IReadOnlyList<ReloadResult> results = await coordinator.ReloadAllAsync();

        Assert.Equal(["a", "b"], results.Select(r => r.Name));
        Assert.Equal(["a", "b"], order);
    }

    [Fact]
    public async Task Status_RemembersTheLastResultPerName()
    {
        ReloadCoordinator coordinator = Attached();
        coordinator.Register(new FakeReloadable("fake"));
        Assert.Empty(coordinator.LastResults);

        await coordinator.ReloadAsync("fake");

        ReloadResult last = Assert.Single(coordinator.LastResults).Value;
        Assert.Equal(ReloadStatus.Applied, last.Status);
    }

    [Fact]
    public async Task Reload_WithoutAWorld_FailsInsteadOfHanging()
    {
        var coordinator = new ReloadCoordinator(NullLogger.Instance);
        coordinator.Register(new FakeReloadable("fake"));

        ReloadResult result = await coordinator.ReloadAsync("fake");

        Assert.Equal(ReloadStatus.Failed, result.Status);
    }

    private sealed class NullSaveQueue : ICharacterSaveQueue
    {
        public void Enqueue(CharacterState state)
        {
        }
    }

    private sealed class FakeReloadable(string name) : IContentReloadable
    {
        private int _commits;

        public string Name { get; } = name;

        public bool InAll { get; init; } = true;

        public bool IncludedInAll => InAll;

        public Exception? BuildFailure { get; init; }

        public IReadOnlyList<string> Problems { get; init; } = [];

        public string? KeepCurrentReason { get; init; }

        public Task? BuildGate { get; init; }

        public Action<WorldRuntime, ReloadTransaction>? CommitAction { get; init; }

        public TaskCompletionSource BuildStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool BuiltOnWorldThread { get; private set; }

        public bool CommittedOnWorldThread { get; private set; }

        public int Commits => Volatile.Read(ref _commits);

        public async Task<ContentCandidate> BuildAsync(CancellationToken cancellationToken)
        {
            BuiltOnWorldThread = Thread.CurrentThread.Name == "world";
            BuildStarted.TrySetResult();
            if (BuildGate is not null)
            {
                await BuildGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (BuildFailure is not null)
            {
                throw BuildFailure;
            }

            return new FakeCandidate(this);
        }

        private sealed class FakeCandidate(FakeReloadable owner) : ContentCandidate
        {
            public override string Summary => "fake summary";

            public override IReadOnlyList<string> Validate() => owner.Problems;

            public override bool TryKeepCurrent(WorldRuntime world, out string reason)
            {
                reason = owner.KeepCurrentReason ?? string.Empty;
                return owner.KeepCurrentReason is not null;
            }

            public override void Commit(WorldRuntime world, ReloadTransaction transaction)
            {
                Interlocked.Increment(ref owner._commits);
                owner.CommittedOnWorldThread = Thread.CurrentThread.Name == "world" && world.IsWorldThread;
                owner.CommitAction?.Invoke(world, transaction);
            }
        }
    }
}
