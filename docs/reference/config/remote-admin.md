# RemoteAdmin options

12 options. Set them in the daemon's `.conf` file or `appsettings.json`. Full reference: [configuration.md](../configuration.md). All areas: [index](README.md).

Daemon says which server reads it. Reload says whether `.reload config` applies it.


## `World:RemoteAdmin`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `AgentLoginFile` | string | `ra-token.txt` | text | World | start | A file the daemon writes a fresh random token to at start; `@token &lt;token&gt;` logs in as the console (Administrator) without an account, for automation running as the same OS user. The file is created owner-only (Windows: an ACL for the daemon's account and SYSTEM, no inheritance; Unix: mode 600), so other local users cannot read it. Relative paths are under the daemon's base directory. Empty: token login off. |
| `BindAddress` | string | `127.0.0.1` | text | World | start | The address to listen on. Default 127.0.0.1: only this machine can connect. Any other address exposes a clear-text protocol (passwords included) to the network; the daemon logs a warning when it is not a loopback address. |
| `CommandTimeoutSeconds` | int | `1800` | a whole number, -2147483648 to 2147483647 | World | start | Seconds a command that holds itself open (`.botctl wait`, `.botctl run`) may run before RA reports `#done error timeout` and reads the next line (the command itself goes on). |
| `Enabled` | bool | `true` | true \| false | World | start | Listen for RA connections. Default on (loopback only, see `BindAddress`). |
| `FailedLoginDelayMs` | int | `1000` | a whole number, -2147483648 to 2147483647 | World | start | The delay before answering a wrong login. |
| `IdleTimeoutSeconds` | int | `3600` | a whole number, -2147483648 to 2147483647 | World | start | Seconds a logged-in connection may stay silent (no command, no running command) before it is closed. 0: never. |
| `LoginTimeoutSeconds` | int | `60` | a whole number, -2147483648 to 2147483647 | World | start | Seconds a connection has to log in. |
| `MaxConnections` | int | `8` | a whole number, -2147483648 to 2147483647 | World | start | Concurrent connections; one more is refused with a message. |
| `MaxFailedLogins` | int | `3` | a whole number, -2147483648 to 2147483647 | World | start | Wrong logins a connection may make before it is closed (each one delayed by `FailedLoginDelayMs`). |
| `MaxLineLength` | int | `16384` | a whole number, -2147483648 to 2147483647 | World | start | The longest command line in bytes; a longer line closes the connection without running anything. |
| `MinSecurity` | AccountSecurity | `Administrator` | Player \| Moderator \| GameMaster \| Administrator | World | start | The lowest account security that may log in (mangosd Ra.MinLevel, default Administrator). Values: `Player`, `Moderator`, `GameMaster`, `Administrator`. |
| `Port` | int | `3443` | a whole number, -2147483648 to 2147483647 | World | start | The TCP port (mangosd Ra.Port default 3443). |
