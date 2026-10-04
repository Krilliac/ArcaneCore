# Network and packet protections (lane `netguard`)

What the logon and world listeners do to a client that sends too much, too slowly, too often or
not what the layout says, and the single rule every one of these follows: **a refused client is
closed and one rate-limited log line is written; no limit ever throws into an accept loop, a
session task or the world thread.** Reference sources (vmangos first) were read, never copied;
where a rule has no retail equivalent this page says so. The configuration lives under one
section, `Net:Protection` (`NetProtectionOptions`, `src/ArcaneCore.Kernel/Configuration`), and is
listed with its defaults in the generated [configuration reference](../reference/configuration.md#netprotection).

## 1. Protocol: bounds-checked readers

### What was found

`PacketReader` (`src/ArcaneCore.Protocol/PacketReader.cs`) is the one reader every handler uses
(133 call sites). Its contract was "a read past the end throws `ArgumentOutOfRangeException`;
handlers treat that as a malformed packet", and `WorldSession` caught exactly that type on both
dispatch paths. The audit found:

| Reader | Finding | Fix |
|---|---|---|
| `PacketReader.ReadByte` / `ReadPackedGuid` | Indexed the span directly (`_data[_position++]`): a truncated packet threw **`IndexOutOfRangeException`**, which neither catch clause matched. On the session task it surfaced as a logged "session error"; on the **world thread** (`ProcessWorldPackets`) it left the handler uncaught and reached the map update. | Every read is now bounds-checked against `Remaining` before it touches the span and funnels into `MalformedPacket.Throw`, so the only exception type a read can produce is the controlled one. `ReadPackedGuid` checks the mask's byte count before consuming the mask. |
| `PacketReader.ReadCString` / `ReadCStringBytes` | No length bound. Inside a client frame the frame bound (0x2800) limits it; over a decompressed buffer it does not. | `MaxCStringBytes = 0x2800` by default (no legitimate client string can exceed the frame), explicit `ReadCString(maxBytes)` overloads for tighter bounds. |
| `PacketReader` (all) | No non-throwing surface: a handler could only refuse a packet by catching. | `TryReadByte/UInt16/UInt32/Int32/UInt64/Single/PackedGuid/Bytes/CString/CStringBytes`, `TrySkip`, `TryReadCount(max)` (u32 count bounded before anything is allocated) and `TryReadByteCount(max)`. They return false and leave the cursor in place; they allocate nothing (`TryReadCString` allocates its string, use the `Bytes` twin on a hot path). |
| `MovementInfo.Read` | Threw on truncation (the documented contract) through the primitives above, so a 0-byte movement packet took the `IndexOutOfRangeException` path. | `MovementInfo.TryRead` (allocation-free, cursor untouched on failure); `Read` delegates to it and throws the controlled outcome. |
| `WorldSession.HandleAuthSessionAsync` | Read the account name without a bound and copied the digest and addon block into fresh arrays. | `AuthSessionRequest.TryParse` (`World/Net`): account name bounded at 16 bytes (the auth schema's `Username` length and realmd's `AUTH_LOGON_MAX_NAME`), digest and addon block are windows over the payload, nothing copied, nothing thrown. |
| `WorldSession.HandlePing` | Threw the controlled exception on a short payload. | `TryHandlePing` uses `TryReadUInt32`; a short ping disconnects without an exception. |
| `AddonInfo.BuildResponse` | Decompresses up to 1 MiB and read each addon name unbounded; a block with tens of thousands of records built a response larger than the SMSG size field, and `Send` then threw `ArgumentException` out of the authentication handler (the "AddonInfo response cap (F11)" limit in `docs/security/hardening.md`). | Names bounded at 256 bytes (`MaxAddonNameBytes`), at most 4096 records (`MaxAddons`), and the list ends before the response could exceed one SMSG payload (`MaxResponseBytes`). A record that does not fit ends the list; nothing throws. |
| `LogonChallengeRequest.TryParse`, `LogonProofRequest.TryParse` (Realm) | Already `Try` readers with every slice bounded. | No change; covered by the fuzz. |
| `WorldSession` catch clauses | Caught `ArgumentOutOfRangeException` only. | `IsMalformed` names the controlled outcome in one place (`MalformedPacket.Is`) and nothing else. An `IndexOutOfRangeException` (or any other exception) from a handler is a **server bug**, not a client fault: it is not caught as malformed, so it reaches the existing Error log with its stack trace (`WorldServer` "session error" on the session task, `Map.ProcessPackets` "packet handling failed" on the world thread) and the connection is closed there. The first version of this lane classified `IndexOutOfRangeException` as malformed, which kicked an innocent player with a Warning and no stack trace; `WorldProtectionTests` pins the split. |

### Why the controlled outcome is still `ArgumentOutOfRangeException`

A dedicated `MalformedPacketException` would have been cleaner. It is not possible in this lane:
the 130 handler call sites live in areas other lanes are editing right now, and existing tests pin
the exact type with `Assert.Throws<ArgumentOutOfRangeException>` (xunit refuses a derived type).
`MalformedPacket.Throw` therefore throws exactly that type with `ParamName = "packet"`, and
`MalformedPacket.Is` names it in one place. Converting the handlers to the `Try` surface, one area
at a time, is the follow-up; the fuzz harness is the proof that the throwing surface is already safe.

### The fuzz harness

`tests/ArcaneCore.World.Tests/Net/PacketFuzzTests.cs` and `tests/ArcaneCore.Realm.Tests/Security/LogonPacketFuzzTests.cs`.
The shapes are the real client packets the end-to-end tests send: CMSG_AUTH_SESSION with a zlib
addon block, every MSG_MOVE_* flag combination (transport, swimming, jumping, spline elevation),
CMSG_PING, CMSG_MOVE_TIME_SKIPPED, CMSG_CHAR_CREATE, packed GUIDs, the logon challenge and proof
bodies. A seeded `Random` (three fixed seeds, 3000 to 4000 mutations each) applies truncation, bit
flips, extreme length/count/mask bytes, 32-bit extremes, insertions, deletions, junk tails, lost
terminators and plain noise. Per mutation the harness asserts:

- the throwing surface throws nothing but the controlled outcome (`ParamName == "packet"`);
- the `Try` surface never throws, fails exactly when the throwing surface does, and leaves the
  cursor in place when it fails; on success both read the same values to the same position;
- the cursor never passes the end of the buffer, on either surface;
- `AuthSessionRequest.TryParse` never throws and its windows lie inside the payload;
  `AddonInfo.BuildResponse` never throws and never exceeds one SMSG payload;
- a random program of reader primitives over a random buffer agrees between the two surfaces.

A failure prints the seed, the iteration and the bytes, so it replays. `PacketReaderTests` pins
the regression (`ReadByte` on an empty payload is the controlled outcome) and that the `Try`
surface allocates zero bytes (`GC.GetAllocatedBytesForCurrentThread`).

## 2. Transport: what `ConnectionLimiter` did and what was added

`ConnectionLimiter` (`Kernel/Net`) already enforced a global cap and a per-address cap before any
session or DI scope existed, keyed by address with IPv4 and its IPv4-mapped IPv6 form as one key,
with both daemon keys (`Auth:`/`World:MaxConnectionsPerIp`) defaulting to 0 = unlimited. It is
kept and composed, not duplicated: `NetGuard` (`Kernel/Net`) owns one limiter whose per-address
cap is `EffectivePerIpCap(daemon, Net:Protection:MaxConnectionsPerIp)`, the lower of the two set
values, plus the per-address table and the log gates. One `NetGuard` per listener, built in
`LogonServer` / `WorldServer` when they start and handed to every session they admit.

| Protection | Where | Retail | Behaviour when hit |
|---|---|---|---|
| Per-address connection cap (`MaxConnectionsPerIp`, default 16) | `NetGuard.TryAdmit` in both accept callbacks, before a scope exists | none (vmangos has no per-address cap); the daemon keys stay 0 = "no daemon cap" and the shared cap applies. **A non-retail default that is on**: it bounds a crowd behind one NAT address at 16 simultaneous connections per listener, so it is listed in the release-caveat register of `docs/guide/operations.md` (asserted against the code default by `OperationsGuideTests`); `0` restores retail | socket closed, no reply |
| Per-address connection rate (`ConnectionBurstPerIp` 100, `ConnectionsPerMinutePerIp` 300) | same | none | socket closed, no reply; the cap slot it took is released |
| Per-address **failure** budget (`AuthFailureBurstPerIp` 10, `AuthFailuresPerMinutePerIp` 10) | `LogonSession.HandleChallengeAsync` after the body is read and before the locale check, the IP-ban lookup and the account lookup; `WorldSession.HandleAuthSessionAsync` after the parse and before the account lookup | the shape of vmangos realmd `WrongPass.MaxCount` (10 per 60 s, `LoginThrottle`), keyed by address | logon: `FAIL_NOACCESS` then close (what realmd answers a refused address, AuthSocket.cpp:338-352); world: `AUTH_FAILED` then close. Checked with `Peek` (consumes nothing); charged with `RecordAuthFailure` on unknown account, wrong proof or digest, banned or suspended account, bad locale or name length, malformed body. A wrong client build and a degenerate stored verifier are not the client's guess and are not charged. Successes never consume the budget, so players behind one NAT address are unaffected. |
| Max frame size before allocation | `WorldSession.ReadLoopAsync` (`size` outside 4..0x2800 or an opcode above 16 bits; vmangos `handle_input_header`) and `LogonSession.HandleChallengeAsync` (body outside 31..47, realmd `sAuthLogonChallengeBody`) | retail | already present; verified: the payload array is allocated only after the check |
| Frame read deadline (`FrameReadTimeout`, default 30 s) | `WorldSession.ReadLoopAsync`: armed when the first header byte is in, covers the remaining header bytes and the payload, disarmed when the frame is complete; `LogonSession.ReadPacketPartAsync`: the bytes after the command byte. `Auth:ReadTimeoutSeconds`, when set, takes precedence on the logon daemon | none (a retail client writes each frame in one send); slowloris | connection closed, `ReportFrameTimeout` line. Waiting for the *first* byte is idle time and is not bounded here: `World:PreAuthTimeout` bounds it before authentication, and a retail client may idle at the character screen |
| Logon unauthenticated lifetime (`LogonUnauthenticatedLifetime`, default 30 s) | `LogonSession.RunAsync`: a linked source cancelled after the lifetime, disarmed by the first successful proof | retail realmd has only `MaxSessionDuration` (300 s, kept as `Auth:MaxSessionDurationSeconds`); mangosd's `Network.TimeoutSecsIfNoAuth` is the existing `World:PreAuthTimeout` (10 s) | connection closed, `ReportUnauthenticatedTimeout` line |
| Outbound queue byte cap | `WorldSessionOptions.MaxOutboundBytes` (8 MiB, `World:MaxOutboundBytes`): `Send` kicks the session when the unsent bytes exceed it; `WriterDrainGrace` tears the stream down | already present; verified | `LogonSession` has no queue: it awaits each small write directly, bounded by the session lifetime |
| Log rate limiting (`LogInterval`, default 10 s) | `LogGate` (`Kernel/Logging`), one per kind of refusal inside `NetGuard` | n/a | one line per interval per kind, carrying the count of refusals it swallowed; `TryEnter` is two interlocked operations and allocates nothing |

### The per-address table

`IpRateTable` (`Kernel/Net`): a fixed array of slots (`MaxTrackedAddresses`, default 4096,
rounded up to a power of two, about 48 bytes each, allocated once), open addressing with a probe
window of 8, each slot one `IpKey` (16 bytes, the IPv6 form so IPv4 and ::ffff:a.b.c.d share a
key), a last-seen time and two token buckets (connections, failures) refilled from the same
timestamp. Looking up, taking a token and evicting allocate nothing (pinned by a test).

Eviction: a slot whose address has not been seen for `AddressIdleEviction` (default 10 min) may be
reused by a newcomer; the least recently seen idle slot in the probe window is taken first. When
every slot of a newcomer's window is busy and none is idle, the newcomer is **still admitted**: the
table forgets the least recently seen address of the window, preferring one that carries no
limiting state (both buckets full after refill, so nothing is lost by forgetting it), counts it in
`ForcedEvictions`, and `NetGuard` writes one rate-limited line ("the per-address table is full ...
nothing was refused") so the operator can raise `MaxTrackedAddresses`. The table is a bounded
measuring device: full, it measures less; it is never by itself the reason a connection or an
authentication attempt is refused. The fail-closed limit on the number of clients is the global
connection cap (`Auth:`/`World:MaxConnections`), which does not depend on the table.

The first version of this lane refused newcomers on a full probe window (`RateVerdict.Saturated`).
That turned the table into a lockout lever: one connection each from a few thousand distinct
addresses (one IPv6 /64 suffices when the listener binds `::`) filled the windows for ten minutes
and every fresh legitimate address whose window was full was closed on accept or answered
`AUTH_FAILED` / `FAIL_NOACCESS`, while tracked players were unaffected and the operator saw one
rate-limited line. The trade-off of evicting instead is accepted and stated: an address that is
being limited can be forgotten (its budget starts over) if its whole probe window is crowded out
by eight addresses seen more recently that are all themselves being limited; an attacker who can
do that holds that many addresses and is bounded by the global cap and the per-address rates on
each of them. `IpRateTableTests` pins that a full table admits a newcomer, prefers to forget an
address that is not being limited, and allocates nothing while doing so. `check-config` warns
when `AddressIdleEviction` is shorter than the time a spent failure budget needs to refill,
because a limited address could then idle out and start over.

Threading and ownership: one table per listener behind one lock; touched from the accept callback
(`TryAdmit`) and from session tasks (`AllowsAuthAttempt`, `RecordAuthFailure`). A session computes
its `IpKey` once in its constructor from the endpoint string (`IpKey.TryParse`), never per
attempt. The clock is `Environment.TickCount64` through a cached delegate (`Clock.Milliseconds`),
injectable for tests, monotonic so a wall-clock jump cannot refill or starve a bucket.

### Options

| Key | Default | Meaning | Fail-closed behaviour |
|---|---|---|---|
| `Net:Protection:MaxConnectionsPerIp` | 16 | simultaneous connections per address, combined with the daemon's own cap (lower wins); 0 disables this side | socket closed before a scope exists |
| `Net:Protection:ConnectionBurstPerIp` | 100 | connections one address may open at once; 0 disables the rate limit | socket closed, cap slot released |
| `Net:Protection:ConnectionsPerMinutePerIp` | 300 | refill rate of the connection budget | `check-config` warns when 0 with a burst set (never refills) |
| `Net:Protection:AuthFailureBurstPerIp` | 10 | failed authentication attempts per address before refusal; 0 disables | refused before any lookup, failure code sent, connection closed |
| `Net:Protection:AuthFailuresPerMinutePerIp` | 10 | refill rate of the failure budget | `check-config` warns when 0 with a burst set |
| `Net:Protection:MaxTrackedAddresses` | 4096 | table slots, allocated at start (1..1048576) | a full table admits the newcomer and forgets the least recently seen address of its probe window (one rate-limited line); never a refusal, the connection caps are the fail-closed limit |
| `Net:Protection:AddressIdleEviction` | 00:10:00 | after this long unseen, a slot may be reused | `check-config` warns when shorter than a failure budget's refill time |
| `Net:Protection:FrameReadTimeout` | 00:00:30 | budget for the rest of a frame after its first byte; 00:00:00 disables | connection closed, one rate-limited line |
| `Net:Protection:LogonUnauthenticatedLifetime` | 00:00:30 | longest a logon connection lives without a proof; 00:00:00 disables | connection closed, one rate-limited line |
| `Net:Protection:LogInterval` | 00:00:10 | shortest interval between two lines about one kind of refusal | n/a (00:00:00 logs every refusal) |

Every value is validated by `check-config` and at every world start (`NetProtectionConfigChecks`,
`World/Ops/Validation`, run by `OpsCli.Validate`); a value that would make a protection
meaningless is an error (exit 78), a weakening one a warning. The logon daemon has no
`check-config`; `NetGuard` clamps negatives to 0 and an unusable table size or idle window to the
default, so a bad value can only disable one limit, never crash the listener. Nothing under
`Net:Protection` is in the `.reload config` set: the guard, its table and its gates are built when
the listener starts (restart-only, like `World:Port`). The shipped `appsettings.json` files do not
carry the section; the defaults apply.

Wiring: one line in each `Program.cs`, `builder.Services.AddNetProtection(builder.Configuration);`
(`ArcaneCore.Realm.Net.NetProtectionServiceExtensions`, `ArcaneCore.World.NetProtectionServiceExtensions`),
which binds the section. `LogonServer` and `WorldServer` take `IOptions<NetProtectionOptions>?` as a
trailing optional constructor parameter and fall back to `new NetProtectionOptions()`, so a host
that omits the line (or the tests, which construct them directly) runs with every protection on.
`LogonSession` and `WorldSession` take `NetGuard?` as a trailing optional parameter; without a
guard they still apply the default frame deadline and (logon) lifetime from a static default, and
skip only the per-address budgets, which need the guard's table.

### Deviations from retail

| Deviation | Switch |
|---|---|
| A per-address connection cap (16 simultaneous connections per address per listener) and connection rate exist and are on by default; vmangos has neither. The cap is in the release-caveat register (`docs/guide/operations.md`) as a non-retail default. | `Net:Protection:MaxConnectionsPerIp = 0`, `ConnectionBurstPerIp = 0` restore unlimited |
| The failure budget is per address and counts every failure kind, including banned-account attempts and malformed bodies; vmangos `WrongPass` counts wrong passwords per account and address and is off by default. | `AuthFailureBurstPerIp = 0` |
| A frame deadline after the first byte; vmangos has none (a client that stalls mid-frame holds its socket until the session cap). | `FrameReadTimeout = 00:00:00` |
| A 30 s unauthenticated lifetime on the logon daemon in addition to the 300 s session cap. | `LogonUnauthenticatedLifetime = 00:00:00` |
| The SMSG_ADDON_INFO list is capped (256-byte names, 4096 records, one SMSG payload); vmangos answers every record. A retail client has a few dozen to a few hundred addons and never meets the cap. | none (structural: the SMSG size field cannot carry more) |
| A world CMSG_AUTH_SESSION whose account name exceeds 16 bytes is malformed; vmangos reads it unbounded. No such account can hold a session key (schema and realmd bound). | none |

## 3. ArrayPool / MemoryPool discipline

Audit of `src/ArcaneCore.Protocol`, `src/ArcaneCore.Kernel`, `src/ArcaneCore.Realm` and
`src/ArcaneCore.World/Net`: there is **no** `ArrayPool`, `MemoryPool` or `Rent`/`Return` anywhere in
`src`, so there is no missing return to fix. The per-frame buffers are plain arrays owned by the
session (`WorldSession.ReadLoopAsync` allocates one payload per frame and hands it to the handler
or the world queue; `Send` allocates one frame per packet and hands it to the writer channel).
Pooling them was considered and not done in this lane: world handlers receive the payload array
and may retain it, and the queued frames leave the session through a channel, so a pooled buffer
would need an owner type across every handler signature (the `Game` and handler areas are owned by
other lanes). What this lane did make allocation-free is everything on the protection path: the
`Try` reader surface, `IpKey.From` (stack span), the table, the gates and the refusal paths of
`NetGuard` (tests pin each with `GC.GetAllocatedBytesForCurrentThread`), and the frame deadline,
which is one `ReadDeadline` (one linked `CancellationTokenSource`) per connection re-armed with
`CancelAfter` per frame instead of one linked source per read as the logon session did before.

## Tests

`tests/ArcaneCore.Realm.Tests/Security`: `LogGateTests` (interval, counts, concurrency, zero
allocation), `IpRateTableTests` (budgets, refill, independence of the buckets, peek, disabled
bucket, idle eviction, a full table admitting a newcomer by forgetting the least recently seen or
unlimited address, key normalisation and hash spread, zero allocation of take / peek / evict and of
`IpKey.From`), `NetGuardTests` (effective cap, shared and daemon caps, connection rate releasing
the cap slot, failure budget charged on failure only, full table admitting with one line,
rate-limited lines with suppressed counts, zero allocation on the refusal path),
`LogonProtectionTests` (over loopback: failure budget refusing with `FAIL_NOACCESS` and closing,
success not consuming the budget, unauthenticated lifetime, frame deadline, `Auth:ReadTimeoutSeconds`
precedence, the real listener refusing by rate and running with defaults when nothing is
registered), `LogonPacketFuzzTests`. `tests/ArcaneCore.World.Tests/Net`: `PacketReaderTests`,
`PacketFuzzTests`, `AuthSessionRequestTests`, `WorldProtectionTests` (frame deadline fires on a
stalled frame and not on an idle connection, failure budget before the lookup with `AUTH_FAILED`,
the real listener refusing by rate before a session exists, a short in-world packet contained on
the world thread and the sender kicked, a handler's `IndexOutOfRangeException` reported as a server
fault and never as "malformed"); `tests/ArcaneCore.World.Tests/Ops/NetProtectionConfigChecksTests`.
The pre-existing realm and world security suites (`ConnectionAdmissionTests`, `LogonServerLimitTests`,
`CodexNetAuth*`, `InboundQueueCapTests`, `StalledWriterTests`) run unchanged against the composed guard.

## Limits (not delivered)

- Handler call sites keep the throwing reader; the `Try` surface is used by the readers this lane
  owns (`MovementInfo`, `AuthSessionRequest`, `AddonInfo`, `TryHandlePing`). Converting the 130
  handler reads is per-area work for the lanes that own them.
- No pooled frame buffers (see section 3).
- The failure budget is per address only; vmangos `WrongPass` also counts per account and can write
  a temporary ban row (`WrongPass.BanType`). The ban tables belong to the live-ban lane.
- `Net:Protection` is restart-only; a `.reload config` row would need the guard to re-read its
  options per use, which the table's fixed size does not allow without a rebuild.
- The logon daemon has no `check-config` verb; its values are clamped, not reported.
- The connection-rate and failure budgets are per listener process; a cluster of realms shares
  nothing.
