using System.Collections.Concurrent;
using ArcaneCore.Game.Groups;
using ArcaneCore.World.Cluster.Social;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Social;

/// <summary>
/// Group commands of a worker that has no link to the Social holder yet (the peer link connects some seconds after startup, and goes away when the holder does): they wait in the
/// bounded queue in order and are sent once the authority is reachable, instead of being dropped, and the not-linked state throws nothing. No timers drive the assertions: the
/// tests wait on the sender's own signals and flush explicitly.
/// </summary>
public sealed class ClusterGroupRoutingHoldTests
{
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(30);

    /// <summary>An authority that can be switched between linked and not linked; calling it while not linked throws, as the lease-routed transport did.</summary>
    private sealed class SwitchableAuthority : IGroupAuthorityTransport
    {
        private readonly TaskCompletionSource _probed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _reachedExpected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reachable;
        private int _throws;

        public ConcurrentQueue<GroupCommand> Executed { get; } = new();

        public Func<GroupCommand, bool>? Refuse { get; set; }

        /// <summary>Completes the first time the sender asked while the authority was not reachable (so it holds a command).</summary>
        public Task Probed => _probed.Task;

        public int ExpectedCount { get; set; } = -1;

        /// <summary>Completes when <see cref="Executed"/> reaches <see cref="ExpectedCount"/> (a full queue cannot take a flush marker, so a test waits on the sends themselves).</summary>
        public Task ReachedExpected => _reachedExpected.Task;

        public int Throws => Volatile.Read(ref _throws);

        public void SetReachable(bool value) => Volatile.Write(ref _reachable, value ? 1 : 0);

        public bool IsAuthorityReachable
        {
            get
            {
                bool reachable = Volatile.Read(ref _reachable) != 0;
                if (!reachable)
                {
                    _probed.TrySetResult();
                }

                return reachable;
            }
        }

        public void ExecuteGroupCommand(string workerId, GroupCommand command)
        {
            if (Volatile.Read(ref _reachable) == 0)
            {
                Interlocked.Increment(ref _throws);
                throw new InvalidOperationException("this node does not hold the Social lease and has no peer link to the node that does");
            }

            if (Refuse?.Invoke(command) == true)
            {
                throw new InvalidOperationException("the presence authority refused the request: test");
            }

            Executed.Enqueue(command);
            if (Executed.Count == ExpectedCount)
            {
                _reachedExpected.TrySetResult();
            }
        }

        public IReadOnlyList<GroupEvent> DrainGroupEvents(string workerId) => [];
    }

    private sealed class CapturingLogger : ILogger
    {
        public ConcurrentQueue<(LogLevel Level, string Text)> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Enqueue((logLevel, formatter(state, exception)));
    }

    private static GroupCommand Command(GroupCommandKind kind, ulong actor) => new(kind, actor, "Actor" + actor, 0, false);

    [Fact]
    public async Task Commands_queued_before_the_link_exists_are_sent_in_order_once_it_appears()
    {
        var authority = new SwitchableAuthority();
        await using var routing = new ClusterGroupRouting(authority, "worker-a");
        GroupCommandKind[] kinds = [GroupCommandKind.Login, GroupCommandKind.Invite, GroupCommandKind.Logout, GroupCommandKind.Login];
        for (int i = 0; i < kinds.Length; i++)
        {
            routing.Forward(Command(kinds[i], (ulong)(i + 1)));
        }

        await authority.Probed.WaitAsync(Safety);
        Assert.Empty(authority.Executed);
        Assert.Equal(0, routing.SendFailed);

        authority.SetReachable(true);
        await routing.FlushAsync().WaitAsync(Safety);

        Assert.Equal(kinds, authority.Executed.Select(static c => c.Kind));
        Assert.Equal(new ulong[] { 1, 2, 3, 4 }, authority.Executed.Select(static c => c.Actor));
        Assert.Equal(0, routing.SendFailed);
        Assert.Equal(0, routing.QueueDropped);
    }

    [Fact]
    public async Task Waiting_for_the_link_raises_no_exception()
    {
        var authority = new SwitchableAuthority();
        await using var routing = new ClusterGroupRouting(authority, "worker-a");
        for (ulong actor = 1; actor <= 20; actor++)
        {
            routing.Forward(Command(GroupCommandKind.Login, actor));
        }

        await authority.Probed.WaitAsync(Safety);
        authority.SetReachable(true);
        await routing.FlushAsync().WaitAsync(Safety);

        Assert.Equal(0, authority.Throws);
        Assert.Equal(20, authority.Executed.Count);
    }

    [Fact]
    public async Task A_refusal_by_the_authority_is_still_dropped_and_counted()
    {
        var authority = new SwitchableAuthority();
        authority.SetReachable(true);
        authority.Refuse = static c => c.Actor == 2;
        await using var routing = new ClusterGroupRouting(authority, "worker-a");
        routing.Forward(Command(GroupCommandKind.Login, 1));
        routing.Forward(Command(GroupCommandKind.Invite, 2));
        routing.Forward(Command(GroupCommandKind.Login, 3));
        await routing.FlushAsync().WaitAsync(Safety);

        Assert.Equal(new ulong[] { 1, 3 }, authority.Executed.Select(static c => c.Actor));
        Assert.Equal(1, routing.SendFailed);
    }

    [Fact]
    public async Task Disposing_while_commands_are_held_returns_and_releases_a_flush_caller()
    {
        var authority = new SwitchableAuthority();
        var routing = new ClusterGroupRouting(authority, "worker-a");
        routing.Forward(Command(GroupCommandKind.Login, 1));
        routing.Forward(Command(GroupCommandKind.Login, 2));
        await authority.Probed.WaitAsync(Safety);
        Task flush = routing.FlushAsync();

        await routing.DisposeAsync().AsTask().WaitAsync(Safety);
        await flush.WaitAsync(Safety);

        Assert.Empty(authority.Executed);
        Assert.Equal(0, authority.Throws);
    }

    [Fact]
    public async Task A_queue_that_fills_during_an_outage_stays_bounded_and_counts_the_overflow()
    {
        var authority = new SwitchableAuthority();
        await using var routing = new ClusterGroupRouting(authority, "worker-a");
        routing.Forward(Command(GroupCommandKind.Login, 1));
        await authority.Probed.WaitAsync(Safety); // the sender now holds command 1 and the queue is empty

        const int Overflow = 10;
        for (ulong actor = 2; actor <= ClusterGroupRouting.MaxQueuedCommands + 1 + Overflow; actor++)
        {
            routing.Forward(Command(GroupCommandKind.Login, actor));
        }

        Assert.Equal(Overflow, routing.QueueDropped);

        authority.ExpectedCount = ClusterGroupRouting.MaxQueuedCommands + 1;
        authority.SetReachable(true);
        await authority.ReachedExpected.WaitAsync(Safety);
        await routing.FlushAsync().WaitAsync(Safety);

        // The oldest commands were kept, in order; the newest overflowed.
        Assert.Equal(ClusterGroupRouting.MaxQueuedCommands + 1, authority.Executed.Count);
        Assert.Equal(1UL, authority.Executed.First().Actor);
        Assert.Equal((ulong)ClusterGroupRouting.MaxQueuedCommands + 1, authority.Executed.Last().Actor);
        Assert.Equal(0, routing.SendFailed);
    }

    [Fact]
    public async Task One_outage_logs_once_when_held_and_once_when_it_resumes()
    {
        var authority = new SwitchableAuthority();
        var logger = new CapturingLogger();
        await using var routing = new ClusterGroupRouting(authority, "worker-a", logger);
        for (ulong actor = 1; actor <= 5; actor++)
        {
            routing.Forward(Command(GroupCommandKind.Login, actor));
        }

        await authority.Probed.WaitAsync(Safety);
        authority.SetReachable(true);
        await routing.FlushAsync().WaitAsync(Safety);

        (LogLevel Level, string Text)[] lines = [.. logger.Lines];
        Assert.Equal(2, lines.Length);
        Assert.All(lines, static l => Assert.Equal(LogLevel.Information, l.Level));
        Assert.Contains("held", lines[0].Text);
        Assert.Contains("resume", lines[1].Text);
    }
}
