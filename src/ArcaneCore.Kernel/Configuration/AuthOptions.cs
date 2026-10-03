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
}
