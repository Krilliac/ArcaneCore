using ArcaneCore.Kernel.Accounts;

namespace ArcaneCore.World.Ops.RemoteAdmin;

/// <summary>
/// Configuration of the world daemon's remote administration listener (RA, docs/ops/remote-admin.md): GM commands over a plain
/// TCP line protocol, the mangosd <c>Ra.*</c> settings (cmangos mangosd.conf.dist: Ra.Enable, Ra.IP, Ra.Port, Ra.MinLevel). On by
/// default, on the loopback address only: a machine-local operator or AI agent can drive the server (and its playerbots through
/// <c>.botctl</c>) headlessly. Every command runs with the logged-in account's security level and is GM-audited.
/// </summary>
public sealed class RemoteAdminOptions
{
    public const string SectionName = "World:RemoteAdmin";

    /// <summary>Listen for RA connections. Default on (loopback only, see <see cref="BindAddress"/>).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The address to listen on. Default 127.0.0.1: only this machine can connect. Any other address exposes a clear-text protocol
    /// (passwords included) to the network; the daemon logs a warning when it is not a loopback address.
    /// </summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    /// <summary>The TCP port (mangosd Ra.Port default 3443).</summary>
    public int Port { get; set; } = 3443;

    /// <summary>The lowest account security that may log in (mangosd Ra.MinLevel, default Administrator).</summary>
    public AccountSecurity MinSecurity { get; set; } = AccountSecurity.Administrator;

    /// <summary>
    /// A file the daemon writes a fresh random token to at start; <c>@token &lt;token&gt;</c> logs in as the console (Administrator)
    /// without an account, for automation running as the same OS user. The file is created owner-only (Windows: an ACL for the daemon's account and SYSTEM, no
    /// inheritance; Unix: mode 600), so other local users cannot read it. Relative paths are under the daemon's base directory. Empty: token login off.
    /// </summary>
    public string AgentLoginFile { get; set; } = "ra-token.txt";

    /// <summary>Concurrent connections; one more is refused with a message.</summary>
    public int MaxConnections { get; set; } = 8;

    /// <summary>Seconds a connection has to log in.</summary>
    public int LoginTimeoutSeconds { get; set; } = 60;

    /// <summary>Seconds a logged-in connection may stay silent (no command, no running command) before it is closed. 0: never.</summary>
    public int IdleTimeoutSeconds { get; set; } = 3600;

    /// <summary>Wrong logins a connection may make before it is closed (each one delayed by <see cref="FailedLoginDelayMs"/>).</summary>
    public int MaxFailedLogins { get; set; } = 3;

    /// <summary>The delay before answering a wrong login.</summary>
    public int FailedLoginDelayMs { get; set; } = 1000;

    /// <summary>The longest command line in bytes; a longer line closes the connection without running anything.</summary>
    public int MaxLineLength { get; set; } = 16384;

    /// <summary>
    /// Seconds a command that holds itself open (<c>.botctl wait</c>, <c>.botctl run</c>) may run before RA reports <c>#done error
    /// timeout</c> and reads the next line (the command itself goes on).
    /// </summary>
    public int CommandTimeoutSeconds { get; set; } = 1800;
}