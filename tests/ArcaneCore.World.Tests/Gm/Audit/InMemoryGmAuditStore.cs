using ArcaneCore.Kernel.Gm;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Tests.Gm.Audit;

/// <summary>An in-memory <see cref="IGmAuditStore"/> (thread-safe) whose next writes can be made to fail.</summary>
internal sealed class InMemoryGmAuditStore : IGmAuditStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<int, AccountMuteRecord> _mutes = [];
    private readonly Dictionary<int, GmTicketRecord> _tickets = [];
    private int _failures;
    private int _attempts;

    /// <summary>Make the next <paramref name="count"/> write calls throw.</summary>
    public void FailNext(int count) => Interlocked.Exchange(ref _failures, count);

    /// <summary>Write calls made so far, failed ones included.</summary>
    public int Attempts => Volatile.Read(ref _attempts);

    public AccountMuteRecord? Mute(int accountId)
    {
        lock (_lock)
        {
            return _mutes.GetValueOrDefault(accountId);
        }
    }

    public GmTicketRecord? Ticket(int id)
    {
        lock (_lock)
        {
            return _tickets.GetValueOrDefault(id);
        }
    }

    public IReadOnlyList<GmTicketRecord> Tickets()
    {
        lock (_lock)
        {
            return [.. _tickets.Values.OrderBy(t => t.Id)];
        }
    }

    /// <summary>Seed a stored row, as a previous run would have left it.</summary>
    public void Seed(AccountMuteRecord mute)
    {
        lock (_lock)
        {
            _mutes[mute.AccountId] = mute;
        }
    }

    public void Seed(GmTicketRecord ticket)
    {
        lock (_lock)
        {
            _tickets[ticket.Id] = ticket;
        }
    }

    private void Gate()
    {
        Interlocked.Increment(ref _attempts);
        if (Interlocked.Decrement(ref _failures) >= 0)
        {
            throw new InvalidOperationException("injected GM audit store failure");
        }

        Interlocked.Exchange(ref _failures, 0);
    }

    public Task<IReadOnlyList<AccountMuteRecord>> LoadActiveMutesAsync(long nowUnix, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<AccountMuteRecord>>([.. _mutes.Values.Where(m => m.MutedUntil > nowUnix)]);
        }
    }

    public Task SaveMuteAsync(AccountMuteRecord mute, CancellationToken cancellationToken = default)
    {
        Gate();
        lock (_lock)
        {
            _mutes[mute.AccountId] = mute;
        }

        return Task.CompletedTask;
    }

    public Task DeleteMuteAsync(int accountId, CancellationToken cancellationToken = default)
    {
        Gate();
        lock (_lock)
        {
            _mutes.Remove(accountId);
        }

        return Task.CompletedTask;
    }

    public Task<int> DeleteExpiredMutesAsync(long nowUnix, CancellationToken cancellationToken = default)
    {
        Gate();
        lock (_lock)
        {
            int[] expired = [.. _mutes.Values.Where(m => m.MutedUntil <= nowUnix).Select(m => m.AccountId)];
            foreach (int accountId in expired)
            {
                _mutes.Remove(accountId);
            }

            return Task.FromResult(expired.Length);
        }
    }

    public Task<IReadOnlyList<GmTicketRecord>> LoadOpenTicketsAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<GmTicketRecord>>([.. _tickets.Values.Where(t => t.Status == GmTicketStatus.Open).OrderBy(t => t.Id)]);
        }
    }

    public Task<int> GetMaxTicketIdAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_tickets.Count == 0 ? 0 : _tickets.Keys.Max());
        }
    }

    public Task SaveTicketAsync(GmTicketRecord ticket, CancellationToken cancellationToken = default)
    {
        Gate();
        lock (_lock)
        {
            _tickets[ticket.Id] = ticket;
        }

        return Task.CompletedTask;
    }

    public Task DeleteTicketAsync(int ticketId, CancellationToken cancellationToken = default)
    {
        Gate();
        lock (_lock)
        {
            _tickets.Remove(ticketId);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Registers one <see cref="InMemoryGmAuditStore"/> per test host as the lane's store.</summary>
internal sealed class GmAuditTestServices : IWorldTestServices
{
    public void Register(IServiceCollection services)
    {
        var store = new InMemoryGmAuditStore();
        services.AddSingleton(store);
        services.AddSingleton<IGmAuditStore>(store);
    }
}
