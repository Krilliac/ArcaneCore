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
| `realmlist` | The realms as served to clients: stored flags, plus the world's state, its heartbeat age and the flags a player is sent now ([realm list liveness](../ops/realm-liveness.md)). |
| `online` | Connected logon sessions. |
| `account create <name> <password>` | Create an account (same logic as the `arcane-account` tool). |
| `account set-password <name> <password>` | Change a password. |
| `account set-gmlevel <name> <0-3\|name>` | Set the GM level; applies from the account's next world login. |
| `quit`, `exit`, `shutdown` | Stop the realm daemon gracefully. |

A password typed at the console is visible while typed; `arcane-account` prompts without echo. Passwords are never echoed back and are redacted from the log line.

## Control pipe

The same console commands, sent by a local tool instead of typed in the window: `tools/ops/live/redeploy.ps1` applies a hot redeploy
through it ([Redeploy prefers hot reload](../ops/redeploy.md)). Windows only; off by default.

| Key | Default | Meaning |
|---|---|---|
| `World:Control:Pipe:Enabled` | `false` | Create the pipe. Needs a restart to change. |
| `World:Control:Pipe:Name` | `arcanecore-world-{pid}` | The pipe name (`\\.\pipe\<name>`); `{pid}` is the world process id. Letters, digits, `.`, `_`, `-`. |
| `World:Control:Pipe:IdleTimeoutSeconds` | `120` | A connected client that sends nothing for this long is disconnected. |
| `World:Control:Pipe:MaxLineLength` | `4096` | A longer line closes the connection; nothing is run. |

Security:

- **Who may connect.** The pipe's security descriptor is protected (nothing inherited), owned by the account the World runs as, and has
  three entries: deny NETWORK, allow that account full control, allow SYSTEM read and write. Administrators, other users and everyone else
  have no entry. Whoever can open it can already run code as that account.
- **Local only, two barriers.** The pipe is created with `PIPE_REJECT_REMOTE_CLIENTS` (native `CreateNamedPipeW` in
  `Ops/Control/LocalPipeServer.cs`; the .NET pipe constructors cannot set it), and the World reads the mode back from the kernel and
  leaves the pipe off if it did not take. Independently, a network logon's token carries NETWORK, which the descriptor denies. Neither
  has been tried from a second machine yet.
- **No squatting.** One pipe instance for the life of the process, created with `FILE_FLAG_FIRST_PIPE_INSTANCE`: if another process created
  the name first (even one allowing any number of instances), the World refuses it (an error is logged and the pipe stays off) instead
  of joining it. The client in `tools/ops/live/RedeployPlan.ps1` checks that the process serving the pipe is the live World's PID before it sends a command.
- **Authority and audit.** A line runs through the same executor as a typed console line: Administrator authority, account 0, no
  character; GM audit entries under the name `Console`; secrets redacted. Each line is also logged under the category
  `ArcaneCore.Control` as `Control pipe command: <line> (client pid <pid> <process>)`, redacted the same way, with the connect and
  disconnect of each client.
- **The world thread is never blocked by a client.** Replies are queued and written by a per-connection pump; a client that stops reading
  loses lines (counted in the disconnect log line), it cannot stall a tick.

Protocol (UTF-8, newline-terminated lines): send a command line; the World answers with the reply lines (prefixed `[console] `) and then
`#done` once the world thread has run the command. A command that finishes later (`.reload`, `.hotmodule`) sends its result after `#done`
while the connection stays open: `#done` is queued on the world thread right after the command ran, so such a result always follows it. If the world
does not run a command within 30 s the World answers that it stays queued and then sends `#done`; whatever that command replies when it does run
comes later. One client at a time; the next one is served after it disconnects.

## Map feed console (packet tools)

A development daemon can also take console lines over the map feed's pipe, as JSON, from Atlas (`server.console`) or any tool that
speaks the feed's protocol: `{"op":"console","id":n,"line":".reload status"}`, answered by a `result` holding the reply lines. It is
part of the map feed's [packet tools](map-feed.md#packet-tools): off by default (`World:Control:PacketTools:Enabled`), refused
outside the Development environment unless `World:Control:PacketTools:AllowOutsideDevelopment` is also set, and only on the map feed's
pipe, whose security is this control pipe's (same access list, `PIPE_REJECT_REMOTE_CLIENTS`, first-instance rule). A line runs through
the same executor as a control-pipe line (Administrator authority, account 0, GM audit entries under `Console`, secrets redacted) and is
logged under `ArcaneCore.MapFeed` as `Map feed console command: <line> (client pid <pid> <process>)`. Each feed connection has its own
console session; a command that holds itself open is waited for up to 2 minutes, and its later lines arrive as `consoleLine`. The
control pipe stays the redeploy scripts' channel; the feed's console is for agents and tools that already drive the daemon through Atlas.

## Limits

- A blocked read cannot be interrupted, so stopping the daemon does not wait for the reader; it is a background thread and cannot keep the process alive.
- The console is local to the window, which keeps the authority tied to whoever can already operate the machine. The control pipe keeps that rule: local only, and only the World's own account (and SYSTEM). The exception is [remote administration](../ops/remote-admin.md) (RA): the same commands over TCP, on by default and bound to 127.0.0.1:3443, each run with the security level of the account that logged in (at least `World:RemoteAdmin:MinSecurity`, Administrator by default), or as the console with the token the World writes to `World:RemoteAdmin:AgentLoginFile` (`ra-token.txt` next to the daemon by default).
