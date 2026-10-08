namespace ArcaneCore.Kernel.Configuration;

/// <summary>Configuration for the logon/realm daemon.</summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>Interface to bind the logon listener to.</summary>
    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>Logon/realm TCP port. Vanilla default is 3724 (Charter §3).</summary>
    public int Port { get; set; } = 3724;

    /// <summary>
    /// WCell-style auto-create-on-login. When enabled, an unknown account whose login
    /// proof validates with password == username is created on the spot
    /// (see WCell Services/WCell.AuthServer/Authentication.cs).
    /// </summary>
    public bool AutocreateAccounts { get; set; }

    /// <summary>
    /// Hard cap on one logon connection's lifetime in seconds; 0 disables. vmangos
    /// MaxSessionDuration defaults to 300 (realmd.conf.dist.in, AuthSocket.cpp:76-82).
    /// </summary>
    public int MaxSessionDurationSeconds { get; set; } = 300;

    /// <summary>
    /// Longest a client may take to deliver the rest of a packet once its command byte has
    /// arrived, in seconds; 0 disables. Hardening (no vmangos equivalent, default 0 =
    /// retail): a retail client sends each logon packet in one write.
    /// </summary>
    public int ReadTimeoutSeconds { get; set; }

    /// <summary>
    /// Reject account names containing anything but printable ASCII (0x21-0x7E). Hardening:
    /// vmangos only escapes the name for SQL. A 1.12 client cannot type other characters.
    /// Default off (retail).
    /// </summary>
    public bool StrictUsernameCharset { get; set; }

    /// <summary>Global cap on simultaneous logon connections; 0 = unlimited (retail, the default). Hardening (no vmangos equivalent).</summary>
    public int MaxConnections { get; set; }

    /// <summary>Cap per client IP address; 0 = unlimited (retail, the default). Hardening: a retail client holds one connection.</summary>
    public int MaxConnectionsPerIp { get; set; }

    /// <summary>
    /// How long the logon daemon keeps its in-memory copy of <c>ip_banned</c> before the next logon challenge reloads it,
    /// in seconds (realm <c>RealmIpBanCache</c>). Default 60, mangosd's BanListReloadTimer (World.cpp:697), whose
    /// in-memory IP list this mirrors (AccountMgr.cpp:340-367, 412-417). vmangos realmd reads the table on every
    /// challenge (AuthSocket.cpp:338-352); 0 restores that. An IP ban written elsewhere reaches the logon screen within
    /// this period; world authentication reads the rows directly and refuses the address at once. An unban written
    /// elsewhere also takes up to this period to let the address back in. When the list cannot be loaded, challenges use
    /// the single-row read for one period instead (closed only if that read fails too).
    /// </summary>
    public int IpBanCacheSeconds { get; set; } = 60;

    /// <summary>vmangos StrictVersionCheck, default false. Enabled checks crc_hash against
    /// a configured 20-byte hash for the client build/OS/platform.</summary>
    public bool StrictVersionCheck { get; set; }

    /// <summary>Known client integrity hashes. A zero hash skips the check for its exact client tuple.</summary>
    public List<ClientIntegrityHashOptions> IntegrityHashes { get; set; } = [];
}

public sealed class ClientIntegrityHashOptions
{
    public ushort Build { get; set; }
    public string Os { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
}
