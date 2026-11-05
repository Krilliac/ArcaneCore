using ArcaneCore.Data.Characters.Cluster;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Gm;
using ArcaneCore.World.Cluster.Social;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Gm.Audit;

// The ticket queue follows the Social lease: a node that gains it at run time rereads the queue before any of its ticket writes reach storage.
public sealed partial class GmAuditFeature
{
    /// <summary>How long one reread of the ticket queue may take before it counts as failed and is tried again.</summary>
    private static readonly TimeSpan TicketReloadBudget = TimeSpan.FromSeconds(30);

    private readonly CancellationTokenSource _ticketReloadStop = new();
    private readonly List<HeldTicketChange> _heldTicketChanges = []; // guarded by _gate; oldest first, one entry per key
    private readonly HashSet<int> _heldTicketIds = []; // guarded by _gate; the tickets whose queued write only refuses itself
    private RealmAuthorityLeaseKeeper? _leaseKeeper;
    private WorldRuntime? _world;
    private Task? _ticketReload;
    private bool _ticketReloading; // world thread
    private long _ticketReloadNotBefore; // world thread: unix seconds; a failed read is tried again from then on

    // True from attach on a clustered node that did not hold the Social lease then, until the queue has been reread after gaining it. Changed at attach and under _gate; read without it on the world tick.
    private volatile bool _ticketsStale;

    /// <summary>A ticket change made while the queue is stale: <see cref="Replay"/> runs the same operation again on the reread queue. A newer change of the same <see cref="Key"/> replaces it, so the list stays about as small as the queue.</summary>
    private readonly record struct HeldTicketChange(string Key, Action Replay);

    /// <summary>
    /// World thread: whether the ticket queue is current for this node's holding of the Social lease. Always true on a node that held the lease at attach (or runs no keeper).
    /// On any other node what attach read is old: the holder has written since, and a whole-row save of it would turn a ticket the holder closed, answered or deleted back into what this node last saw.
    /// So until this is true no ticket write of this node reaches storage, not even with the lease held (<see cref="HoldTicketChange"/>). The first call that finds the lease held starts the read, off the world
    /// thread; the world tick makes that call. A failed read is tried again a second later and the writes stay held.
    /// </summary>
    internal bool EnsureTicketsCurrent()
    {
        if (!_ticketsStale)
        {
            return true;
        }

        if (!_ticketReloading && !_ticketReloadStop.IsCancellationRequested && NowUnixSeconds >= _ticketReloadNotBefore
            && _world is { } world && _leaseKeeper is { } keeper && keeper.Holds(RealmAuthorityScopes.Social))
        {
            _ticketReloading = true;
            _ticketReload = Task.Run(() => ReadTicketQueueAsync(world));
        }

        return false;
    }

    private void ReloadTicketsOnLeaseGain(uint diffMs) => EnsureTicketsCurrent();

    /// <summary>
    /// Call with <c>_gate</c> held, in the same lock as the cache change: remembers the change for <see cref="ApplyTicketReload"/> when the queue is stale. True when it did: the caller then queues
    /// <see cref="RefuseHeldTicketWrite"/> in place of the write, so the key stays retained and is named at stop exactly as a non-holder's refused write is, but cannot land when the lease arrives.
    /// </summary>
    private bool HoldTicketChange(string key, int ticketId, Action replay)
    {
        if (!_ticketsStale)
        {
            return false;
        }

        _heldTicketChanges.RemoveAll(change => change.Key == key);
        _heldTicketChanges.Add(new HeldTicketChange(key, replay));
        _heldTicketIds.Add(ticketId);
        return true;
    }

    /// <summary>The write queued for a held change. It never reaches the store, whatever the lease says by the time the queue runs it.</summary>
    private static Task RefuseHeldTicketWrite(IGmAuditStore store) => Task.FromException(new RealmLeaseNotHeldException(RealmAuthorityScopes.Social));

    private async Task ReadTicketQueueAsync(WorldRuntime world)
    {
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(_ticketReloadStop.Token);
            budget.CancelAfter(TicketReloadBudget);
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            IReadOnlyList<GmTicketRecord>? tickets = null;
            int maxTicketId = 0;
            if (scope.ServiceProvider.GetService<IGmAuditStore>() is { } store)
            {
                // As at attach: the rows first, the id seed after them, so every row seen is at or below the seed.
                tickets = await store.LoadOpenTicketsAsync(budget.Token).ConfigureAwait(false);
                maxTicketId = await store.GetMaxTicketIdAsync(budget.Token).ConfigureAwait(false);
            }

            world.Post(() => ApplyTicketReload(tickets, maxTicketId));
        }
        catch (OperationCanceledException) when (_ticketReloadStop.IsCancellationRequested)
        {
            // Stopping: the held writes stay retained and the stop names them.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            loggers.CreateLogger<GmAuditFeature>().LogFault(LogLevel.Warning, "gm.ticket-reload", ex, "Reading the GM ticket queue after gaining the social lease failed; ticket writes stay held and the read is tried again");
            world.Post(() =>
            {
                _ticketReloading = false;
                _ticketReloadNotBefore = NowUnixSeconds + 1;
            });
        }
    }

    /// <summary>
    /// World thread: storage replaces the ticket queue, then the changes held here run again on it, in order, as the normal operations. A held change that no longer applies (the previous holder closed or deleted the
    /// ticket, or the character already has a stored open ticket) does nothing, so it reverts nothing. Every write queued while stale is replaced first, so none stays retained. In one lock, so no other thread sees the
    /// queue between the two steps. With no store the cache is all there is: it stays, and the held changes are already in it.
    /// </summary>
    private void ApplyTicketReload(IReadOnlyList<GmTicketRecord>? stored, int maxTicketId)
    {
        _ticketReloading = false;
        if (_ticketReloadStop.IsCancellationRequested)
        {
            return;
        }

        int replayed;
        lock (_gate)
        {
            HeldTicketChange[] held = [.. _heldTicketChanges];
            _heldTicketChanges.Clear();
            int[] heldIds = [.. _heldTicketIds];
            _heldTicketIds.Clear();
            _ticketsStale = false;
            foreach (int id in heldIds)
            {
                Writes.Save(TicketKey(id), static _ => Task.CompletedTask);
            }

            if (stored is null)
            {
                return;
            }

            _tickets.Clear();
            foreach (GmTicketRecord ticket in stored)
            {
                _tickets[ticket.Id] = ticket;
            }

            _lastTicketId = Math.Max(_lastTicketId, Math.Max(maxTicketId, stored.Count == 0 ? 0 : stored.Max(t => t.Id)));
            foreach (HeldTicketChange change in held)
            {
                change.Replay();
            }

            replayed = held.Length;
        }

        loggers.CreateLogger<GmAuditFeature>().LogInformation("GM ticket queue reloaded after gaining the social lease: {Tickets} open tickets, {Held} held changes run again", stored.Count, replayed);
    }

    /// <summary>A ticket filed here while the queue was stale, on the reread queue. The stored ticket wins when the character has one by now: one open ticket per character, and it is the one the previous holder accepted.</summary>
    private void RestoreHeldTicket(GmTicketRecord ticket)
    {
        lock (_gate)
        {
            if (_tickets.Values.Any(t => t.CharacterId == ticket.CharacterId))
            {
                loggers.CreateLogger<GmAuditFeature>().LogWarning(
                    "GM ticket {Ticket} filed here before the social lease arrived is dropped: character {Character} already has a stored open ticket", ticket.Id, ticket.CharacterId);
                return;
            }

            _tickets[ticket.Id] = ticket;
        }

        TouchQueue();
        PersistTicket(ticket, held: false);
    }

    private async Task StopTicketReloadAsync()
    {
        await _ticketReloadStop.CancelAsync().ConfigureAwait(false);
        if (_ticketReload is { } reload)
        {
            await reload.ConfigureAwait(false); // never faults: ReadTicketQueueAsync handles every failure it can meet
        }
    }
}
