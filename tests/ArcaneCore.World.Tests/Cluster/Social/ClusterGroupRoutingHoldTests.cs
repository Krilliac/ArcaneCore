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

        /// <summary>Every call, with the command id it carried, whatever came of it.</summary>
        public ConcurrentQueue<(ulong Actor, long Id)> Attempts { get; } = new();

        /// <summary>Throws a <see cref="PresenceRefusedException"/> for a command this selects.</summary>
        public Func<GroupCommand, bool>? Refuse { get; set; }

        /// <summary>Throws an <see cref="IOException"/> (a link lost mid-call) for a command this selects.</summary>
        public Func<GroupCommand, bool>? LoseLink { get; set; }

        /// <summary>Throws what the peer client throws when it has no link at that moment, though the check before the call said reachable.</summary>
        public Func<GroupCommand, bool>? LinkVanished { get; set; }

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

        public void ExecuteGroupCommand(string workerId, GroupCommand command, long commandId)
        {
            Attempts.Enqueue((command.Actor, commandId));
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

            if (LinkVanished?.Invoke(command) == true)
            {
                throw new InvalidOperationException("the peer link is not connected");
            }

            if (Refuse?.Invoke(command) == true)
            {
                throw new PresenceRefusedException("the presence authority refused the request: test");
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
    public async Task Relays_are_never_held_so_a_logout_and_leave_behind_a_flood_of_them_still_arrive()
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
    public async Task A_backlog_that_all_expires_still_writes_the_resume_line_with_no_command_behind_it()
    {
        var authority = new SwitchableAuthority();
        var time = new SteppedTimeProvider();
        var logger = new CapturingLogger();
        await using var routing = new ClusterGroupRouting(authority, "worker-a", logger, time);
        routing.Forward(Command(GroupCommandKind.Leave, 1));
        await authority.Probed.WaitAsync(Safety);
        routing.Forward(Command(GroupCommandKind.Leave, 2));
        time.Advance(ClusterGroupRouting.MaxHeldCommandAge + TimeSpan.FromSeconds(10));

        authority.SetReachable(true);

        // No flush: a flush marker would write the line itself, and this is about the queue running dry.
        using var safety = new CancellationTokenSource(Safety);
        while (!logger.AtLeast(LogLevel.Information).Any(static l => l.Text.Contains("resume")))
        {
            await Task.Delay(5, safety.Token);
        }

        Assert.Contains(logger.AtLeast(LogLevel.Information), static l => l.Text.Contains("resume") && l.Text.Contains("2 older than 120 s"));
        Assert.Equal(2, routing.Expired);
        Assert.Empty(authority.Executed);
    }

    [Fact]
    public async Task An_outage_starting_inside_the_last_ones_backlog_reports_the_last_ones_drops_first()
    {
        var authority = new SwitchableAuthority();
        var time = new SteppedTimeProvider();
        var logger = new CapturingLogger();
        await using var routing = new ClusterGroupRouting(authority, "worker-a", logger, time);
        routing.Forward(Command(GroupCommandKind.Leave, 1)); // expires
        await authority.Probed.WaitAsync(Safety);
        routing.Forward(Command(GroupCommandKind.Logout, 2)); // too old, but goes: the sender is inside this send when the link goes again
        time.Advance(ClusterGroupRouting.MaxHeldCommandAge + TimeSpan.FromSeconds(10));
        routing.Forward(Command(GroupCommandKind.Leave, 3)); // fresh: held by the second outage

        authority.Block();
        authority.SetReachable(true);
        await authority.Executing.WaitAsync(Safety);
        authority.SetReachable(false);
        authority.Release();
        using var safety = new CancellationTokenSource(Safety);
        while (logger.AtLeast(LogLevel.Information).Count(static l => l.Text.Contains("are held")) < 2)
        {
            await Task.Delay(5, safety.Token);
        }

        authority.SetReachable(true);
        await routing.FlushAsync().WaitAsync(Safety);

        string[] lines = [.. logger.AtLeast(LogLevel.Information).Select(static l => l.Text)];
        Assert.Equal(4, lines.Length);
        Assert.Contains("are held", lines[0]);
        Assert.Contains("1 older than 120 s", lines[1]);
        Assert.Contains("are held", lines[2]);
        Assert.Contains("0 older than 120 s", lines[3]);
        Assert.Equal(new ulong[] { 2, 3 }, authority.Executed.Select(static c => c.Actor));
    }

    [Theory]
    [InlineData(GroupCommandKind.Login)]
    [InlineData(GroupCommandKind.Logout)]
    public async Task A_login_or_logout_held_past_the_age_limit_is_still_sent(GroupCommandKind kind)
    {
        var authority = new SwitchableAuthority();
        var time = new SteppedTimeProvider();
        await using var routing = new ClusterGroupRouting(authority, "worker-a", null, time);
        routing.Forward(Command(kind, 1));
        await authority.Probed.WaitAsync(Safety);
        time.Advance(ClusterGroupRouting.MaxHeldCommandAge + TimeSpan.FromSeconds(10));

        authority.SetReachable(true);
        await routing.FlushAsync().WaitAsync(Safety);

        Assert.Equal(new[] { kind }, authority.Executed.Select(static c => c.Kind));
        Assert.Equal(0, routing.Expired);
    }

    [Fact]
    public async Task A_command_forwarded_after_the_hold_never_expires_behind_a_slow_walk_of_stale_logins()
    {
        var authority = new SwitchableAuthority();
        var time = new SteppedTimeProvider();
        await using var routing = new ClusterGroupRouting(authority, "worker-a", null, time);
        routing.Forward(Command(GroupCommandKind.Login, 1));
        await authority.Probed.WaitAsync(Safety);
        time.Advance(ClusterGroupRouting.MaxHeldCommandAge + TimeSpan.FromSeconds(10));

        authority.Block();
        authority.SetReachable(true);
        await authority.Executing.WaitAsync(Safety); // the stale Login is being sent: the backlog is still being walked
        routing.Forward(Command(GroupCommandKind.Leave, 2)); // the link is back: this one never sat through the outage
        time.Advance(ClusterGroupRouting.MaxHeldCommandAge + TimeSpan.FromSeconds(10));
        authority.Release();

        await routing.FlushAsync().WaitAsync(Safety);

        Assert.Equal(new ulong[] { 1, 2 }, authority.Executed.Select(static c => c.Actor));
        Assert.Equal(0, routing.Expired);
    }

    [Fact]
    public async Task A_flush_marker_does_not_end_the_backlog_while_stale_commands_wait_behind_it()
    {
        var authority = new SwitchableAuthority();
        var time = new SteppedTimeProvider();
        await using var routing = new ClusterGroupRouting(authority, "worker-a", null, time);
        routing.Forward(Command(GroupCommandKind.Leave, 1));
        await authority.Probed.WaitAsync(Safety);
        Task first = routing.FlushAsync();
        routing.Forward(Command(GroupCommandKind.Leave, 2));
        routing.Forward(Command(GroupCommandKind.Kick, 3));
        time.Advance(ClusterGroupRouting.MaxHeldCommandAge + TimeSpan.FromSeconds(10));

        authority.SetReachable(true);
        await first.WaitAsync(Safety);
        await routing.FlushAsync().WaitAsync(Safety);

        Assert.Empty(authority.Executed);
        Assert.Equal(3, routing.Expired);
    }

    [Fact]
    public async Task A_slow_authority_with_the_link_up_expires_nothing()
    {
        var authority = new SwitchableAuthority();
        authority.SetReachable(true);
        var time = new SteppedTimeProvider();
        await using var routing = new ClusterGroupRouting(authority, "worker-a", null, time);
        authority.Block();
        routing.Forward(Command(GroupCommandKind.Leave, 1));
        await authority.Executing.WaitAsync(Safety);
        routing.Forward(Command(GroupCommandKind.Leave, 2));
        time.Advance(ClusterGroupRouting.MaxHeldCommandAge + TimeSpan.FromSeconds(10));
        authority.Release();

        await routing.FlushAsync().WaitAsync(Safety);

        Assert.Equal(new ulong[] { 1, 2 }, authority.Executed.Select(static c => c.Actor));
        Assert.Equal(0, routing.Expired);
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
    public async Task A_link_lost_mid_send_keeps_that_command_at_the_head_and_sends_it_again_once_the_holder_is_reachable()
    {
        var authority = new SwitchableAuthority();
        authority.SetReachable(true);
        int calls = 0;
        authority.LoseLink = c =>
        {
            if (c.Actor == 1 && Interlocked.Increment(ref calls) == 1)
            {
                authority.SetReachable(false); // the holder died with the call in flight
                return true;
            }

            return false;
        };
        await using var routing = new ClusterGroupRouting(authority, "worker-a");
        routing.Forward(Command(GroupCommandKind.Invite, 1));
        routing.Forward(Command(GroupCommandKind.Login, 2));
        routing.Forward(Command(GroupCommandKind.Logout, 3));
        await authority.Probed.WaitAsync(Safety);
        Assert.Empty(authority.Executed);

        authority.SetReachable(true); // the new holder
        await routing.FlushAsync().WaitAsync(Safety);

        Assert.Equal(new ulong[] { 1, 2, 3 }, authority.Executed.Select(static c => c.Actor));
        Assert.Equal(0, routing.SendFailed);
        Assert.Equal(1, routing.SendRetried);
    }

    [Fact]
    public async Task A_send_that_fails_while_the_link_stays_open_is_sent_again_and_a_command_that_keeps_failing_is_dropped_after_the_age_limit()
    {
        var authority = new SwitchableAuthority();
        authority.SetReachable(true); // a hung holder: the link reads as open, every call times out
        authority.LoseLink = static c => c.Actor == 1;
        var time = new SteppedTimeProvider();
        await using var routing = new ClusterGroupRouting(authority, "worker-a", null, time);
        routing.Forward(Command(GroupCommandKind.Invite, 1));
        routing.Forward(Command(GroupCommandKind.Login, 2));
        using var timeout = new CancellationTokenSource(Safety);
        while (routing.SendRetried < 2)
        {
            await Task.Delay(10, timeout.Token); // it keeps the command and tries again
        }

        Assert.Empty(authority.Executed);
        Assert.Equal(0, routing.SendFailed);

        time.Advance(ClusterGroupRouting.MaxHeldCommandAge + TimeSpan.FromSeconds(10));
        await routing.FlushAsync().WaitAsync(Safety);

        Assert.Equal(1, routing.SendFailed);
        Assert.Equal(new ulong[] { 2 }, authority.Executed.Select(static c => c.Actor));
    }

    /// <summary>Fails the first send of actor 1 and takes the link down with it: the holder died with the call in flight.</summary>
    private static void DieDuringTheFirstSendOfActor1(SwitchableAuthority authority)
    {
        int calls = 0;
        authority.LoseLink = c =>
        {
            if (c.Actor == 1 && Interlocked.Increment(ref calls) == 1)
            {
                authority.SetReachable(false);
                return true;
            }

            return false;
        };
    }

    [Theory]
    [InlineData(GroupCommandKind.Leave)]
    [InlineData(GroupCommandKind.Kick)]
    public async Task A_head_command_that_failed_mid_send_and_sat_out_an_outage_past_the_age_limit_is_dropped_after_the_hold(GroupCommandKind kind)
    {
        var authority = new SwitchableAuthority();
        authority.SetReachable(true);
        DieDuringTheFirstSendOfActor1(authority);
        var time = new SteppedTimeProvider();
        var logger = new CapturingLogger();
        await using var routing = new ClusterGroupRouting(authority, "worker-a", logger, time);
        routing.Forward(Command(kind, 1));
        await authority.Probed.WaitAsync(Safety);
        time.Advance(ClusterGroupRouting.MaxHeldCommandAge + TimeSpan.FromSeconds(10));
        routing.Forward(Command(GroupCommandKind.Leave, 2)); // queued just now: it did not sit through this outage

        authority.SetReachable(true);
        await routing.FlushAsync().WaitAsync(Safety);

        Assert.Equal(new ulong[] { 2 }, authority.Executed.Select(static c => c.Actor));
        Assert.Equal(1, routing.Expired);
        Assert.Equal(0, routing.SendFailed);
        Assert.Contains(logger.AtLeast(LogLevel.Information), static l => l.Text.Contains("resume") && l.Text.Contains("1 older than 120 s"));
    }

    [Theory]
    [InlineData(GroupCommandKind.Login)]
    [InlineData(GroupCommandKind.Logout)]
    public async Task A_login_or_logout_that_failed_mid_send_and_sat_out_an_outage_past_the_age_limit_is_still_sent(GroupCommandKind kind)
    {
        var authority = new SwitchableAuthority();
        authority.SetReachable(true);
        DieDuringTheFirstSendOfActor1(authority);
        var time = new SteppedTimeProvider();
        await using var routing = new ClusterGroupRouting(authority, "worker-a", null, time);
        routing.Forward(Command(kind, 1));
        await authority.Probed.WaitAsync(Safety);
        time.Advance(ClusterGroupRouting.MaxHeldCommandAge + TimeSpan.FromSeconds(10));

        authority.SetReachable(true);
        await routing.FlushAsync().WaitAsync(Safety);

        Assert.Equal(new[] { kind }, authority.Executed.Select(static c => c.Kind));
        Assert.Equal(0, routing.Expired);
        Assert.Equal(0, routing.SendFailed);
    }

    [Theory]
    [InlineData(GroupCommandKind.Login)]
    [InlineData(GroupCommandKind.Logout)]
    public async Task A_login_or_logout_that_keeps_failing_on_an_open_link_is_never_dropped(GroupCommandKind kind)
    {
        var authority = new SwitchableAuthority();
        authority.SetReachable(true);
        bool hung = true;
        authority.LoseLink = _ => Volatile.Read(ref hung);
        var time = new SteppedTimeProvider();
        await using var routing = new ClusterGroupRouting(authority, "worker-a", null, time);
        routing.Forward(Command(kind, 1));
        await WaitForRetriesAsync(routing, 2);

        time.Advance(ClusterGroupRouting.MaxHeldCommandAge + TimeSpan.FromSeconds(10));
        long before = routing.SendRetried;
        await WaitForRetriesAsync(routing, before + 2); // past the limit, and still going
        Assert.Equal(0, routing.SendFailed);
        Assert.Equal(0, routing.Expired);

        Volatile.Write(ref hung, false);
        await routing.FlushAsync().WaitAsync(Safety);
        Assert.Equal(new[] { kind }, authority.Executed.Select(static c => c.Kind));
    }

    [Theory]
    [InlineData(GroupCommandKind.Login)]
    [InlineData(GroupCommandKind.Logout)]
    public async Task A_login_or_logout_stuck_at_the_head_past_the_age_limit_is_counted_and_logged_once(GroupCommandKind kind)
    {
        var authority = new SwitchableAuthority();
        authority.SetReachable(true);
        bool hung = true;
        authority.LoseLink = _ => Volatile.Read(ref hung);
        var time = new SteppedTimeProvider();
        var logger = new CapturingLogger();
        await using var routing = new ClusterGroupRouting(authority, "worker-a", logger, time);
        routing.Forward(Command(kind, 1));
        await WaitForRetriesAsync(routing, 2);
        Assert.Equal(0, routing.LoginLogoutStuckAtHead); // retried, but not yet past the limit

        time.Advance(ClusterGroupRouting.MaxHeldCommandAge + TimeSpan.FromSeconds(10));
        await WaitForRetriesAsync(routing, routing.SendRetried + 3); // several more failures past the limit
        Assert.Equal(1, routing.LoginLogoutStuckAtHead);
        Assert.Single(logger.AtLeast(LogLevel.Warning), static l => l.Text.Contains("head of the queue"));

        Volatile.Write(ref hung, false);
        await routing.FlushAsync().WaitAsync(Safety);
        Assert.Equal(1, routing.LoginLogoutStuckAtHead);
        Assert.Equal(new[] { kind }, authority.Executed.Select(static c => c.Kind));
    }

    [Fact]
    public async Task A_login_held_for_an_unreachable_holder_past_the_age_limit_is_counted_when_it_goes()
    {
        var authority = new SwitchableAuthority();
        var time = new SteppedTimeProvider();
        await using var routing = new ClusterGroupRouting(authority, "worker-a", null, time);
        routing.Forward(Command(GroupCommandKind.Logout, 1));
        await authority.Probed.WaitAsync(Safety);

        time.Advance(ClusterGroupRouting.MaxHeldCommandAge + TimeSpan.FromSeconds(10));
        authority.SetReachable(true);
        await routing.FlushAsync().WaitAsync(Safety);

        Assert.Equal(1, routing.LoginLogoutStuckAtHead);
        Assert.Equal(new[] { GroupCommandKind.Logout }, authority.Executed.Select(static c => c.Kind));
    }

    [Fact]
    public async Task A_command_that_can_expire_is_never_counted_as_stuck()
    {
        var authority = new SwitchableAuthority();
        var time = new SteppedTimeProvider();
        await using var routing = new ClusterGroupRouting(authority, "worker-a", null, time);
        routing.Forward(Command(GroupCommandKind.Leave, 1));
        await authority.Probed.WaitAsync(Safety);

        time.Advance(ClusterGroupRouting.MaxHeldCommandAge + TimeSpan.FromSeconds(10));
        authority.SetReachable(true);
        await routing.FlushAsync().WaitAsync(Safety);

        Assert.Equal(0, routing.LoginLogoutStuckAtHead);
        Assert.Equal(1, routing.Expired);
    }

    private static async Task WaitForRetriesAsync(ClusterGroupRouting routing, long atLeast)
    {
        using var timeout = new CancellationTokenSource(Safety);
        while (routing.SendRetried < atLeast)
        {
            await Task.Delay(5, timeout.Token);
        }
    }

    [Fact]
    public async Task A_hung_holder_is_one_failure_window_so_what_waited_behind_the_head_expires_without_a_send()
    {
        var authority = new SwitchableAuthority();
        authority.SetReachable(true);
        bool hung = true;
        authority.LoseLink = _ => Volatile.Read(ref hung);
        var time = new SteppedTimeProvider();
        var logger = new CapturingLogger();
        await using var routing = new ClusterGroupRouting(authority, "worker-a", logger, time);
        routing.Forward(Command(GroupCommandKind.Leave, 1));
        routing.Forward(Command(GroupCommandKind.Leave, 2));
        routing.Forward(Command(GroupCommandKind.Kick, 3));
        await WaitForRetriesAsync(routing, 1);

        time.Advance(ClusterGroupRouting.MaxHeldCommandAge + TimeSpan.FromSeconds(10));
        Task flushed = routing.FlushAsync(); // 1 gives up at its next failure; 2 and 3 are as old, so they go without a send
        await flushed.WaitAsync(Safety);
        Volatile.Write(ref hung, false);
        routing.Forward(Command(GroupCommandKind.Leave, 4)); // fresh: the holder is back
        await routing.FlushAsync().WaitAsync(Safety);

        Assert.Equal(1, routing.SendFailed);
        Assert.Equal(2, routing.Expired);
        Assert.DoesNotContain(authority.Attempts, static a => a.Actor is 2 or 3);
        Assert.Equal(new ulong[] { 4 }, authority.Executed.Select(static c => c.Actor));
        Assert.Single(logger.AtLeast(LogLevel.Information), static l => l.Text.Contains("are held"));
        Assert.Single(logger.AtLeast(LogLevel.Information), static l => l.Text.Contains("resume"));
    }

    [Fact]
    public async Task What_waited_behind_a_logout_on_a_hung_holder_expires_when_the_logout_finally_goes()
    {
        var authority = new SwitchableAuthority();
        authority.SetReachable(true);
        bool hung = true;
        authority.LoseLink = _ => Volatile.Read(ref hung);
        var time = new SteppedTimeProvider();
        await using var routing = new ClusterGroupRouting(authority, "worker-a", null, time);
        routing.Forward(Command(GroupCommandKind.Logout, 1));
        routing.Forward(Command(GroupCommandKind.Leave, 2));
        await WaitForRetriesAsync(routing, 1);

        time.Advance(ClusterGroupRouting.MaxHeldCommandAge + TimeSpan.FromSeconds(10));
        Volatile.Write(ref hung, false);
        await routing.FlushAsync().WaitAsync(Safety);

        Assert.Equal(new ulong[] { 1 }, authority.Executed.Select(static c => c.Actor));
        Assert.Equal(1, routing.Expired);
    }

    [Fact]
    public async Task A_retry_carries_the_id_of_the_first_attempt_and_every_command_has_its_own()
    {
        var authority = new SwitchableAuthority();
        authority.SetReachable(true);
        int failures = 0;
        authority.LoseLink = c => c.Actor == 1 && Interlocked.Increment(ref failures) <= 2;
        await using var routing = new ClusterGroupRouting(authority, "worker-a");
        routing.Forward(Command(GroupCommandKind.SwapSubGroup, 1));
        routing.Forward(Command(GroupCommandKind.SwapSubGroup, 2));
        await routing.FlushAsync().WaitAsync(Safety);

        long[] first = [.. authority.Attempts.Where(static a => a.Actor == 1).Select(static a => a.Id)];
        long second = Assert.Single(authority.Attempts, static a => a.Actor == 2).Id;
        Assert.Equal(3, first.Length);
        Assert.Single(first.Distinct());
        Assert.NotEqual(first[0], second);
        Assert.All(authority.Attempts, static a => Assert.True(a.Id > 0));
    }

    [Fact]
    public async Task A_link_that_vanished_between_the_check_and_the_call_is_not_read_as_a_refusal()
    {
        var authority = new SwitchableAuthority();
        authority.SetReachable(true); // reachable again by the time the failure is looked at: a redial, or a takeover here
        int calls = 0;
        authority.LinkVanished = c => c.Actor == 1 && Interlocked.Increment(ref calls) == 1;
        await using var routing = new ClusterGroupRouting(authority, "worker-a");
        routing.Forward(Command(GroupCommandKind.Leave, 1));
        await routing.FlushAsync().WaitAsync(Safety);

        Assert.Equal(new ulong[] { 1 }, authority.Executed.Select(static c => c.Actor));
        Assert.Equal(0, routing.SendFailed);
        Assert.Equal(1, routing.SendRetried);
    }

    [Fact]
    public async Task A_relay_whose_send_fails_is_dropped_not_retried()
    {
        var authority = new SwitchableAuthority();
        authority.SetReachable(true);
        authority.LoseLink = static c => c.Kind == GroupCommandKind.Relay;
        await using var routing = new ClusterGroupRouting(authority, "worker-a");
        routing.Forward(Command(GroupCommandKind.Relay, 1));
        routing.Forward(Command(GroupCommandKind.Login, 2));
        await routing.FlushAsync().WaitAsync(Safety);

        Assert.Equal(1, routing.SendFailed);
        Assert.Equal(0, routing.SendRetried);
        Assert.Equal(new ulong[] { 2 }, authority.Executed.Select(static c => c.Actor));
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
