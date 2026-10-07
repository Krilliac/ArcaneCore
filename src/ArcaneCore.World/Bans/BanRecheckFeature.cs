using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Bans;

/// <summary>
/// Periodically re-checks the connected sessions against the ban rows, the IP bans and the status column, so a
/// ban written outside this process (SQL, AccountTool in another process, another realm daemon) is enforced
/// without waiting for the next login. This is an ArcaneCore extension: vmangos never kicks for an externally
/// written row. Its mangosd only reloads the IP-ban cache every BanListReloadTimer seconds and the account-ban
/// reload is commented out (AccountMgr.cpp:317-327, World.cpp:697). Off by default
/// (<see cref="BanOptions.RecheckIntervalSeconds"/> = 0).
/// <para>
/// Fail open for live sessions: a store error is logged and the pass retried on the next tick, nobody is kicked
/// on an error (a database blip must not disconnect the whole realm) and the timer never stops. Authentication
/// stays fail closed. Expired rows are purged at most hourly.
/// </para>
/// </summary>
public sealed class BanRecheckFeature(IServiceProvider services, ILogger<BanRecheckFeature> logger) : IWorldFeature
{
    private static readonly TimeSpan PurgeEvery = TimeSpan.FromHours(1);

    private readonly CancellationTokenSource _stop = new();
    private Task _loop = Task.CompletedTask;
    private SessionRegistry? _registry;
    private IServiceScopeFactory? _scopes;
    private TimeProvider _clock = TimeProvider.System;
    private DateTimeOffset _lastPurge = DateTimeOffset.MinValue;

    public void Attach(WorldRuntime world)
    {
        double seconds = services.GetService<IOptions<BanOptions>>()?.Value.RecheckIntervalSeconds ?? 0;
        _registry = services.GetService<SessionRegistry>();
        _scopes = services.GetService<IServiceScopeFactory>();
        _clock = services.GetService<TimeProvider>() ?? TimeProvider.System;
        if (seconds <= 0 || _registry is null || _scopes is null)
        {
            return; // retail: no re-check
        }

        TimeSpan interval = TimeSpan.FromSeconds(seconds);
        logger.LogInformation("Live ban re-check every {Interval}", interval);
        _loop = Task.Run(() => RunAsync(interval, _stop.Token));
    }

    public async Task StopAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
    }

    private async Task RunAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval, _clock);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await PassAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // fail open for live sessions; retry on the next tick
                    logger.LogWarning(ex, "Ban re-check failed; nobody was disconnected, retrying on the next tick");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    private async Task PassAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<WorldSession> sessions = _registry!.Sessions;
        if (sessions.Count == 0)
        {
            return;
        }

        int[] ids = [.. sessions.Select(s => s.AccountId)];
        string[] addresses = [.. sessions.Select(s => s.RemoteAddress).Where(a => a is not null).Distinct()!];

        await using AsyncServiceScope scope = _scopes!.CreateAsyncScope();
        IBanStore? bans = scope.ServiceProvider.GetService<IBanStore>();
        IAccountAdmin? admin = scope.ServiceProvider.GetService<IAccountAdmin>();

        // Every read completes before anything is kicked: an error in any of them means nobody is kicked.
        IReadOnlySet<int> bannedAccounts = bans is null ? new HashSet<int>() : await bans.FindBannedAccountsAsync(ids, cancellationToken).ConfigureAwait(false);
        IReadOnlySet<int> nonActive = admin is null ? new HashSet<int>() : await admin.FindNonActiveAsync(ids, cancellationToken).ConfigureAwait(false);
        IReadOnlySet<string> storedIps = bans is null || addresses.Length == 0 ? new HashSet<string>() : await bans.FindBannedIpsAsync(addresses, cancellationToken).ConfigureAwait(false);

        // The store returns the stored spelling, which a lenient collation (MariaDB PAD SPACE, case-insensitive hex)
        // can match although it differs from the canonical session address: compare canonical forms.
        var bannedIps = new HashSet<string>(storedIps.Select(ip => AccountBanEvaluator.NormalizeIp(ip) ?? ip.Trim()), StringComparer.Ordinal);

        foreach (WorldSession session in sessions)
        {
            if (bannedAccounts.Contains(session.AccountId) || nonActive.Contains(session.AccountId)
                || (session.RemoteAddress is { } address && bannedIps.Contains(address)))
            {
                logger.LogInformation("Re-check: disconnecting banned account {AccountId}", session.AccountId);
                session.Kick();
            }
        }

        if (bans is not null && _clock.GetUtcNow() - _lastPurge >= PurgeEvery)
        {
            await bans.PurgeExpiredAsync(cancellationToken).ConfigureAwait(false);
            _lastPurge = _clock.GetUtcNow();
        }
    }
}
