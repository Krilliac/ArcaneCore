using System.Security.Cryptography;
using ArcaneCore.Cryptography;
using ArcaneCore.Kernel.Accounts;

namespace ArcaneCore.World.Ops.Accounts;

/// <summary>Why <see cref="AccountPasswordCheck"/> refused a name and password, for the daemon's own log; a caller never shows it to the person logging in.</summary>
internal enum AccountCheckOutcome : byte
{
    /// <summary>The name and password are right and the account may log in.</summary>
    Ok,

    /// <summary>No such account, or the password is wrong (the two take the same time and are not told apart).</summary>
    UnknownOrWrongPassword,

    /// <summary>The password is right but the account's status column is not Active.</summary>
    NotActive,

    /// <summary>The password is right but a ban row is in force on the account.</summary>
    Banned,
}

/// <summary>The answer of <see cref="AccountPasswordCheck.CheckAsync"/>.</summary>
/// <param name="Outcome">What was decided.</param>
/// <param name="Account">The account, only when <paramref name="Outcome"/> is <see cref="AccountCheckOutcome.Ok"/>; otherwise null.</param>
internal readonly record struct AccountCheckResult(AccountCheckOutcome Outcome, Account? Account)
{
    /// <summary>The name and password are right and the account is active and unbanned.</summary>
    public bool Ok => Outcome == AccountCheckOutcome.Ok;
}

/// <summary>
/// The one place that judges "name and password" for the remote-admin console and the web login (docs/areas/atlas-public.md): the SRP6
/// verifier is recomputed for every name, including an unknown one, and compared in fixed time; the ban store is asked for every name too, so a
/// right password, a wrong password and an unknown account all do the same work. An account passes only when it exists, the verifier matches,
/// its status column is Active and no ban row is in force (<see cref="AccountBanEvaluator.Effective"/>: the realm and the gateway use the same
/// rule, which the remote console did not before). Level floors, PIN/TOTP codes, IP bans and lockouts are the callers' business.
/// Thread-safe (no state); the stores are the caller's scope.
/// </summary>
internal static class AccountPasswordCheck
{
    /// <summary>Longest account name judged (longer is refused without any work, as the client cannot send one).</summary>
    public const int MaxNameLength = 32;

    /// <summary>Longest password judged.</summary>
    public const int MaxPasswordLength = 128;

    /// <summary>Judge <paramref name="name"/> and <paramref name="password"/> against <paramref name="accounts"/> and, when given, <paramref name="bans"/>.</summary>
    /// <param name="accounts">The account store (a scoped service).</param>
    /// <param name="bans">The ban store, or null when the host has none (then only the status column counts).</param>
    /// <param name="name">The account name as typed (case-insensitive).</param>
    /// <param name="password">The password as typed.</param>
    /// <param name="time">The clock the ban rows are judged on.</param>
    /// <param name="cancellationToken">Cancels the store calls.</param>
    public static async Task<AccountCheckResult> CheckAsync(
        IAccountStore accounts, IBanStore? bans, string name, string password, TimeProvider time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(time);
        if (name.Length is 0 or > MaxNameLength || password.Length is 0 or > MaxPasswordLength)
        {
            return new AccountCheckResult(AccountCheckOutcome.UnknownOrWrongPassword, null);
        }

        Account? account = await accounts.FindByUsernameAsync(name, cancellationToken).ConfigureAwait(false);

        // The verifier is computed even for an unknown name, so a wrong name and a wrong password take the same time.
        byte[] salt = account?.Salt ?? new byte[WowSrp6.SaltLength];
        byte[] computed = WowSrp6.ToFixedLittleEndian(WowSrp6.ComputeVerifier(salt, account?.Username ?? name, password), 32);
        bool passwordOk = account is not null && account.Verifier.Length == computed.Length
            && CryptographicOperations.FixedTimeEquals(computed, account.Verifier);

        // The ban lookup runs for every name (id 0 matches no row), so the database round trip does not tell a real account from an invented one.
        AccountBanRecord? ban = bans is null
            ? null
            : await bans.GetActiveAccountBanAsync(account?.Id ?? 0, cancellationToken).ConfigureAwait(false);

        if (!passwordOk)
        {
            return new AccountCheckResult(AccountCheckOutcome.UnknownOrWrongPassword, null);
        }

        long now = time.GetUtcNow().ToUnixTimeSeconds();
        AccountBanRecord? inForce = ban is not null && AccountBanEvaluator.IsActive(ban, now) ? ban : null;
        AccountStatus status = AccountBanEvaluator.Effective(account!.Status, inForce);
        if (status == AccountStatus.Active)
        {
            return new AccountCheckResult(AccountCheckOutcome.Ok, account);
        }

        return new AccountCheckResult(account.Status != AccountStatus.Active ? AccountCheckOutcome.NotActive : AccountCheckOutcome.Banned, null);
    }

    /// <summary>
    /// Whether the account is still allowed to act now: its status column is Active and no ban row is in force. Used to re-judge a ticket's
    /// account without its password.
    /// </summary>
    public static async Task<bool> IsStillActiveAsync(IBanStore? bans, Account account, TimeProvider time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.Status != AccountStatus.Active)
        {
            return false;
        }

        if (bans is null)
        {
            return true;
        }

        AccountBanRecord? ban = await bans.GetActiveAccountBanAsync(account.Id, cancellationToken).ConfigureAwait(false);
        return ban is null || !AccountBanEvaluator.IsActive(ban, time.GetUtcNow().ToUnixTimeSeconds());
    }
}
