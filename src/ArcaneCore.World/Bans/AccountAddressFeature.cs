using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Bans;

/// <summary>
/// Records the address each account enters the world from (vmangos realmd writes <c>account.last_ip</c> at logon), so
/// <c>.ban allip</c> can find the accounts last seen on an address. Every authenticated session is written through
/// <see cref="KeyedStoreWriteQueue{TStore}"/> (one key per account, newest wins) off the session thread; without an
/// <see cref="IAccountAddressStore"/> (no database) nothing is recorded and <c>.ban allip</c> finds nobody.
/// </summary>
public sealed class AccountAddressFeature(IServiceProvider services, IServiceScopeFactory scopes, ILoggerFactory loggers) : IWorldFeature
{
    private SessionRegistry? _registry;
    private TimeProvider _clock = TimeProvider.System;

    /// <summary>The write queue (tests and health reporting); null without a store.</summary>
    public KeyedStoreWriteQueue<IAccountAddressStore>? Writes { get; private set; }

    public void Attach(WorldRuntime world)
    {
        using (IServiceScope scope = scopes.CreateScope())
        {
            if (scope.ServiceProvider.GetService<IAccountAddressStore>() is null)
            {
                return;
            }
        }

        _registry = services.GetService<SessionRegistry>();
        if (_registry is null)
        {
            return;
        }

        _clock = services.GetService<TimeProvider>() ?? TimeProvider.System;
        Writes = new KeyedStoreWriteQueue<IAccountAddressStore>(scopes, loggers.CreateLogger<KeyedStoreWriteQueue<IAccountAddressStore>>(), "account address");
        Writes.Start();
        _registry.Registered += OnRegistered;
    }

    public Task StopAsync()
    {
        if (_registry is not null)
        {
            _registry.Registered -= OnRegistered;
        }

        return Writes?.StopAsync() ?? Task.CompletedTask;
    }

    private void OnRegistered(WorldSession session)
    {
        if (Writes is null || session.AccountId == 0 || session.RemoteAddress is not { } address)
        {
            return; // a placeholder endpoint (tests) has no address to record
        }

        int account = session.AccountId;
        long now = _clock.GetUtcNow().ToUnixTimeSeconds();
        Writes.Save("addr:" + account, store => store.RecordAsync(account, address, now));
    }
}
