using System.Collections.Concurrent;
using ArcaneCore.Game.Groups;
using ArcaneCore.World.Cluster.Social;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Social;

/// <summary>
/// Group commands of a worker that has no link to the Social holder yet (the peer link connects some seconds after startup, and goes away when the holder does): they wait in the
/// bounded queue in order and are sent once the authority is reachable, instead of being dropped; transient relay commands are not held; commands that waited too long are dropped;
/// and the not-linked state throws nothing. No timers drive the assertions: the tests wait on the sender's own signals, step a manual clock and flush explicitly.
/// </summary>
public sealed class ClusterGroupRoutingHoldTests
{
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(30);

    /// <summary>An authority that can be switched between linked and not linked; calling it while not linked throws, as the lease-routed transport did.</summary>
    private sealed class SwitchableAuthority : IGroupAuthorityTransport
    {
        private TaskCompletionSource _probed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _reachedExpected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _executing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _gate = new(true);
        private int _reachable;
        private int _throws;
        private int _drains;

        public ConcurrentQueue<GroupCommand> Executed { get; } = new();

        /// <summary>Throws an <see cref="InvalidOperationException"/> refusal for a command this selects.</summary>
        public Func<GroupCommand, bool>? Refuse { get; set; }

        /// <summary>Throws an <see cref="IOException"/> (a link lost mid-call) for a command this selects.</summary>
        public Func<GroupCommand, bool>? LoseLink { get; set; }

        public int ExpectedCount { get; set; } = -1;

        /// <summary>Completes the first time the sender asked while the authority was not reachable (so it holds a command).</summary>
        public Task Probed => _probed.Task;

        public Task ReachedExpected => _reachedExpected.Task;

        /// <summary>Completes when the sender is inside <see cref="ExecuteGroupCommand"/> (used with <see cref="Block"/>).</summary>
        public Task Executing => _executing.Task;

        public int Throws => Volatile.Read(ref _throws);

        public int Drains => Volatile.Read(ref _drains);

        public void SetReachable(bool value) => Volatile.Write(ref _reachable, value ? 1 : 0);

        /// <summary>Arms a fresh <see cref="Probed"/> for a second outage.</summary>
        public void ArmProbe() => Volatile.Write(ref _probed, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        public void Block() => _gate.Reset();

        public void Release() => _gate.Set();

        public bool IsAuthorityReachable
        {
            get
            {
                bool reachable = Volatile.Read(ref _reachable) != 0;
                if (!reachable)
                {
                    Volatile.Read(ref _probed).TrySetResult();
                }

                return reachable;
            }
        }

        public void ExecuteGroupCommand(string workerId, GroupCommand command)
        {
            _executing.TrySetResult();
            bool linked = Volatile.Read(ref _reachable) != 0; // as the sender saw it before the call started
            _gate.Wait();
            if (!linked)
            {
                Interlocked.Increment(ref _throws);
                throw new InvalidOperationException("this node does not hold the Social lease and has no peer link to the node that does");
            }

            if (LoseLink?.Invoke(command) == true)
            {
                throw new IOException("the peer link was lost");
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

        public IReadOnlyList<GroupEvent> DrainGroupEvents(string workerId)
        {
            Interlocked.Increment(ref _drains);
            if (Volatile.Read(ref _reachable) == 0)
            {
                throw new InvalidOperationException("this node does not hold the Social lease and has no peer link to the node that does");
            }

            return [];
        }
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
    public async Task Disposing_while_commands_are_held_returns_releases_a_flush_caller_and_logs_what_was_discarded()
    {
        var authority = new SwitchableAuthority();
        var logger = new CapturingLogger();
        var routing = new ClusterGroupRouting(authority, "worker-a", logger);
        routing.Forward(Command(GroupCommandKind.Login, 1));
        routing.Forward(Command(GroupCommandKind.Login, 2));
        routing.Forward(Command(GroupCommandKind.Login, 3));
        await authority.Probed.WaitAsync(Safety);
        Task flush = routing.FlushAsync();

        await routing.DisposeAsync().AsTask().WaitAsync(Safety);
        await flush.WaitAsync(Safety);

        Assert.Empty(authority.Executed);
        Assert.Equal(0, authority.Throws);
        Assert.Contains(logger.AtLeast(LogLevel.Information), static l => l.Text.Contains("3 held commands were discarded"));
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
        await authority.ReachedExpected.WaitAsync(Safety); // a full queue cannot take a flush marker, so wait on the sends themselves
        await routing.FlushAsync().WaitAsync(Safety);

        // The oldest commands were kept, in order; the newest overflowed.
        Assert.Equal(ClusterGroupRouting.MaxQueuedCommands + 1, authority.Executed.Count);
        Assert.Equal(1UL, authority.Executed.First().Actor);
        Assert.Equal((ulong)ClusterGroupRouting.MaxQueuedCommands + 1, authority.Executed.Last().Actor);
        Assert.Equal(0, routing.SendFailed);
    }

    [Fact]
    public async Task Relays_are_never_held_so_a_logout_and_leave_after_a_long_outage_still_arrive()
    {
        var authority = new SwitchableAuthority();
        await using var routing = new ClusterGroupRouting(authority, "worker-a");
        routing.Forward(Command(GroupCommandKind.Login, 1));
        await authority.Probed.WaitAsync(Safety);

        for (int i = 0; i < ClusterGroupRouting.MaxQueuedCommands + 1; i++)
        {
            routing.Forward(Command(GroupCommandKind.Relay, 1));
        }

        routing.Forward(Command(GroupCommandKind.Logout, 1));
        routing.Forward(Command(GroupCommandKind.Leave, 1));
        authority.SetReachable(true);
        await routing.FlushAsync().WaitAsync(Safety);

        Assert.Equal(new[] { GroupCommandKind.Login, GroupCommandKind.Logout, GroupCommandKind.Leave }, authority.Executed.Select(static c => c.Kind));
        Assert.Equal(ClusterGroupRouting.MaxQueuedCommands + 1, routing.RelayDropped);
        Assert.Equal(0, routing.QueueDropped);
    }

    [Fact]
    public async Task A_relay_queued_while_linked_is_dropped_if_the_link_is_gone_when_its_turn_comes()
    {
        var authority = new SwitchableAuthority();
        authority.SetReachable(true);
        await using var routing = new ClusterGroupRouting(authority, "worker-a");
        authority.Block();
        routing.Forward(Command(GroupCommandKind.Leave, 1));
        await authority.Executing.WaitAsync(Safety); // the sender is inside the send of command 1
        routing.Forward(Command(GroupCommandKind.Relay, 1)); // accepted: still linked
        authority.SetReachable(false);
        authority.Release();

        await routing.FlushAsync().WaitAsync(Safety);

        Assert.Equal(1, routing.RelayDropped);
        Assert.Equal(new[] { GroupCommandKind.Leave }, authority.Executed.Select(static c => c.Kind));
    }

    [Fact]
    public async Task A_command_that_waited_longer_than_the_age_limit_is_dropped_and_counted_and_a_fresh_one_behind_it_goes()
    {
        var authority = new SwitchableAuthority();
        var time = new SteppedTimeProvider();
        var logger = new CapturingLogger();
        await using var routing = new ClusterGroupRouting(authority, "worker-a", logger, time);
        routing.Forward(Command(GroupCommandKind.Leave, 1)); // stale by the time the link comes up
        await authority.Probed.WaitAsync(Safety);
        time.Advance(ClusterGroupRouting.MaxHeldCommandAge + TimeSpan.FromSeconds(10));
        routing.Forward(Command(GroupCommandKind.Logout, 2)); // fresh

        authority.SetReachable(true);
        await routing.FlushAsync().WaitAsync(Safety);

        Assert.Equal(new ulong[] { 2 }, authority.Executed.Select(static c => c.Actor));
        Assert.Equal(1, routing.Expired);
        Assert.Contains(logger.AtLeast(LogLevel.Information), static l => l.Text.Contains("resume") && l.Text.Contains("1 older than 120 s"));
    }

    [Fact]
    public async Task A_command_exactly_inside_the_age_limit_is_still_sent()
    {
        var authority = new SwitchableAuthority();
        var time = new SteppedTimeProvider();
        await using var routing = new ClusterGroupRouting(authority, "worker-a", null, time);
        routing.Forward(Command(GroupCommandKind.Leave, 1));
        await authority.Probed.WaitAsync(Safety);
        time.Advance(ClusterGroupRouting.MaxHeldCommandAge - TimeSpan.FromSeconds(1));

        authority.SetReachable(true);
        await routing.FlushAsync().WaitAsync(Safety);

        Assert.Single(authority.Executed);
        Assert.Equal(0, routing.Expired);
    }

    [Fact]
    public async Task A_link_lost_mid_send_drops_that_command_then_the_next_ones_are_held_and_sent_after_the_resume()
    {
        var authority = new SwitchableAuthority();
        authority.SetReachable(true);
        authority.LoseLink = static c => c.Actor == 1;
        await using var routing = new ClusterGroupRouting(authority, "worker-a");
        routing.Forward(Command(GroupCommandKind.Invite, 1));
        await routing.FlushAsync().WaitAsync(Safety);
        Assert.Equal(1, routing.SendFailed);
        Assert.Empty(authority.Executed);

        authority.SetReachable(false);
        routing.Forward(Command(GroupCommandKind.Login, 2));
        routing.Forward(Command(GroupCommandKind.Logout, 3));
        await authority.Probed.WaitAsync(Safety);
        authority.SetReachable(true);
        await routing.FlushAsync().WaitAsync(Safety);

        Assert.Equal(new ulong[] { 2, 3 }, authority.Executed.Select(static c => c.Actor));
        Assert.Equal(1, routing.SendFailed);
    }

    [Fact]
    public async Task One_outage_logs_once_when_held_and_once_when_it_resumes_and_a_second_outage_logs_again()
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

        (LogLevel Level, string Text)[] lines = logger.AtLeast(LogLevel.Information);
        Assert.Equal(2, lines.Length);
        Assert.All(lines, static l => Assert.Equal(LogLevel.Information, l.Level));
        Assert.Contains("held", lines[0].Text);
        Assert.Contains("resume", lines[1].Text);

        authority.SetReachable(false);
        authority.ArmProbe();
        routing.Forward(Command(GroupCommandKind.Login, 6));
        routing.Forward(Command(GroupCommandKind.Login, 7));
        await authority.Probed.WaitAsync(Safety);
        authority.SetReachable(true);
        await routing.FlushAsync().WaitAsync(Safety);

        lines = logger.AtLeast(LogLevel.Information);
        Assert.Equal(4, lines.Length);
        Assert.Contains("held", lines[2].Text);
        Assert.Contains("resume", lines[3].Text);
        Assert.Equal(7, authority.Executed.Count);
    }

    [Fact]
    public async Task The_event_pump_drains_nothing_and_throws_nothing_while_the_authority_is_not_reachable()
    {
        var authority = new SwitchableAuthority();
        await using var routing = new ClusterGroupRouting(authority, "worker-a");
        routing.Deliver = static _ => { };

        int handed = routing.PumpOnce();

        Assert.Equal(0, handed);
        Assert.Equal(0, authority.Drains);

        authority.SetReachable(true);
        routing.PumpOnce();
        Assert.Equal(1, authority.Drains);
    }
}
