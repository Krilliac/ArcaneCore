using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Realms;
using ArcaneCore.Protocol;
using ArcaneCore.World.RealmList;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Net;

/// <summary>
/// The player limit and the realm lock of the world, as vmangos keeps them in one signed number (<c>World::m_playerLimit</c>, World.h:947):
/// zero or more is a player-count limit (0 = no limit), below zero is a minimum security level, the negated level (World.h:695-696,
/// <c>GetPlayerAmountLimit</c> / <c>GetPlayerSecurityLimit</c>). Set by <c>.server plimit</c>; the default is
/// <see cref="RealmStatusOptions.PlayerLimit"/> (vmangos config <c>PlayerLimit</c>, default 100, World.cpp:486).
/// <list type="bullet">
/// <item><see cref="Check"/> is the admission test of <c>CMSG_AUTH_SESSION</c>: a lock above the account's security level is
/// AUTH_UNAVAILABLE (WorldSocket.cpp:349-358); a full world is refused to a plain player (accounts above Player skip the queue,
/// World.cpp:3183). vmangos parks the refused player in a login queue (World.cpp:316-322); ArcaneCore has no queue, so a full world
/// answers AUTH_UNAVAILABLE too and the client retries.</item>
/// <item>A change of the lock writes <c>realmlist.allowedSecurityLevel</c> through <see cref="IRealmStatusStore"/> (World.cpp:2914-2920),
/// off the calling thread, one write at a time, always the level in force when the write runs (so a burst of commands ends on the last one).
/// A failed write is logged and retried by the next change or by the next start; it never blocks the command.</item>
/// </list>
/// Thread safety: the limit is one volatile integer; every database call runs on a thread-pool thread under <c>_gate</c>.
/// </summary>
public sealed class WorldLoginAdmission
{
    /// <summary>vmangos clamps the limit at -SEC_ADMINISTRATOR (World.cpp:2910-2911).</summary>
    public const int MinimumLimit = -(int)AccountSecurity.Administrator;

    private readonly IServiceProvider? _services;
    private readonly ILogger? _logger;
    private readonly int _realmId;
    private readonly int _defaultLimit;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _limit;

    public WorldLoginAdmission(IServiceProvider services, IOptions<RealmStatusOptions> options, ILogger<WorldLoginAdmission> logger)
        : this(options.Value.PlayerLimit, options.Value.Id, services, logger)
    {
    }

    /// <summary>Test seam and host-less use: <paramref name="defaultLimit"/> in vmangos encoding; no <paramref name="services"/> means no realm-list write.</summary>
    public WorldLoginAdmission(int defaultLimit, int realmId, IServiceProvider? services = null, ILogger? logger = null)
    {
        _defaultLimit = Clamp(defaultLimit);
        _limit = _defaultLimit;
        _realmId = realmId;
        _services = services;
        _logger = logger;
    }

    /// <summary>The configured default (<c>.server plimit reset</c>).</summary>
    public int DefaultLimit => _defaultLimit;

    /// <summary>The limit in vmangos encoding: at least 0 is a player count, below 0 a negated minimum security level.</summary>
    public int Limit => Volatile.Read(ref _limit);

    /// <summary>The player-count limit; 0 = none (vmangos GetPlayerAmountLimit).</summary>
    public int AmountLimit => Math.Max(Limit, 0);

    /// <summary>The minimum security level to log in; <see cref="AccountSecurity.Player"/> = no lock (vmangos GetPlayerSecurityLimit).</summary>
    public AccountSecurity SecurityLimit => Limit < 0 ? (AccountSecurity)(-Limit) : AccountSecurity.Player;

    /// <summary>
    /// Set the limit (clamped at <see cref="MinimumLimit"/>). Returns true when the lock level changed, in which case
    /// <see cref="PublishLockAsync"/> has been started (World.cpp:2914: only a lock change is written to the database).
    /// </summary>
    public bool SetLimit(int limit)
    {
        limit = Clamp(limit);
        AccountSecurity before = SecurityLimit;
        Volatile.Write(ref _limit, limit);
        if (SecurityLimit == before)
        {
            return false;
        }

        _ = PublishLockAsync();
        return true;
    }

    /// <summary>
    /// The admission test: null admits, otherwise the refusal to send. <paramref name="otherActiveSessions"/> counts the authenticated
    /// sessions of other accounts (a reconnect of the same account replaces its old session and is not counted twice, World.cpp:311-314).
    /// </summary>
    public AuthResponseCode? Check(AccountSecurity security, int otherActiveSessions)
    {
        int limit = Limit;
        if (limit < 0)
        {
            return security < (AccountSecurity)(-limit) ? AuthResponseCode.Unavailable : null;
        }

        if (limit > 0 && otherActiveSessions >= limit && security == AccountSecurity.Player)
        {
            return AuthResponseCode.Unavailable;
        }

        return null;
    }

    /// <summary>
    /// Write the lock level in force to this realm's row. Safe on any thread, never throws; does nothing without a store, without a
    /// realm id, or when the row is not this world's to write.
    /// </summary>
    public Task PublishLockAsync()
        => _services is null || _realmId <= 0 ? Task.CompletedTask : Task.Run(WriteLockAsync);

    private async Task WriteLockAsync()
    {
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                byte level = (byte)SecurityLimit;
                await using AsyncServiceScope scope = _services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
                if (scope.ServiceProvider.GetService<IRealmStatusStore>() is not { } store)
                {
                    return;
                }

                if (!await store.SetAllowedSecurityLevelAsync(_realmId, level, CancellationToken.None).ConfigureAwait(false))
                {
                    _logger?.LogWarning("Realm {RealmId} has no row in the realm list, so its security lock {Level} was not written", _realmId, level);
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger?.LogFault("realm.lock-write", ex, "Writing the security lock of realm {RealmId} to the realm list failed; the next change or start writes it again", _realmId);
        }
    }

    private static int Clamp(int limit) => Math.Max(limit, MinimumLimit);
}
