# Area: Atlas action channel (world side)

Branch `claude/atlas-devui-chan`. The world daemon's [map feed](map-feed.md) can carry **actions**: a tool such as
[Atlas](atlas.md) sends one GM command, or one command template with a clicked point and a unit selection, and the daemon runs it as an
audited remote invoker (`CommandTable.ForRemote`) on the world thread. Code: `src/ArcaneCore.World/Ops/MapFeed/MapFeedAction*.cs`,
`MapFeedRoles.cs`, `MapFeedClientIdentity.cs`, `MapFeedWebAudit.cs`, `WebFeedPipe.cs`, `src/ArcaneCore.World/Gm/Atlas`.
Tests: `tests/ArcaneCore.World.Tests/Ops/MapFeed/MapFeedActions*Tests.cs`, `MapFeedRolesTests.cs`, `Gm/Atlas/AtlasClickCommandsTests.cs`.

The channel is separate from the packet tools: it never runs `console`, `inject`, `send` or `tap`.

## Switching it on

Off by default (`World:Control:MapFeed:Actions:Enabled` absent). While off, `caps` answers `actions:false` with `actionsWhyOff` and an `action`
line gets `actions-off`. A hello and the frame stream are byte-identical whether it is on or off.

| Key (under `World:Control:MapFeed:Actions`) | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Accept `action` lines. `World:Control:MapFeed:Enabled` must be on too. |
| `AllowOnLiveServer` | `false` | Needed on top of `Enabled` when the process carries the live-server marker (`ARCANECORE_LIVE_SERVER`); the daemon logs a Warning either way. |
| `LocalSecurity` | `Administrator` | The level the local actor runs at (Player to Administrator). |
| `WebLogin` | `false` | Accept web actors (tickets from the public Atlas listener). Needs the true-console predicate (`CommandContextConsole`, from `claude/fix-remote-console-bypass`) in the build and an `IFeedActorTickets` service; otherwise a Warning is logged and web actors get `web-login-off`. |
| `WebLoginMinSecurity` | `Moderator` | Lowest account level a web login is accepted for. |
| `PublicMaxSecurity` | `GameMaster` | Highest level a web actor runs at, whatever its account has. |
| `PublicDeny` / `PublicAllowModeration` / `Deny` | account, server lifecycle, reload, hot code, raw bot, `ban`/`unban` | Command paths refused to web actors / to every actor, matched on the resolved path. |
| `LocalAccountId` | none | The account the local actor counts as for "my own character" (`.atlas tele` at GameMaster level). |
| `BulkConfirmAbove`, `SettleSeconds` | `1`, `30` | A template over more targets than this needs a confirm code; how long an action waits for its command to settle. |
| `LocalPerSecond/Burst`, `WebPerSecond/Burst` | 10/20, 2/5 | Rate limits (token bucket; per actor, and per connection before the actor is resolved: a web connection gets twice the web burst). |
| `AuditLocalDurable`, `WebAuditDays`, `AuditDirectory` | on, 90, `logs` | Durable JSONL audit (`atlas-web-audit.jsonl`). |

`World:Control:MapFeed:Web:Enabled` + `ClientAccount` start a second pipe (`arcanecore-mapfeed-web-<pid>`) whose connections are the `web` role by
construction; its ACL admits only that one Windows account. `ClientAccount` must be one created account: Everyone, Authenticated Users, SYSTEM,
LOCAL/NETWORK SERVICE, `BUILTIN\`, `NT AUTHORITY\` and `NT SERVICE\` names, `S-1-5-32-*`, any account SID with a RID below 1000 and the daemon's own
account are refused (the configuration check fails for the names and SIDs it can judge as text, the pipe stays off with a Warning for the rest).

## Roles

A connection's role is fixed by the pipe it arrived on (local pipe: `local`; web pipe: `web`), never by a request. On a `web` connection `console`,
`inject`, `send`, `tap`, `untap` and any `query` are `role-mismatch` whatever the packet tools setting is; an action with `tier:local` is
`role-mismatch` and nothing runs. The `role` op binds an unbound connection once; a second, different binding answers `role-locked`.

## Actors

* **local**: the daemon account. The audit actor is `atlas-local:<token user>#<pid>` from `GetNamedPipeClientProcessId` and that process's token; a
  name in the request is ignored.
* **web**: `actor:{"tier":"web","ticket":"..."}`. The ticket is checked through `IFeedActorTickets` (cached at most 30 s, so a ban or demotion
  takes effect within 30 s). Failures: `no-ticket`, `expired`, `banned`, `ticket-unavailable`.

Every action gets its own executor and console session at the actor's level; two actors on one connection never share one.

## Wire

`{"op":"caps","id":N}` answers `actions`, `actionsWhyOff`, `login`, `liveServer`, `role`, `templates`.
`{"op":"action","id":N,"actor":{...},"command":"atlas place creature ..."}` or `"template":"place.creature","args":{...},"targets":["<guid>"],
"point":{"map":0,"x":..,"y":..,"z":..,"o":..},"confirm":"<code>"` answers one `result` (`ok`, `kind:"action"`, `lines`, `settled`, per-target
`targets`, `code`/`message` on refusal). Output that arrives after the settle window is sent as `actionLine`.

## Templates

`place.creature` and `place.gobject` (`entry`, point), `moveto` and `tele` (targets, point), `kill` and `debug` (targets), `spawns` (`radius`, point).
The daemon formats the command; a tool never splices text into it. Arguments are validated (non-negative whole numbers, finite coordinates), so
`; .server shutdown` or a newline in an argument is `bad-args`.

## Codes

`actions-off`, `live-server`, `role-mismatch`, `role-locked`, `web-login-off`, `no-ticket`, `expired`, `banned`, `ticket-unavailable`,
`audit-unavailable`, `rate-limited`, `unknown-command`, `console-only`, `security`, `too-many-targets`, `target-gone`, `confirm-required`,
`confirm-wrong`, `timeout`, `world-stopping`, `failed`, plus the feed's `bad-args`. `cancelled` and `unsettled` are audit results, not refusals: see Audit.

## Confirm tokens

A template on more than `BulkConfirmAbove` targets answers `confirm-required` with a `confirm` object (`code`, `expiresIn`, `summary`) and runs
nothing. Re-send with the code. A code belongs to the actor it was issued to; three wrong codes invalidate it.

## Audit

Every action writes a log line (`ArcaneCore.Ops.MapFeed.Actions`: actor, command, `-> result`) and a `GmAuditFeature` entry, including level-0
commands the command table does not log. Web actions (and, by default, local ones) also append one JSONL record to the durable audit, before the
answer goes out, as do web login, logout, kick and revoke. An unwritable audit file refuses the action with `audit-unavailable` and runs nothing.
Secrets in arguments are redacted and the ticket is never written.

An action that ran is always recorded, whatever ended the wait. The `result` is `ok` or the failure code only when the command settled; `unsettled`
when it was still open after `SettleSeconds` (the answer is `ok:true, settled:false`), and `cancelled` when the connection or the daemon ended during
the wait or before the answer. A `timeout` or `world-stopping` refusal means the world thread had not begun the command; one that fires after it began
waits for the command (at most `SettleSeconds`) and records what it did. The request's service scope is released when the command settles, or after
five minutes, never earlier.

Refusals before an actor is known (no ticket, wrong role, unknown ticket, a rate-limited connection) are merged on a web connection: the first of a run
of the same code is recorded as an `action` record, the repeats are counted, and one `refusals` record (`targets` = the repeat count, `result` and `code`
= the refused code) is written when the code changes, after 100 repeats, or when the connection ends. Repeats are logged at Debug.

## The `.atlas` commands

`.atlas place creature|gobject`, `.atlas moveto`, `.atlas tele`, `.atlas debug`, `.atlas spawns` (see the [GM command reference](../reference/gm-commands.md))
read the click point and selection from the action context; `.creature kill` and the other selection-based commands use the first selected guid
when the invoker is a console-like action. The context survives a `World.Post` deferral.

## Remote bypasses

Console-only commands answer `console-only` for every actor, local and web. Branches that skip a security check for a console invoker
(`GmTargets.ActOnOffline`, `.ban` and the like) must use the true-console predicate (design 1.7, PR `claude/fix-remote-console-bypass`) so a
GameMaster cannot act on an offline Administrator's character or ban an Administrator account through the channel. A build without that predicate
refuses web login. The registry is `ConsoleOnlyCommands` (registered with the command table); `WorldConsoleExecutor` also refuses a registered path for
every remote context, so remote admin gets the same answer. A lane that adds a Console-only leaf registers its path there or tests `IsTrueConsole()`
in the handler.
