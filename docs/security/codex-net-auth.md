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
