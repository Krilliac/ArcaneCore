namespace ArcaneCore.Kernel.Accounts;

/// <summary>
/// Account security levels — the cmangos-classic / TrinityCore AccountTypes scale
/// (SEC_PLAYER 0 … SEC_ADMINISTRATOR 3; vmangos splits the staff levels further into 0–7).
/// Gates GM commands, instant logout and /who visibility.
/// </summary>
public enum AccountSecurity : byte
{
    Player = 0,
    Moderator = 1,
    GameMaster = 2,
    Administrator = 3,
}

/// <summary>Login state of an account, surfaced to the client as a logon result code.</summary>
public enum AccountStatus
{
    Active = 0,
    Banned = 1,
    Suspended = 2,
}

/// <summary>vmangos realmd AuthSocket.h LockFlag values used by classic PIN authentication.</summary>
[Flags]
public enum AccountLockFlags : byte
{
    None = 0,
    IpLock = 0x01,
    FixedPin = 0x02,
    Totp = 0x04,
    AlwaysEnforce = 0x08,
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

    /// <summary>GM level (auth schema v2).</summary>
    public AccountSecurity Security { get; set; } = AccountSecurity.Player;

    /// <summary>PIN/TOTP and IP-lock policy (auth schema v5).</summary>
    public AccountLockFlags LockFlags { get; set; }

    /// <summary>Fixed PIN digits or Base32 TOTP key; never include in logs.</summary>
    public string SecurityInfo { get; set; } = string.Empty;

    /// <summary>Last successful realm logon address, for vmangos IP_LOCK semantics.</summary>
    public string LastIp { get; set; } = string.Empty;
}
