using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ArcaneCore.Cryptography;
using ArcaneCore.Kernel.Accounts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ArcaneCore.World.Ops.Accounts;

/// <summary>
/// The PIN/TOTP second factor of an account, shared by every world-side login (the Atlas web login and remote admin) so the per-account
/// "newest TOTP step used" record is one table: a code spent on one login surface cannot be replayed on the other inside the +-1 step window
/// (vmangos AuthSocket.cpp:497-517 and ArcaneCore.Realm LogonSession judge the same PIN/TOTP). Registered as a singleton; the record is in
/// memory and per process. Thread safety: every member is safe to call concurrently.
/// </summary>
public sealed class SecondFactorChecker
{
    /// <summary>Longest code (PIN or TOTP digits) read.</summary>
    public const int MaxCodeLength = 32;

    private readonly ConcurrentDictionary<int, long> _lastTotpStep = new();

    /// <summary>Whether the account has a PIN or TOTP configured.</summary>
    public static bool HasFactor(Account account) => (account.LockFlags & (AccountLockFlags.FixedPin | AccountLockFlags.Totp)) != 0;

    /// <summary>
    /// Whether <paramref name="code"/> satisfies the account's second factor: a fixed PIN (checked first, as the realm does), else a TOTP code of the
    /// current 30 s step or one either side, never a step already used for this account. An account with neither flag takes any code (it is ignored).
    /// A TOTP step is not recorded here: the accepted step is returned in <paramref name="totpStep"/> (-1 for a PIN, no factor, or nothing accepted) and the
    /// caller claims it with <see cref="ClaimTotpStep"/> once every other check has passed.
    /// </summary>
    public bool CodeAccepted(Account account, string? code, TimeProvider time, out long totpStep)
    {
        totpStep = -1;
        bool pin = account.LockFlags.HasFlag(AccountLockFlags.FixedPin);
        if (!pin && !account.LockFlags.HasFlag(AccountLockFlags.Totp)) return true;
        if (code is not { Length: > 0 and <= MaxCodeLength }) return false;
        string typed = code.Trim();
        if (pin)
        {
            string stored = account.SecurityInfo;
            return stored.Length is >= 4 and <= 10 && stored.All(char.IsAsciiDigit)
                && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(typed), Encoding.UTF8.GetBytes(stored));
        }

        if (!Totp.TryDecodeSecret(account.SecurityInfo, out byte[] secret)) return false;
        long now = time.GetUtcNow().ToUnixTimeSeconds();
        _lastTotpStep.TryGetValue(account.Id, out long lastUsed);
        bool accepted = false;
        for (int offset = -1; offset <= 1; offset++)
        {
            long step = (now / 30) + offset;
            if (step < 0) continue;
            string expected = Totp.Generate(secret, now, offset).ToString("D6", CultureInfo.InvariantCulture);
            if (CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(typed), Encoding.UTF8.GetBytes(expected)) & step > lastUsed)
            {
                accepted = true;
                totpStep = step;
            }
        }

        return accepted;
    }

    /// <summary>Records <paramref name="step"/> as the account's newest used step; false when that step (or a newer one) was already used, so a replay loses.</summary>
    public bool ClaimTotpStep(int accountId, long step)
    {
        while (true)
        {
            if (!_lastTotpStep.TryGetValue(accountId, out long last))
            {
                if (_lastTotpStep.TryAdd(accountId, step)) return true;
                continue;
            }

            if (step <= last) return false;
            if (_lastTotpStep.TryUpdate(accountId, step, last)) return true;
        }
    }
}

/// <summary>Registers the shared <see cref="SecondFactorChecker"/>.</summary>
public static class SecondFactorCheckerRegistration
{
    /// <summary>Add the singleton once, whichever login surface registers first.</summary>
    public static IServiceCollection AddSecondFactorChecker(this IServiceCollection services)
    {
        services.TryAddSingleton<SecondFactorChecker>();
        return services;
    }
}
