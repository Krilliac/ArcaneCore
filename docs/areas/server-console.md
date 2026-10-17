# The server console

Both daemons read typed commands from their console window. This replaces the vmangos console command sender (mangosd `CliRunnable`; commands marked `AllowConsole`, Chat.h:66-70).

## Options

| Key | Default | Meaning |
|---|---|---|
| `World:Console:Enabled` | `true` | Read typed GM commands at the world daemon's console. |
| `Realm:Console:Enabled` | `true` | Read typed commands at the realm daemon's console. |

Both need a restart to change. Neither starts a reader when standard input is redirected or the session is not interactive (a service, a pipe, a deploy script, the tests): the daemon logs that the console is off and carries on.

## World console

Each line is read on a background thread, queued to the world thread and run through the same GM command dispatcher as in-game commands. The invoker is the console: Administrator authority, account 0, no character. The leading `.` or `!` is optional.

- `quit`, `exit` and `shutdown [n|cancel]` are shortcuts for `.server shutdown`.
- `.server faults [top N | show ID | reset]` lists the exceptions the server caught (count, site, type, age), shows one with its frames and message, or clears the list (administrator; the console is one). `.server info` carries a `faults:` summary line for GameMaster and above ([fault ledger](../ops/invariants.md#the-fault-ledger)).
- Commands that need the invoker's own character or selection answer that they need a player, and do nothing.
- With `World:HotCode:Enabled` (development only), `.worldport far <name> [x y z [o]]` and `.worldport readd <name>` run the Tier 3 same-map world-port spike on a named player; follow it with `.worldport status` or the `worldport-spike` log lines ([code hot reload](code-hot-reload.md#worldport-tier-3-spike-administrator-only-when-enabled)).
- Replies are printed prefixed `[console] ` and are not written to the log, so a one-time secret in a reply never reaches a log file.
- Every line is logged under the category `ArcaneCore.Console` as `Console command:` with passwords redacted.
- Console commands appear in the GM audit log under the name `Console`.
- If the world thread does not run a command within 30 seconds the console says so; the command stays queued. While the world is stopping the console answers that the command was not run.

The command table has no `console` root: console input is not a chat command.

## Realm console

The realm has no world thread; commands run on the reader thread and open their own database scope.

| Command | Effect |
|---|---|
| `help` | List the commands. |
| `info` | Uptime, logon sessions and the realm count. |
| `realmlist` | The realms as served to clients. |
| `online` | Connected logon sessions. |
| `account create <name> <password>` | Create an account (same logic as the `arcane-account` tool). |
| `account set-password <name> <password>` | Change a password. |
| `account set-gmlevel <name> <0-3\|name>` | Set the GM level; applies from the account's next world login. |
| `quit`, `exit`, `shutdown` | Stop the realm daemon gracefully. |

A password typed at the console is visible while typed; `arcane-account` prompts without echo. Passwords are never echoed back and are redacted from the log line.

## Limits

- A blocked read cannot be interrupted, so stopping the daemon does not wait for the reader; it is a background thread and cannot keep the process alive.
- The console is local to the window: there is no remote access, which keeps the authority tied to whoever can already operate the machine.
