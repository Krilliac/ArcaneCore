using System.Collections.Concurrent;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Bans;

/// <summary>
/// Disconnects live sessions when a ban lands in this process (vmangos World::BanAccount, World.cpp:2469-2486:
/// LogoutPlayer(true) + KickPlayer). Subscribes to <see cref="AccountStatusEvents"/>: an account whose effective
/// status is no longer Active loses its session, and an IP ban drops every session from that address (retail
/// kicks accounts whose last_ip matches; ArcaneCore keeps no last_ip, so the live connection address is the
/// equivalent). The banning author's own account is skipped (World.cpp:2552-2553).
/// <para>
/// The kick is <see cref="WorldSession.Kick"/>, which is thread-safe (it cancels the read loop); the session's
/// normal close path then unregisters it and posts the player's removal and final save to the world thread, the
/// equivalent of LogoutPlayer(true). Everything is resolved lazily, so a host that does not register the ban
/// services (older seam tests) simply gets no enforcement. Events are in-process: a ban written by another
/// process is covered only by the optional periodic re-check.
/// </para>
/// </summary>
public sealed class BanEnforcementFeature(IServiceProvider services, ILogger<BanEnforcementFeature> logger) : IWorldFeature
{
    private readonly ConcurrentDictionary<Task, byte> _revocations = new();
    private SessionRegistry? _registry;
    private AccountStatusEvents? _events;
    private BanOptions _options = new();
    private IServiceScopeFactory? _scopes;

    public void Attach(WorldRuntime world)
    {
        _registry = services.GetService<SessionRegistry>();
        _events = services.GetService<AccountStatusEvents>();
        _scopes = services.GetService<IServiceScopeFactory>();
        _options = services.GetService<IOptions<BanOptions>>()?.Value ?? new BanOptions();
        if (_registry is null || _events is null)
        {
            logger.LogInformation("Live ban enforcement is inactive: no session registry or status events are registered");
            return;
        }

        _events.StatusChanged += OnStatusChanged;
        _events.IpBanned += OnIpBanned;
    }

    public async Task StopAsync()
    {
        if (_events is not null)
        {
            _events.StatusChanged -= OnStatusChanged;
            _events.IpBanned -= OnIpBanned;
        }

        await Task.WhenAll(_revocations.Keys.ToArray()).ConfigureAwait(false);
    }

    private void OnStatusChanged(AccountStatusChange change)
    {
        if (change.EffectiveStatus == AccountStatus.Active || change.ActorAccountId == change.AccountId)
        {
            return; // a lifted ban kicks nobody; the author is never kicked by their own ban
        }

        WorldSession? session = _registry?.Find(change.AccountId);
        if (session is null)
        {
            return;
        }

        logger.LogInformation("Disconnecting account {AccountId}: status is now {Status}", change.AccountId, change.EffectiveStatus);
        session.Kick();
        if (_options.RevokeSessionKeyOnBan)
        {
            TrackRevocation(change.AccountId);
        }
    }

    private void OnIpBanned(IpBanChange change)
    {
        if (_registry is null)
        {
            return;
        }

        // Session addresses are already canonical (AddressOfEndpoint); bring the published one to the same form so a
        // padded or IPv4-mapped spelling from any publisher still matches.
        string ip = AccountBanEvaluator.NormalizeIp(change.Ip) ?? change.Ip.Trim();
        foreach (WorldSession session in _registry.Sessions)
        {
            if (session.RemoteAddress == ip && session.AccountId != change.ActorAccountId)
            {
                logger.LogInformation("Disconnecting account {AccountId}: address {Ip} is banned", session.AccountId, change.Ip);
                session.Kick();
                if (_options.RevokeSessionKeyOnBan)
                {
                    TrackRevocation(session.AccountId);
                }
            }
        }
    }

    /// <summary>Null the stored key on a fresh scope; a failure is logged, never thrown into the publisher.</summary>
    private void TrackRevocation(int accountId)
    {
        if (_scopes is null)
        {
            return;
        }

        Task task = Task.Run(async () =>
        {
            try
            {
                await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
                IAccountAdmin? admin = scope.ServiceProvider.GetService<IAccountAdmin>();
                if (admin is not null)
                {
                    await admin.RevokeSessionKeyAsync(accountId).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not revoke the session key of banned account {AccountId}", accountId);
            }
        });
        _revocations[task] = 0;
        _ = task.ContinueWith(t => _revocations.TryRemove(t, out _), TaskScheduler.Default);
    }
}
