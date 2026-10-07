namespace ArcaneCore.Kernel.Accounts;

/// <summary>The address an account last entered the world from (vmangos <c>account.last_ip</c>), with the time.</summary>
public sealed record AccountAddressRecord(int AccountId, string Ip, long LastSeenUnix);

/// <summary>
/// The last address of each account, recorded by the world daemon when a session authenticates (vmangos writes
/// <c>account.last_ip</c> at logon, AuthSocket.cpp). It serves <c>.ban allip</c>, which bans every low-level account last
/// seen on an address prefix (vmangos HandleBanAllIPCommand, AccountCommands.cpp:531-585). Kept in the characters
/// database next to <c>account_mute</c>: the world daemon owns it, and the auth schema is the realm daemon's.
/// </summary>
public interface IAccountAddressStore
{
    /// <summary>Insert or replace the account's last address.</summary>
    Task RecordAsync(int accountId, string ip, long nowUnix, CancellationToken cancellationToken = default);

    /// <summary>
    /// The accounts whose last address starts with <paramref name="prefix"/> (vmangos <c>last_ip LIKE CONCAT(prefix, '%')</c>,
    /// a literal prefix here: <c>%</c> and <c>_</c> are not wildcards), lowest account id first.
    /// </summary>
    Task<IReadOnlyList<AccountAddressRecord>> FindByPrefixAsync(string prefix, CancellationToken cancellationToken = default);
}
