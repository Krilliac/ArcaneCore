using ArcaneCore.Data.Characters.Cluster;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Kernel.Gm;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Social;
using ArcaneCore.World.Gm.Audit;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Tests.Cluster.Social;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Audit;

/// <summary>
/// The GM ticket queue is the Social lease holder's. A worker that takes the lease over at run time rereads the queue before any of its ticket writes reach storage, so what it read at attach
/// never reverts what the dead holder wrote. The store here refuses as the real one does (<see cref="LeaseFencedWrite.RequireFence"/>), so a write that reaches it after the takeover would pass.
/// </summary>
public sealed class GmTicketTakeoverTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private static GmTicketRecord Ticket(int id, int character, string text = "help")
        => new(id, character, text, 0, 0, 0f, 0f, 0f, 100, 100, GmTicketStatus.Open, string.Empty, string.Empty, 0);

    private static GmTicketRecord ClosedByHolder(GmTicketRecord ticket)
        => ticket with { Status = GmTicketStatus.Closed, Response = "done", ClosedBy = "Holder", ClosedAt = 200, UpdatedAt = 200 };

    [Fact]
    public async Task Survivor_sees_what_the_dead_holder_wrote_and_cannot_revert_or_duplicate_it()
    {
        await using Rig rig = await Rig.CreateAsync(Ticket(1, 1), Ticket(2, 2), Ticket(5, 5));

        // After this node attached, the holder closed ticket 1, deleted ticket 2, answered ticket 5 and accepted ticket 3.
        rig.Rows.Seed(ClosedByHolder(Ticket(1, 1)));
        await rig.Rows.DeleteTicketAsync(2);
        rig.Rows.Seed(Ticket(5, 5) with { Response = "answered", UpdatedAt = 200 });
        rig.Rows.Seed(Ticket(3, 3, "from the holder"));

        rig.TakeOver();
        await rig.WaitUntilCurrentAsync();

        await rig.OnWorld(() =>
        {
            Assert.Null(rig.Audit.OpenTicket(1));
            Assert.Null(rig.Audit.OpenTicket(2));
            Assert.Equal("answered", rig.Audit.OpenTicket(5)!.Response);
            Assert.Equal("from the holder", rig.Audit.OpenTicketOf(3)!.Text);

            // The operations that would have written a stale row refuse now, as they do for any ticket that is not open.
            Assert.Null(rig.Audit.UpdateTicketText(1, "stale", out bool changed));
            Assert.False(changed);
            Assert.Null(rig.Audit.RespondToTicket(1, "stale"));
            Assert.Null(rig.Audit.CloseTicket(2, "Gm", "stale"));
            Assert.Null(rig.Audit.CreateTicket(3, "a duplicate", 0, 0, 0f, 0f, 0f));
            return true;
        });

        await rig.Audit.Writes.FlushAsync();
        Assert.Equal(0, rig.Store.TicketWrites);
        Assert.Equal(GmTicketStatus.Closed, rig.Rows.Ticket(1)!.Status);
        Assert.Equal("done", rig.Rows.Ticket(1)!.Response);
        Assert.Null(rig.Rows.Ticket(2));

        // Normal work from here on, and ids continue past the holder's.
        GmTicketRecord filed = await rig.OnWorld(() => rig.Audit.CreateTicket(9, "new", 0, 0, 0f, 0f, 0f)!);
        Assert.Equal(6, filed.Id);
        await rig.Audit.Writes.FlushAsync();
        Assert.Equal("new", rig.Rows.Ticket(6)!.Text);
    }

    [Fact]
    public async Task Ticket_writes_wait_for_the_reload_with_the_lease_held_and_then_run_on_the_stored_rows()
    {
        await using Rig rig = await Rig.CreateAsync(Ticket(1, 1), Ticket(2, 2), Ticket(3, 3));
        rig.Rows.Seed(ClosedByHolder(Ticket(1, 1)));
        rig.Rows.Seed(Ticket(2, 2) with { Response = "answered", UpdatedAt = 200 });

        rig.Store.HoldLoads();
        rig.TakeOver();
        Assert.False(await rig.OnWorld(rig.Audit.EnsureTicketsCurrent));
        await rig.Store.LoadEntered.WaitAsync(Budget);

        // The lease is held and the read is still running: the store's fence would let every one of these through.
        Assert.NotNull(LeaseFencedWrite.RequireFence(rig.Keeper, RealmAuthorityScopes.Social));
        GmTicketRecord filed = await rig.OnWorld(() =>
        {
            Assert.NotNull(rig.Audit.RespondToTicket(1, "late answer"));
            Assert.NotNull(rig.Audit.UpdateTicketText(2, "more detail", out bool changed));
            Assert.True(changed);
            Assert.NotNull(rig.Audit.CloseTicket(3, "Gm", "bye"));
            return rig.Audit.CreateTicket(4, "filed during the read", 0, 0, 0f, 0f, 0f)!;
        });

        await rig.Audit.Writes.FlushAsync();
        Assert.Equal(0, rig.Store.TicketWrites);
        Assert.Equal(new[] { "ticket:1", "ticket:2", "ticket:3", "ticket:" + filed.Id }, rig.Audit.Writes.RetainedKeys);

        rig.Store.ReleaseLoads();
        await rig.WaitUntilCurrentAsync();
        await rig.Audit.Writes.FlushAsync();

        Assert.Empty(rig.Audit.Writes.RetainedKeys);

        // A change to a ticket the holder closed does nothing; the others run on the stored row, so neither side's edit is lost.
        Assert.Equal(GmTicketStatus.Closed, rig.Rows.Ticket(1)!.Status);
        Assert.Equal("done", rig.Rows.Ticket(1)!.Response);
        Assert.Equal("more detail", rig.Rows.Ticket(2)!.Text);
        Assert.Equal("answered", rig.Rows.Ticket(2)!.Response);
        Assert.Equal(GmTicketStatus.Closed, rig.Rows.Ticket(3)!.Status);
        Assert.Equal("Gm", rig.Rows.Ticket(3)!.ClosedBy);
        Assert.Equal("filed during the read", rig.Rows.Ticket(filed.Id)!.Text);

        await rig.OnWorld(() =>
        {
            Assert.Null(rig.Audit.OpenTicket(1));
            Assert.Equal("answered", rig.Audit.OpenTicket(2)!.Response);
            Assert.Null(rig.Audit.OpenTicket(3));
            Assert.Equal(filed.Id, rig.Audit.OpenTicketOf(4)!.Id);
            return true;
        });
    }

    [Fact]
    public async Task Write_retained_before_the_takeover_does_not_land_when_the_lease_arrives()
    {
        await using Rig rig = await Rig.CreateAsync(Ticket(1, 1));

        // Made while another node holds the lease: refused and retained.
        await rig.OnWorld(() => rig.Audit.RespondToTicket(1, "from the old cache"));
        await rig.Audit.Writes.FlushAsync();
        Assert.Equal(new[] { "ticket:1" }, rig.Audit.Writes.RetainedKeys);

        rig.Rows.Seed(ClosedByHolder(Ticket(1, 1)));
        rig.Store.HoldLoads();
        rig.TakeOver();
        Assert.False(await rig.OnWorld(rig.Audit.EnsureTicketsCurrent));
        await rig.Store.LoadEntered.WaitAsync(Budget);

        // The periodic retry of a retained write, with the lease held and the fence open.
        await rig.Audit.Writes.RetryRetainedAsync();
        Assert.Equal(0, rig.Store.TicketWrites);

        rig.Store.ReleaseLoads();
        await rig.WaitUntilCurrentAsync();
        await rig.Audit.Writes.FlushAsync();

        Assert.Equal(0, rig.Store.TicketWrites);
        Assert.Empty(rig.Audit.Writes.RetainedKeys);
        Assert.Equal(GmTicketStatus.Closed, rig.Rows.Ticket(1)!.Status);
        Assert.Equal("done", rig.Rows.Ticket(1)!.Response);
    }

    [Fact]
    public async Task Ticket_filed_here_before_the_takeover_is_stored_after_it_unless_the_holder_took_one_for_that_character()
    {
        await using Rig rig = await Rig.CreateAsync();
        (GmTicketRecord kept, GmTicketRecord dropped) = await rig.OnWorld(() =>
            (rig.Audit.CreateTicket(6, "only here", 0, 0, 0f, 0f, 0f)!, rig.Audit.CreateTicket(7, "also here", 0, 0, 0f, 0f, 0f)!));
        rig.Rows.Seed(Ticket(50, 7, "at the holder"));

        rig.TakeOver();
        await rig.WaitUntilCurrentAsync();
        await rig.Audit.Writes.FlushAsync();

        Assert.Equal("only here", rig.Rows.Ticket(kept.Id)!.Text);
        Assert.Null(rig.Rows.Ticket(dropped.Id));
        Assert.Empty(rig.Audit.Writes.RetainedKeys);
        await rig.OnWorld(() =>
        {
            Assert.Equal(50, rig.Audit.OpenTicketOf(7)!.Id);
            Assert.Null(rig.Audit.OpenTicket(dropped.Id));
            return true;
        });
    }

    [Fact]
    public async Task Failed_reload_keeps_the_writes_held_and_is_tried_again()
    {
        await using Rig rig = await Rig.CreateAsync(Ticket(1, 1));
        await rig.OnWorld(() => rig.Audit.RespondToTicket(1, "from the old cache"));
        rig.Rows.Seed(ClosedByHolder(Ticket(1, 1)));

        // The first read fails; the retry is held, so the node stays in this state however fast the retry comes.
        rig.Store.FailNextLoad();
        rig.Store.HoldLoads();
        rig.TakeOver();
        Assert.False(await rig.OnWorld(rig.Audit.EnsureTicketsCurrent));
        await rig.Store.LoadFailed.WaitAsync(Budget);
        await rig.WaitForLoadRetryAsync();
        Assert.False(await rig.OnWorld(rig.Audit.EnsureTicketsCurrent));
        await rig.Audit.Writes.FlushAsync();
        Assert.Equal(0, rig.Store.TicketWrites);

        rig.Store.ReleaseLoads();
        await rig.WaitUntilCurrentAsync();
        await rig.Audit.Writes.FlushAsync();
        Assert.Equal(0, rig.Store.TicketWrites);
        Assert.Equal("done", rig.Rows.Ticket(1)!.Response);
    }

    [Fact]
    public async Task Node_that_never_gains_the_lease_keeps_its_ticket_writes_out_of_storage_and_names_them_at_stop()
    {
        await using Rig rig = await Rig.CreateAsync();
        GmTicketRecord filed = await rig.OnWorld(() => rig.Audit.CreateTicket(1, "here", 0, 0, 0f, 0f, 0f)!);
        await rig.Audit.Writes.FlushAsync();
        Assert.Equal(new[] { "ticket:" + filed.Id }, rig.Audit.Writes.RetainedKeys);
        Assert.False(await rig.OnWorld(rig.Audit.EnsureTicketsCurrent));

        rig.World.Dispose();
        InvalidOperationException stop = await Assert.ThrowsAsync<InvalidOperationException>(rig.Audit.StopAsync);
        Assert.Contains("ticket:" + filed.Id, stop.Message);
        Assert.Equal(0, rig.Store.TicketWrites);
        Assert.Equal(1, rig.Store.Loads); // the attach read only: no reload without the lease
    }

    [Fact]
    public async Task Holder_at_attach_writes_straight_through_and_never_rereads()
    {
        await using Rig rig = await Rig.CreateAsync(true, Ticket(1, 1));
        Assert.True(await rig.OnWorld(rig.Audit.EnsureTicketsCurrent));

        await rig.OnWorld(() => rig.Audit.RespondToTicket(1, "answered"));
        await rig.Audit.Writes.FlushAsync();

        Assert.Equal(1, rig.Store.TicketWrites);
        Assert.Equal("answered", rig.Rows.Ticket(1)!.Response);
        Assert.Equal(1, rig.Store.Loads);
    }

    /// <summary>The queue's store as a worker sees it: refused without the Social fence, counted when a ticket write gets through, and a load that can be held or failed.</summary>
    private sealed class FencedTicketStore(InMemoryGmAuditStore rows, IRealmLeaseFenceSource fences) : IGmAuditStore
    {
        private readonly TaskCompletionSource _loadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _loadFailed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource? _release;
        private int _failLoads;
        private int _ticketWrites;
        private int _loads;

        public int TicketWrites => Volatile.Read(ref _ticketWrites);

        public int Loads => Volatile.Read(ref _loads);

        public Task LoadEntered => _loadEntered.Task;

        public Task LoadFailed => _loadFailed.Task;

        public void HoldLoads() => Volatile.Write(ref _release, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        public void ReleaseLoads() => Volatile.Read(ref _release)!.TrySetResult();

        public void FailNextLoad() => Volatile.Write(ref _failLoads, 1);

        public async Task<IReadOnlyList<GmTicketRecord>> LoadOpenTicketsAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _loads);
            if (Interlocked.Exchange(ref _failLoads, 0) == 1)
            {
                _loadFailed.TrySetResult();
                throw new IOException("ticket read unavailable");
            }

            if (Volatile.Read(ref _release) is { } release)
            {
                _loadEntered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }

            return await rows.LoadOpenTicketsAsync(cancellationToken);
        }

        public Task<int> GetMaxTicketIdAsync(CancellationToken cancellationToken = default) => rows.GetMaxTicketIdAsync(cancellationToken);

        public Task SaveTicketAsync(GmTicketRecord ticket, CancellationToken cancellationToken = default)
        {
            LeaseFencedWrite.RequireFence(fences, RealmAuthorityScopes.Social);
            Interlocked.Increment(ref _ticketWrites);
            return rows.SaveTicketAsync(ticket, cancellationToken);
        }

        public Task DeleteTicketAsync(int ticketId, CancellationToken cancellationToken = default)
        {
            LeaseFencedWrite.RequireFence(fences, RealmAuthorityScopes.Social);
            Interlocked.Increment(ref _ticketWrites);
            return rows.DeleteTicketAsync(ticketId, cancellationToken);
        }

        public Task<IReadOnlyList<AccountMuteRecord>> LoadActiveMutesAsync(long nowUnix, CancellationToken cancellationToken = default)
            => rows.LoadActiveMutesAsync(nowUnix, cancellationToken);

        public Task SaveMuteAsync(AccountMuteRecord mute, CancellationToken cancellationToken = default) => rows.SaveMuteAsync(mute, cancellationToken);

        public Task DeleteMuteAsync(int accountId, CancellationToken cancellationToken = default) => rows.DeleteMuteAsync(accountId, cancellationToken);

        public Task<int> DeleteExpiredMutesAsync(long nowUnix, CancellationToken cancellationToken = default) => rows.DeleteExpiredMutesAsync(nowUnix, cancellationToken);
    }

    /// <summary>A map worker (worker-a) whose Social scope is held by worker-b, which never renews it, unless <c>holdsSocial</c> says this node took it first.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly ManualTimeProvider _time;
        private readonly ServiceProvider _services;

        private Rig(ManualTimeProvider time, RealmAuthorityLeaseKeeper keeper, InMemoryGmAuditStore rows, FencedTicketStore store, ServiceProvider services)
        {
            _time = time;
            _services = services;
            Keeper = keeper;
            Rows = rows;
            Store = store;
            World = new WorldRuntime(new WorldRuntimeOptions { TickIntervalMs = 5, AutosaveIntervalMs = 0 },
                services.GetRequiredService<CharacterSaveQueue>(), NullLogger<WorldRuntime>.Instance);
            Audit = new GmAuditFeature(services.GetRequiredService<IServiceScopeFactory>(), NullLoggerFactory.Instance);
            Audit.Attach(World);
            World.Start();
        }

        public RealmAuthorityLeaseKeeper Keeper { get; }

        public InMemoryGmAuditStore Rows { get; }

        public FencedTicketStore Store { get; }

        public WorldRuntime World { get; }

        public GmAuditFeature Audit { get; }

        public static Task<Rig> CreateAsync(params GmTicketRecord[] stored) => CreateAsync(false, stored);

        public static async Task<Rig> CreateAsync(bool holdsSocial, params GmTicketRecord[] stored)
        {
            var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero));
            var leases = new InMemoryRealmAuthorityLeaseStore();
            if (!holdsSocial)
            {
                leases.TryAcquire(RealmAuthorityScopes.Social, "worker-b", Guid.NewGuid(), TimeSpan.FromSeconds(30), time.GetUtcNow());
            }

            var keeper = new RealmAuthorityLeaseKeeper(leases,
                Microsoft.Extensions.Options.Options.Create(new ClusterNodeOptions { Role = ClusterNodeRole.MapWorker, AuthorityLeaseTtlSeconds = 30, AuthorityLeaseRenewSeconds = 10 }),
                Microsoft.Extensions.Options.Options.Create(new GatewayWorkerOptions { Enabled = true, NodeId = "worker-a" }),
                NullLogger<RealmAuthorityLeaseKeeper>.Instance, null, time, _ => { });
            await keeper.AcquireAtStartAsync(Guid.NewGuid(), CancellationToken.None);
            Assert.Equal(holdsSocial, keeper.Holds(RealmAuthorityScopes.Social));

            var rows = new InMemoryGmAuditStore();
            foreach (GmTicketRecord ticket in stored)
            {
                rows.Seed(ticket);
            }

            var store = new FencedTicketStore(rows, keeper);
            ServiceProvider services = new ServiceCollection()
                .AddSingleton(keeper)
                .AddSingleton<IGmAuditStore>(store)
                .AddSingleton(provider => new CharacterSaveQueue(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance))
                .BuildServiceProvider();
            return new Rig(time, keeper, rows, store, services);
        }

        /// <summary>worker-b's lease lapses at 30 s, and the keeper's renewal tick takes it over.</summary>
        public void TakeOver()
        {
            for (int i = 0; i < 4; i++)
            {
                _time.Advance(TimeSpan.FromSeconds(10));
            }

            Assert.True(Keeper.Holds(RealmAuthorityScopes.Social));
        }

        public Task<T> OnWorld<T>(Func<T> action) => World.InvokeAsync(action).WaitAsync(Budget);

        public async Task WaitUntilCurrentAsync()
        {
            using var timeout = new CancellationTokenSource(Budget);
            while (!await OnWorld(Audit.EnsureTicketsCurrent).WaitAsync(timeout.Token))
            {
                await Task.Delay(10, timeout.Token);
            }
        }

        /// <summary>Keeps asking until the failed read has been tried again (the retry then waits at the store's hold).</summary>
        public async Task WaitForLoadRetryAsync()
        {
            using var timeout = new CancellationTokenSource(Budget);
            while (!Store.LoadEntered.IsCompleted)
            {
                await OnWorld(Audit.EnsureTicketsCurrent).WaitAsync(timeout.Token);
                await Task.Delay(10, timeout.Token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            World.Dispose();
            try
            {
                await Audit.StopAsync();
            }
            catch (InvalidOperationException)
            {
                // A write held on a node that never gained the lease is named at stop; the test that cares asserts it.
            }

            await _services.GetRequiredService<CharacterSaveQueue>().StopAsync();
            await _services.DisposeAsync();
            await Keeper.DisposeAsync();
        }
    }
}
