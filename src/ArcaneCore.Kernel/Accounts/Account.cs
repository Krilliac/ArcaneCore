namespace ArcaneCore.Kernel.Accounts;

/// <summary>Login state of an account, surfaced to the client as a logon result code.</summary>
public enum AccountStatus
{
    Active = 0,
    Banned = 1,
    Suspended = 2,
}

/// <summary>
/// A logon account. Credentials are stored as the SRP6 salt + verifier only — never
/// a recoverable password (Charter §3; mirrors the vmangos `account` table semantics).
/// </summary>
public sealed class Account
{
    public int Id { get; set; }

    /// <summary>Account name, stored uppercased (the client uppercases before hashing).</summary>
    public required string Username { get; set; }

    /// <summary>32-byte SRP6 salt.</summary>
    public required byte[] Salt { get; set; }

    /// <summary>SRP6 verifier v, stored as a 32-byte little-endian array.</summary>
    public required byte[] Verifier { get; set; }

    /// <summary>
    /// 40-byte SRP6 session key K from the last successful logon. Handed to the world
    /// daemon for the M2 world handshake. Null until the first successful login.
    /// </summary>
    public byte[]? SessionKey { get; set; }

    public AccountStatus Status { get; set; } = AccountStatus.Active;
}
