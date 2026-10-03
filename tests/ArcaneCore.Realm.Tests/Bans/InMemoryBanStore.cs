using ArcaneCore.Kernel.Accounts;

namespace ArcaneCore.Realm.Tests.Bans;

/// <summary>An in-memory <see cref="IBanStore"/> with the retail row rules, for the realm tests.</summary>
internal sealed class InMemoryBanStore(TimeProvider? clock = null) : IBanStore
{
    private readonly object _gate = new();
    private readonly List<AccountBanRecord> _accountRows = [];
    private readonly List<IpBanRecord> _ipRows = [];
    private int _nextId = 1;

    /// <summary>When set, every read throws it (a database outage).</summary>
    public Exception? FailWith { get; set; }

    private long Now => (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();

    private void MaybeFail()
    {
        if (FailWith is not null)
        {
            throw FailWith;
        }
    }

    /// <summary>Insert a row exactly as given (an external writer such as SQL or another tool).</summary>
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
        lock (_gate)
        {
            return Task.FromResult(_accountRows.Where(r => r.AccountId == accountId && AccountBanEvaluator.IsActive(r, Now))
                .OrderByDescending(r => r.IsPermanent).ThenByDescending(r => r.UnbanDate).FirstOrDefault());
        }
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
        return Task.CompletedTask;
    }

    public Task<bool> BanIpAsync(IpBanRequest request, CancellationToken cancellationToken = default)
    {
        AddIpRow(request.Ip, Now, Now + request.DurationSeconds);
        return Task.FromResult(true);
    }

    public Task<bool> UnbanAccountAsync(int accountId, string source, string message, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            bool any = false;
            for (int i = 0; i < _accountRows.Count; i++)
            {
                if (_accountRows[i].AccountId == accountId && _accountRows[i].Active)
                {
                    any = true;
                    _accountRows[i] = _accountRows[i] with { Active = false };
                }
            }

            return Task.FromResult(any);
        }
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
            return Task.FromResult<IReadOnlyList<AccountBanRecord>>([.. _accountRows.Where(r => r.AccountId == accountId)]);
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
            return Task.FromResult<IReadOnlyList<IpBanRecord>>([.. _ipRows.Where(r => r.Ip.StartsWith(prefix, StringComparison.Ordinal))]);
        }
    }

    public Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
}
