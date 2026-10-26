# Remote administration (RA)

The world daemon listens for remote-admin connections: a line-based TCP console for operators, scripts and AI agents. It is **on by
default** and binds **127.0.0.1:3443**, so only processes on the same machine can reach it. Every command runs exactly like a server
console line, but with the security level of the login, and the GM audit records it with the invoker "RA" plus the account name.

## Configuration (`World:RemoteAdmin`)

| Key | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `true` | Listen at all. |
| `BindAddress` | `127.0.0.1` | The address to bind. A non-loopback address logs a warning, because RA is clear text (passwords included). Put it behind an SSH tunnel or VPN instead. |
| `Port` | `3443` | The TCP port. `0` lets the system pick one (tests). |
| `MinSecurity` | `Administrator` | The lowest account level allowed to log in. |
| `AgentLoginFile` | `ra-token.txt` | Where a fresh random token is written at start-up for `@token` login (relative paths sit next to the daemon binary). The file is deleted on shutdown. Empty turns token login off. |
| `MaxConnections` | `8` | Concurrent connections. Extra ones get `-Too many connections; try again later.` |
| `LoginTimeoutSeconds` | `60` | Time allowed to log in. |
| `IdleTimeoutSeconds` | `3600` | An idle connection closes after this long. |
| `MaxFailedLogins` | `3` | Wrong logins per connection before it closes. |
| `FailedLoginDelayMs` | `1000` | Delay after each wrong login. |
| `MaxLineLength` | `16384` | The longest accepted line, in bytes. |
| `CommandTimeoutSeconds` | `1800` | The longest a long-running command (`.botctl wait`, `.botctl run`) is waited for. Then RA stops waiting (`#done error timeout` in automation mode) and reads the next line; the command itself keeps running until it ends or the connection closes, and its later reply lines still arrive. |

## Logging in

After connecting, the server sends a greeting line and then `Username:` as a line of its own.

* **Interactive** (telnet, PuTTY raw): type the account name, then the password after `Password:`. The prompt `AC> ` (no newline) is
  shown before each command.
* **Automation, account:** one line, `@login <user> <password>`.
* **Automation, token:** one line, `@token <token>`. The token is the content of `AgentLoginFile`. It logs in as `Console` with
  Administrator rights. Anyone who can read the daemon's directory can read the token, so it is meant for agents on the same machine.

The answer is `+Logged in as NAME (Level).`, or `-Wrong username or password.` (an unknown account, a wrong password, an inactive account
and an account below `MinSecurity` all look the same). After `MaxFailedLogins` the server says `-Too many failed logins; closing.`

## Automation mode

`@login` and `@token` turn automation mode on. `@auto on|off` switches it at any time. In automation mode **every command line gets
exactly one terminal line** after its replies:

```
#done ok
#done error <reason>
```

Reasons: `unknown-command`, `unknown-subcommand`, `security`, `console-only` (the command can only be run at the true server console), `syntax`, `needs-player` (the command needs a player in game, e.g.
`.gps`), `exception`, `timeout`, `world-timeout`, `empty`, or the command's own code (every `.botctl` failure code, e.g. `no-match`,
`not-controlled`, `server-error`, `step-failed`).

Other protocol lines:

| You send | You get |
| --- | --- |
| `@id TAG <command>` | The command's replies, then `#done TAG ok` or `#done TAG error <reason>`. |
| `@ping` | `#pong` |
| `@auto on` / `@auto off` | `#done ok` |
| `quit`, `exit`, `logout`, `@quit` | `+Bye.`, then the connection closes. The server keeps running. |

The commands of one connection run one at a time, in order, except that a command reported `#done error timeout` goes on in the
background while the next lines run. A command that holds itself open (`.botctl wait`, `.botctl run`, `.botctl sleep`) is `#done`
only when its work is done, or after `CommandTimeoutSeconds` with `#done error timeout`. Lines starting with `#event` can arrive at any time on a connection that turned on
`.botctl stream`. A leading `.` on commands is optional.

Remote administration, including the `@token` "Console" login, never has the true console's outranks-every-account exemption: it is
checked at its login's security level like any other invoker. With `ProtectHigherSecurity` on it cannot ban an account of equal or
higher security, and it cannot run Console-only commands. Scripts that need those use the control pipe.

## Example

```text
$ python tools/botctl/ra.py --token-file <live-home>\bin\ra-token.txt ".server info" ".botctl list"
```

Raw session (`>` is what you send):

```text
ArcaneCore remote administration. Log in with your account (or one line: @login <user> <password> | @token <token>).
Username:
> @token 3f9c...e1
+Logged in as Console (Administrator).
> .botctl take Alice
  Alice: ok controlled (passive); its own AI is paused
OK take (1/1 bots ok)
#done ok
> @id m1 .botctl move Alice -8913.2 554.6 93.1
  Alice: ok walking to (-8913.2, 554.6, 93.1) (31.4 yd)
OK move (1/1 bots ok)
#done m1 ok
> .botctl cast Alice 99999
  Alice: error ignored the server ignored the cast of 99999 (the bot does not know it, or it is not castable this way)
ERR ignored cast (0/1 bots ok)
#done error ignored
```

See [botctl.md](botctl.md) for the bot control commands and the client scripts.