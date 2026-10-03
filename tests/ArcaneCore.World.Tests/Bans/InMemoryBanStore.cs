using ArcaneCore.Kernel.Accounts;

namespace ArcaneCore.World.Tests.Bans;

/// <summary>
/// An in-memory <see cref="IBanStore"/> with the retail row rules. Mutations through the interface
/// raise <see cref="AccountStatusEvents"/> like the EF store; the <c>Add*Row</c> methods write a row
/// with no event, as an external writer (SQL, another process) would.
/// </summary>
internal sealed class InMemoryBanStore(AccountStatusEvents? events = null, TimeProvider? clock = null) : IBanStore
{
    private readonly object _gate = new();
    private readonly List<AccountBanRecord> _accountRows = [];
    private readonly List<IpBanRecord> _ipRows = [];
    private int _nextId = 1;
    private int _accountQueries;
    private int _chunkCalls;

    /// <summary>When set, every read throws it (a database outage).</summary>
    public Exception? FailWith { get; set; }

    /// <summary>Runs after the first single-account lookup has been answered (the status-read/Register race window).</summary>
    public Action? AfterFirstAccountQuery { get; set; }

    /// <summary>How many times <see cref="FindBannedAccountsAsync"/> was called (one per chunk).</summary>
    public int FindBannedAccountsCalls => Volatile.Read(ref _chunkCalls);

    /// <summary>Largest id list passed to <see cref="FindBannedAccountsAsync"/>.</summary>
    public int LargestChunk { get; private set; }

    private long Now => (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();

    private void MaybeFail()
    {
        if (FailWith is not null)
        {
            throw FailWith;
        }
    }

    public void AddAccountRow(int accountId, long banDate, long unbanDate, bool active = true)
    {
        lock (_gate)
        {
            _accountRows.Add(new AccountBanRecord(_nextId++, accountId, banDate, unbanDate, "ext", "ext", active, 1));
        }
    }

    public void AddIpRow(string ip, long banDate, long unbanDate)
    {
        lock (_gate)
        {
            _ipRows.RemoveAll(r => r.Ip == ip);
            _ipRows.Add(new IpBanRecord(ip, banDate, unbanDate, "ext", "ext"));
        }
    }

    public Task<AccountBanRecord?> GetActiveAccountBanAsync(int accountId, CancellationToken cancellationToken = default)
    {
        MaybeFail();
        AccountBanRecord? found;
        lock (_gate)
        {
            found = _accountRows.Where(r => r.AccountId == accountId && AccountBanEvaluator.IsActive(r, Now))
                .OrderByDescending(r => r.IsPermanent).ThenByDescending(r => r.UnbanDate).FirstOrDefault();
        }

        if (Interlocked.Increment(ref _accountQueries) == 1)
        {
            AfterFirstAccountQuery?.Invoke();
        }

        return Task.FromResult(found);
    }

    public Task<IpBanRecord?> GetActiveIpBanAsync(string ip, CancellationToken cancellationToken = default)
    {
        MaybeFail();
        lock (_gate)
        {
            return Task.FromResult(_ipRows.FirstOrDefault(r => r.Ip == ip && AccountBanEvaluator.IsActive(r, Now)));
        }
    }

    public Task<IReadOnlySet<int>> FindBannedAccountsAsync(IReadOnlyCollection<int> accountIds, CancellationToken cancellationToken = default)
    {
        MaybeFail();
        Interlocked.Increment(ref _chunkCalls);
        LargestChunk = Math.Max(LargestChunk, accountIds.Count);
        lock (_gate)
        {
            return Task.FromResult<IReadOnlySet<int>>(_accountRows
                .Where(r => accountIds.Contains(r.AccountId) && AccountBanEvaluator.IsActive(r, Now)).Select(r => r.AccountId).ToHashSet());
        }
    }

    public Task<IReadOnlySet<string>> FindBannedIpsAsync(IReadOnlyCollection<string> ips, CancellationToken cancellationToken = default)
    {
        MaybeFail();
        lock (_gate)
        {
            return Task.FromResult<IReadOnlySet<string>>(_ipRows
                .Where(r => ips.Contains(r.Ip) && AccountBanEvaluator.IsActive(r, Now)).Select(r => r.Ip).ToHashSet());
        }
    }

    public Task BanAccountAsync(BanRequest request, CancellationToken cancellationToken = default)
    {
        AddAccountRow(request.AccountId, Now, Now + request.DurationSeconds);
        events?.Publish(new AccountStatusChange(
            request.AccountId, request.DurationSeconds > 0 ? AccountStatus.Suspended : AccountStatus.Banned, request.AuthorAccountId));
        return Task.CompletedTask;
    }

    public Task<bool> BanIpAsync(IpBanRequest request, CancellationToken cancellationToken = default)
    {
        string ip = AccountBanEvaluator.NormalizeIp(request.Ip) ?? request.Ip;
        AddIpRow(ip, Now, Now + request.DurationSeconds);
        events?.Publish(new IpBanChange(ip, request.AuthorAccountId));
        return Task.FromResult(true);
    }

    public Task<bool> UnbanAccountAsync(int accountId, string source, string message, CancellationToken cancellationToken = default)
    {
        bool any = false;
        lock (_gate)
        {
            for (int i = 0; i < _accountRows.Count; i++)
            {
                if (_accountRows[i].AccountId == accountId && _accountRows[i].Active)
                {
                    any = true;
                    _accountRows[i] = _accountRows[i] with { Active = false };
                }
            }

            _accountRows.Add(new AccountBanRecord(_nextId++, accountId, Now, Now + 1, source, "UNBAN: " + message, false, 0));
        }

        if (any)
        {
            events?.Publish(new AccountStatusChange(accountId, AccountStatus.Active));
        }

        return Task.FromResult(any);
    }

    public Task<bool> UnbanIpAsync(string ip, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_ipRows.RemoveAll(r => r.Ip == ip) > 0);
        }
    }

    public Task<IReadOnlyList<AccountBanRecord>> GetHistoryAsync(int accountId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<AccountBanRecord>>([.. _accountRows.Where(r => r.AccountId == accountId).OrderBy(r => r.BanDate).ThenBy(r => r.BanId)]);
        }
    }

    public Task<IReadOnlyList<AccountBanRecord>> ListActiveAccountBansAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<AccountBanRecord>>([.. _accountRows.Where(r => AccountBanEvaluator.IsActive(r, Now))]);
        }
    }

    public Task<IReadOnlyList<IpBanRecord>> ListIpBansAsync(string prefix, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<IpBanRecord>>([.. _ipRows.Where(r => r.Ip.StartsWith(prefix, StringComparison.Ordinal) && AccountBanEvaluator.IsActive(r, Now))]);
        }
    }

    /// <summary>How many times <see cref="PurgeExpiredAsync"/> ran.</summary>
    public int PurgeCalls => Volatile.Read(ref _purgeCalls);

    private int _purgeCalls;

    public Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _purgeCalls);
        lock (_gate)
        {
            int removed = _ipRows.RemoveAll(r => !AccountBanEvaluator.IsActive(r, Now));
            for (int i = 0; i < _accountRows.Count; i++)
            {
                AccountBanRecord r = _accountRows[i];
                if (r.Active && r.UnbanDate <= Now && !r.IsPermanent)
                {
                    _accountRows[i] = r with { Active = false };
                    removed++;
                }
            }

            return Task.FromResult(removed);
        }
    }
}
