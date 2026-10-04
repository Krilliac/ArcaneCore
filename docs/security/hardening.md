# Security hardening (lane `security-hardening`)

Scope of this document: what the lane delivered, how each rule maps to the real references
(`D:\refs\vmangos` primary), which behaviours are deliberate hardening that retail does not
have, and what is still open. Reference code was read only; nothing was copied.

## Delivered

### Logon connection state (finding F1, critical)

`LogonSession` kept the SRP object, username and auto-create flag across challenges. After
`challenge(A)` then `challenge(B)` where B was banned or suspended, a proof computed from A's
password but carrying B's name passed and `UpdateSessionKeyAsync(B, K)` stored an
attacker-known key on B (the world daemon then accepted that key). Reproduced with
`StaleSrpTakeoverTests` before the fix.

* Every challenge calls `ResetChallengeState()` first; the username and SRP object are
  committed only after every early-out (unknown, banned, suspended, degenerate credentials).
* The first proof consumes the SRP state, success or failure: one proof per challenge.
* A new challenge or proof revokes the previous authentication.
* vmangos: `src/realmd/AuthSocket.cpp:327` and `:555` (`m_status = STATUS_INVALID` on handler
  entry). Realm-list stays repeatable (vmangos does not invalidate there).

### Degenerate SRP verifier / salt (F3)

`Srp6Validation` + `Srp6Server.TryCreate`: verifier must be in (1, N), salt 32 bytes and not
all zero; client key A must be exactly 32 bytes in (0, N); M1 exactly 20 bytes. Wrong sizes
return false instead of throwing. A zero/1/N verifier made the shared secret independent of
any password (forgeable from public data; proven by `Srp6DegenerateInputTests`). vmangos
`src/shared/Crypto/Authentication/SRP6.cpp:69-78` (A rejected) and `:188-199` (zero
salt/verifier rejected). The `v == 1`, `v >= N` and `A >= N` rules extend vmangos. An account
with unusable credentials gets `FAIL_NOACCESS` (0x0D, `AuthCodes.h`).

### World authentication enforces account status (F4, status half)

`WorldSession.HandleAuthSessionAsync` answers `AUTH_BANNED` (0x1C) for every non-active account
(banned or suspended) after the digest check. vmangos has exactly one ban reply, `AUTH_BANNED`
(`WorldSocket.cpp:333-345`), for a banned account or an IP ban, temporary or permanent;
`AUTH_SUSPENDED` does not occur in that file (the only other nearby reply is `AUTH_UNAVAILABLE`
at `:355`). The IP-ban half is delivered by the live-ban lane (`docs/security/live-bans.md`).
`AuthResponseCode` gained `Unavailable 0x10`, `AlreadyOnline 0x1D`, `DbBusy 0x1F`, `Banned`,
`Suspended`, confirmed against `wow_messages world/enums/world_result.wowm:35,57,59,63,65`.
Ban tables, IP bans and live enforcement: delivered by the live-ban lane (`docs/security/live-bans.md`).
Not delivered here: session-key age (see below).

### Stalled-writer teardown (F5)

`Kick()` only cancelled the read side, so `RunAsync` awaited a writer parked in `WriteAsync`
against a client with a zero TCP window; the socket, DI scope and up to 8 MiB of frames
leaked. Writes now take an abort token; after `WorldSessionOptions.WriterDrainGrace`
(`World:WriterDrainGrace`) the write is cancelled and the stream disposed. The default is
`00:00:00`, which waits forever as retail does; an internet-facing operator should set a bound,
for example `00:00:05`. `StalledWriterTests`.

### Logon limits and input validation (F2 partial, F12)

* `Auth:MaxSessionDurationSeconds = 300` (vmangos `MaxSessionDuration`, `AuthSocket.cpp:76-82`).
* `Auth:ReadTimeoutSeconds = 30` for the rest of a packet after its command byte
  (hardening, slowloris; vmangos has none).
* Challenge body must be 31..47 bytes, `username_len <= 16`, locale in the vmangos allow-list;
  violations close without a reply as realmd does (`AuthSocket.cpp:248-262, 273, 213-230,
  306`).
* `Auth:StrictUsernameCharset = true` (opt-in, default false = retail): names outside printable
  ASCII get `UnknownAccount`.
* Client text goes through `LogSafe.Escape` before logging (CR/LF/ESC and format characters
  escaped, 64-character cap) to stop log forging.
* `Realm/appsettings.json` ships `AutocreateAccounts: false` (was `true`). **Deviation from the
  WCell convenience, not from retail** (vmangos has no autocreate). README.md and
  docs/M1_ACCEPTANCE.md describe enabling it as an opt-in dev convenience; accounts are normally
  created with `arcane-account create`.

### Connection admission and accept-loop resilience (F7, listener half)

`Kernel/Net/AcceptLoop` keeps the loop alive through `SocketException`/`IOException`
(rate-limited log, 100 ms backoff); previously one error faulted the `BackgroundService` and
stopped the host. `ConnectionLimiter` enforces `Auth:` / `World:MaxConnections` and
`MaxConnectionsPerIp` before a DI scope or session exists (default 0 = unlimited, retail;
IPv4-mapped addresses share the IPv4 bucket). Both caps are opt-in hardening with no vmangos
equivalent. The world
`HandleClientAsync` no longer evaluates `RemoteEndPoint` outside its `try`.

### Movement validation (F8)

`MovementValidator` ports `VerifyMovementInfo` (`MovementHandler.cpp:1042-1061`,
`IsValidMapCoord` `GridDefines.h:175-203`): |x|,|y| <= MAP_HALFSIZE - 0.5 (17066.166),
|z| <= 400000, finite |o| <= 4 pi, on a transport |tx|,|ty| <= 250 and |tz| <= 100 and
position + transport offset itself a valid coordinate. Invalid packets are **dropped** (vmangos
behaviour) instead of kicking. Opt-in hardening beyond retail
(`World:StrictMovementFiniteness`, default false): pitch, jump speeds/angles and spline
elevation must be finite, because they are stored and relayed to every observer verbatim. `MovementRelayTests` proved the relay before the fix.

### Channel cap (F12)

`World:Social:MaxJoinedChannels` (`SocialOptions`, default 0 = unlimited, retail). A positive value
makes a refused join answer the existing `INVALID_NAME` notify and create no channel. Opt-in
hardening: vmangos has no cap (`ChannelMgr.cpp:52-69`).

### Pre-auth deadline and inbound queue bounds

`WorldSessionOptions.PreAuthTimeout` (`World:PreAuthTimeout`, default 10 s) closes a world
connection that has not sent a valid `CMSG_AUTH_SESSION` in time; this is the retail rule
(vmangos `Network.TimeoutSecsIfNoAuth = 10`, `WorldSocket.cpp:621-628`), and `00:00:00` disables
it. `MaxQueuedWorldPackets` (8192) and `MaxQueuedWorldBytes` (8 MiB) bound what one session may
have queued for the world thread and disconnect it past either (0 disables). vmangos queues
without a bound (`WorldSession.cpp:307-332`), so these two are hardening that is **on by
default**. Tests: `CodexNetAuthWorldTests`, `InboundQueueCapTests`.

## Configuration summary

The hardening switches below default to retail behaviour (a value of 0, off or unlimited), except
the two inbound-queue bounds and the shared per-address connection cap, which are on by default
because vmangos has no equivalent (both are in the release-caveat register of `docs/guide/operations.md`).
The full list of every key and its default is the generated
[configuration reference](../reference/configuration.md).

| Key | Default | Source |
|---|---|---|
| `Auth:MaxSessionDurationSeconds` | 300 | vmangos retail |
| `Auth:ReadTimeoutSeconds` | 0 (off) | opt-in hardening |
| `Auth:StrictUsernameCharset` | false | opt-in hardening |
| `Auth:MaxConnections` / `MaxConnectionsPerIp` | 0 / 0 (unlimited) | opt-in hardening |
| `World:MaxConnections` / `MaxConnectionsPerIp` | 0 / 0 (unlimited) | opt-in hardening |
| `Net:Protection:MaxConnectionsPerIp` | 16 (on by default; 0 disables) | hardening, no vmangos cap; shared by both listeners, the lower of it and the daemon cap wins (`docs/ops/netguard.md`) |
| `Auth:AutocreateAccounts` | false (also shipped) | retail has no autocreate |
| `World:WriterDrainGrace` | 00:00:00 (wait forever) | opt-in hardening, for example `00:00:05` |
| `World:PreAuthTimeout` | 00:00:10 | vmangos retail (`Network.TimeoutSecsIfNoAuth`) |
| `World:MaxQueuedWorldPackets` / `MaxQueuedWorldBytes` | 8192 / 8388608 (on by default) | hardening, no vmangos bound |
| `World:StrictMovementFiniteness` | false | opt-in hardening |
| `World:Social:MaxJoinedChannels` | 0 (unlimited) | opt-in hardening |

## Not delivered (limits)

* The wrong-password throttle (vmangos `LoginThrottle.cpp`, `WrongPass.MaxAttempts = 10` / 60 s) as retail shapes it,
  per account and address with a temporary ban row. A per-address failure budget with the same numbers
  (`Net:Protection:AuthFailureBurstPerIp`) is delivered by the netguard lane (`docs/ops/netguard.md`). The ban tables,
  IP bans, expiry, reasons, `IBanStore` and `AccountTool` ban verbs were delivered by the live-ban lane
  (`docs/security/live-bans.md`).
* World session-key age (vmangos `WorldSocket.cpp:287-290`), char-screen idle kick (900 s),
  overspeed-ping kick, malformed-packet strike policy, opcode >= 828 rejection. (The pre-auth deadline and the
  inbound world-queue bounds are delivered: see "Pre-auth deadline and inbound queue bounds" above. The `AddonInfo`
  response cap (F11), the frame read deadline and the per-address connection caps and rates are delivered by the
  netguard lane, `docs/ops/netguard.md`.)
* `IPacketGate`, generated per-opcode payload bounds, and the vmangos antiflood port.
  The antiflood must run at the drain point with per-pass counters; the update cadence has not
  been verified, so a kick default is not safe to ship yet.
* Chat hygiene (255-byte cap, invisible characters, link grammar, flood mute).
* The seeded fuzz/property harness and source-guard tests.
* Reconnect commands (0x02/0x03) are still unsupported by `LogonSession`.
* A real 1.12.1 client has not been run against any of this; the logon and world
  hardening is covered by protocol-level tests and the MockClient self-test only.

## Open questions for the developer

* Whether any hardening knob should ship enabled in the shipped appsettings (all default to
  retail now; an operator exposing the daemons to the internet will want the connection caps
  and read timeout).
* Whether the integrator should flip the README/M1 acceptance wording for autocreate.
