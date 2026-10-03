# Codex net/auth review: status of each finding

Source: the independent Codex (gpt-6-sol) static review of the network/authentication surface at
main `49448fd` (`sec-codex/out/net-auth/report.md`). Findings 2-4 were fixed by the
`claude/vw2-security-hardening` lane (see `docs/security/hardening.md`); this lane re-verified
them with the exact attack sequences from the report and fixed finding 1.

Base of this lane: `d3b8059` (head of `claude/vw2-security-hardening` when branched).

## Finding 1 (high): unbounded inbound world packet queue - fixed here

`WorldSession._worldQueue` took every world-handler packet in `LoggingIn`/`InWorld` and the world
thread drains `MaxWorldPacketsPerTick` (150) per tick; the 0x2800 frame cap bounds one payload,
not the sum. A client flooding valid 12-byte `CMSG_MOVE_TIME_SKIPPED` frames grew the retained
arrays without limit.

Fix (`WorldSession`, `WorldSessionOptions`):

* `World:MaxQueuedWorldPackets` (default 8192) and `World:MaxQueuedWorldBytes` (default 8 MiB);
  0 disables a bound. The check runs on the single producer (the read loop) before the packet is
  queued, so the queue can never exceed either bound. Over the bound the session logs a warning
  with counts and limits only (no payload) and disconnects.
* Accounting (`QueuedWorldPackets`, `QueuedWorldBytes`) is decremented at every dequeue, including
  the drop paths of `ProcessWorldPackets`, and by `DiscardQueuedPackets` (used by login start,
  logout and `Close`) instead of `ConcurrentQueue.Clear`, so the counters cannot drift.
* Why the defaults are safe: the drain capacity is 150 per 50 ms tick (3000/s) while a retail
  client sends a few tens of packets per second, so 8192 only fills after the world thread has
  stalled for minutes or when the peer floods. The byte bound mirrors the 8 MiB outbound bound.
* Reference behaviour: vmangos `WorldSession::QueuePacket` pushes into `m_recvQueue` with no
  bound (`src/game/Server/WorldSession.cpp:307-332`), and mangos-classic does the same
  (`src/game/Server/WorldSession.cpp:256-285`). The cap is therefore hardening that does not
  change behaviour for any legitimate client; it is the one default that is not "off", because
  the defaults are far above anything a retail client reaches (a deliberate deviation, set to 0
  to restore the unbounded retail behaviour).

Evidence (`tests/ArcaneCore.World.Tests/Security/InboundQueueCapTests.cs`; the world thread is
parked on a gate so queue depth depends on what was sent, not on scheduling):

* RED with the options and accounting present but no enforcement (equivalent to main for this
  path): both flood tests fail because the client is never disconnected (10 s read timeout).
* GREEN with enforcement: flood is disconnected, the polled high-water mark never exceeds the
  bound, counters return to zero, and the warning contains no payload data. Traffic at exactly the
  bound, a 3000-packet burst under default options, and a client disconnect with packets queued
  all behave and release their accounting.

## Finding 2 (medium): slow unauthenticated connections - logon closed by the branch, world fixed here

Attack sequences replayed as worded: logon `0x00` plus a challenge header with size `0xFFFF` and the
body withheld; world connect and withhold the packet header, or send one byte and stop.

* Logon, oversized claim: **already closed** by the hardening branch (`2bb77eb`). The challenge size
  window 31..47 is checked before anything is allocated, and the connection is closed without a
  reply (vmangos `AuthSocket.cpp:248-262`). Test:
  `CodexNetAuthRealmTests.ChallengeHeaderClaiming0xFFFF_...` (default options). RED on main
  (`49448fd`: the session waits for the 65535-byte body until the session limit), GREEN here.
* Logon, valid size with a withheld body: bounded by `Auth:MaxSessionDurationSeconds` (300, the
  retail rule) by default and by `Auth:ReadTimeoutSeconds` when set (default 0 = off, retail).
  Existing test `LogonHardeningTests.StalledPacketBody_IsClosedAtReadTimeout` covers the timeout.
* World, withheld or trickled header: **NOT closed by the hardening branch** (its own limits list
  says "pre-auth deadline ... not delivered"; `ReadLoopAsync` read with only the host token). Fixed
  here: `WorldSessionOptions.PreAuthTimeout` (`World:PreAuthTimeout`, default 10 s), a timer started
  at `RunAsync` that kicks a session still in `Connected` when it fires and never touches an
  authenticated session. That is the retail rule, not a deviation: vmangos
  `Network.TimeoutSecsIfNoAuth = 10` (`WorldSocket.cpp:621-628`, `mangosd.conf.dist.in:3039`).
  `0` disables it. Tests `CodexNetAuthWorldTests.WithheldPacketHeader_...`,
  `OneByteOfTheHeader_...`; `AuthenticatedSession_IsNotKilledByThePreAuthDeadline` proves a session
  that authenticated in time idles past the deadline unharmed. RED before the timer (20 s budget
  exhausted, connection never closed), GREEN after.
* Caps: `ConnectionLimiter` admission (`Auth:`/`World:MaxConnections`, `MaxConnectionsPerIp`) is the
  hardening branch's work. The new tests drive the real `LogonServer` and `WorldServer`: stalled
  connections fill the cap, a further connection is refused before a session exists, the deadlines
  cut the stalled ones off, and the slots are returned
  (`StalledConnections_CannotExceedTheCap_AndTheirSlotsComeBack`, one per daemon).
* Residual: the caps default to 0 (unlimited) and `Auth:ReadTimeoutSeconds` defaults to 0, so an
  operator exposing the daemons must set them (the hardening doc already lists this as an open
  question). The world has no idle deadline after authentication (retail has none either; a client
  that stalls mid-frame after login is held until it disconnects or its outbound queue overflows).

## Finding 3 (medium): SRP proof replay restoring an older key - closed by the hardening branch

Sequence replayed as worded: complete challenge and 74-byte proof on socket A (K1); complete a second
logon on socket B (key becomes K2); resend `0x01` plus the original proof on the still-open socket A.

* Closed by `2bb77eb` (`ResetChallengeState` on every challenge and proof; one proof per challenge,
  vmangos `AuthSocket.cpp:327`, `:555`). Test `CodexNetAuthReplayTests` asserts the replay is
  answered with a failure, the stored key stays K2 (K1 is not written back), and the replayed
  connection is not served the realm list (it is no longer authenticated).
* RED on main `49448fd`: the replay is answered with `AUTH_LOGON_PROOF` success (the test fails on
  `Expected: Not 0, Actual: 0`), reproducing the report. GREEN on the branch.
* The last step of the report (using K1 in a fresh world `CMSG_AUTH_SESSION`) needs no new test: the
  world checks the stored key, which stays K2, and `WorldHandshakeTests.WrongDigest_IsRejected`
  covers a digest built from any other key.

## Finding 4 (medium): banned/suspended account with an existing key - closed for new logins; live sessions are a residual risk

Sequence replayed as worded: key issued while Active, a world login succeeds, the status becomes
Banned or Suspended, a fresh connection sends a correct `CMSG_AUTH_SESSION`.

* Closed by `d3b8059`: `HandleAuthSessionAsync` answers `AUTH_BANNED` (0x1C) for any non-Active
  account after the digest check (vmangos `WorldSocket.cpp:333-345`) and never registers the
  session. `WorldAuthStatusTests` (hardening branch) seeds a non-Active account directly;
  `CodexNetAuthStatusTests` adds the exact order of events (key issued while Active, working login,
  then the status flip, stored key untouched). RED on main (the account is admitted: the reply
  header is already encrypted), GREEN on the branch, for both `Banned` and `Suspended`.
* Not closed, and not claimed by the hardening doc: disconnecting an already-connected world session
  when the status changes, and revoking the stored key. `IAccountStore` has no status mutation and
  nothing raises a status-change event, so in this code base the status can only change through the
  database or an external tool, and a world session that is already connected keeps running until it
  disconnects. This needs a design decision (ban store plus a kick path through `SessionRegistry`) and
  belongs with the `IBanStore` work listed in `hardening.md`; it was not implemented here.
