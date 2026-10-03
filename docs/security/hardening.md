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

`WorldSession.HandleAuthSessionAsync` answers `AUTH_BANNED` (0x1C) or `AUTH_SUSPENDED` (0x20)
for a non-active account after the digest check (vmangos `WorldSocket.cpp:283-345`).
`AuthResponseCode` gained `Unavailable 0x10`, `AlreadyOnline 0x1D`, `DbBusy 0x1F`, `Banned`,
`Suspended`, confirmed against `wow_messages world/enums/world_result.wowm:35,57,59,63,65`.
Not delivered: the `account_banned` / `ip_banned` tables, IP bans and session-key age (see
below).

### Stalled-writer teardown (F5)

`Kick()` only cancelled the read side, so `RunAsync` awaited a writer parked in `WriteAsync`
against a client with a zero TCP window; the socket, DI scope and up to 8 MiB of frames
leaked. Writes now take an abort token; after `WorldSessionOptions.WriterDrainGrace` (5 s)
the write is cancelled and the stream disposed. `StalledWriterTests`.

### Logon limits and input validation (F2 partial, F12)

* `Auth:MaxSessionDurationSeconds = 300` (vmangos `MaxSessionDuration`, `AuthSocket.cpp:76-82`).
* `Auth:ReadTimeoutSeconds = 30` for the rest of a packet after its command byte
  (hardening, slowloris; vmangos has none).
* Challenge body must be 31..47 bytes, `username_len <= 16`, locale in the vmangos allow-list;
  violations close without a reply as realmd does (`AuthSocket.cpp:248-262, 273, 213-230,
  306`).
* `Auth:StrictUsernameCharset = true`: names outside printable ASCII get `UnknownAccount`
  (hardening).
* Client text goes through `LogSafe.Escape` before logging (CR/LF/ESC and format characters
  escaped, 64-character cap) to stop log forging.
* `Realm/appsettings.json` ships `AutocreateAccounts: false` (was `true`). **Deviation from the
  WCell convenience, not from retail** (vmangos has no autocreate). README.md:104 and
  docs/M1_ACCEPTANCE.md:31 still describe enabling it; the integrator should add a note.

### Connection admission and accept-loop resilience (F7, listener half)

`Kernel/Net/AcceptLoop` keeps the loop alive through `SocketException`/`IOException`
(rate-limited log, 100 ms backoff); previously one error faulted the `BackgroundService` and
stopped the host. `ConnectionLimiter` enforces `Auth:` / `World:MaxConnections = 4096` and
`MaxConnectionsPerIp = 64` before a DI scope or session exists (0 disables; IPv4-mapped
addresses share the IPv4 bucket). Both are hardening with no vmangos equivalent. The world
`HandleClientAsync` no longer evaluates `RemoteEndPoint` outside its `try`.

### Movement validation (F8)

`MovementValidator` ports `VerifyMovementInfo` (`MovementHandler.cpp:1042-1061`,
`IsValidMapCoord` `GridDefines.h:175-203`): |x|,|y| <= MAP_HALFSIZE - 0.5 (17066.166),
|z| <= 400000, finite |o| <= 4 pi, on a transport |tx|,|ty| <= 250 and |tz| <= 100 and
position + transport offset itself a valid coordinate. Invalid packets are **dropped** (vmangos
behaviour) instead of kicking. Hardening beyond retail: pitch, jump speeds/angles, spline
elevation and transport orientation must be finite, because they were stored and relayed to
every observer verbatim. `MovementRelayTests` proved the relay before the fix.

### Channel cap (F12)

`ChannelManager.MaxJoinedChannels = 64` (0 = unlimited, retail). A refused join answers the
existing `INVALID_NAME` notify and creates no channel. Hardening: vmangos has no cap
(`ChannelMgr.cpp:52-69`).

## Configuration summary

| Key | Default | Source |
|---|---|---|
| `Auth:MaxSessionDurationSeconds` | 300 | vmangos retail |
| `Auth:ReadTimeoutSeconds` | 30 | hardening |
| `Auth:StrictUsernameCharset` | true | hardening |
| `Auth:MaxConnections` / `MaxConnectionsPerIp` | 4096 / 64 | hardening |
| `World:MaxConnections` / `MaxConnectionsPerIp` | 4096 / 64 | hardening |
| `Auth:AutocreateAccounts` (shipped) | false | hardening of a WCell convenience |
| `WorldSessionOptions.WriterDrainGrace` | 5 s | hardening (code default, not config-bound) |
| `ChannelManager.MaxJoinedChannels` | 64 | hardening (code default, not config-bound) |

## Not delivered (limits)

* `account_banned` / `ip_banned` tables, IP bans, ban expiry and reasons, `AccountTool`
  ban commands, and the wrong-password throttle (vmangos `LoginThrottle.cpp`,
  `WrongPass.MaxAttempts = 10` / 60 s). These need an Auth schema module and an `IBanStore`
  that the gm-commands lane may also want; the status enum is honoured at both daemons today.
* World session-key age (vmangos `WorldSocket.cpp:287-290`), pre-auth deadline
  (`Network.TimeoutSecsIfNoAuth = 10`), char-screen idle kick (900 s), overspeed-ping kick,
  inbound world-queue cap, malformed-packet strike policy, `AddonInfo` response cap (F11),
  opcode >= 828 rejection.
* `IPacketGate`, generated per-opcode payload bounds, and the vmangos antiflood port.
  The antiflood must run at the drain point with per-pass counters; the update cadence has not
  been verified, so a kick default is not safe to ship yet.
* Chat hygiene (255-byte cap, invisible characters, link grammar, flood mute).
* The seeded fuzz/property harness and source-guard tests.
* Reconnect commands (0x02/0x03) are still unsupported by `LogonSession`.
* `MaxJoinedChannels`, `WriterDrainGrace` are not bound to configuration.
* A real 1.12.1 client has not been run against any of this; the logon and world
  hardening is covered by protocol-level tests and the MockClient self-test only.

## Open questions for the developer

* Hardened defaults (connection caps, read timeout, channel cap, charset) versus unlimited
  retail behaviour: they are listed above so any can be set to 0 / false.
* Whether the integrator should flip the README/M1 acceptance wording for autocreate.
