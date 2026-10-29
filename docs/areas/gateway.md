# Gateway passthrough (Tier 3a)

Status: **built, mock-client tested, not yet accepted with a real 1.12.1 client.** This is phase 3a of
[Hot reload tiers](../design/hot-reload-tiers.md#53-phases): a separate `ArcaneCore.Gateway` process owns the client
socket and forwards plaintext opcodes to a World running in worker mode. There is no handoff yet: a gateway restart drops
its clients, and a World restart drops them too. With the default configuration nothing here is active and the World
runs exactly as before (`Combined` mode).

Related: [Clustering design](../CLUSTERING_DESIGN.md) (M15.1 gateway target), [netguard](../ops/netguard.md),
[anticheat](anticheat.md), [configuration reference](../reference/configuration.md#clusterworker).

## Topology

```
1.12.1 client --TCP, header cipher--> ArcaneCore.Gateway --session link (TCP, plaintext opcodes)--> ArcaneCore.World (Cluster:Worker:Enabled=true)
                                         |                                                              |
                                         +-- auth database (session key, status, bans) <----------------+
```

| Piece | Code | What it does |
|---|---|---|
| Gateway daemon | `src/ArcaneCore.Gateway` (`GatewayServer`, `GatewayClientConnection`) | Client listener with a `NetGuard` built from `Net:Protection`; `SMSG_AUTH_CHALLENGE`; the `CMSG_AUTH_SESSION` digest check against the session key the realm stored; build, account status and account/IP ban checks with the same plain-text refusals as `WorldSession.HandleAuthSessionAsync`; the 1.12 header cipher; `CMSG_PING` answered with `SMSG_PONG` in every state; one connection per account (a newer login closes the older one); the outbound byte bound; reconnects the worker link with backoff (1 s doubling to 30 s) |
| Session link | `src/ArcaneCore.World/Cluster/Gateway` (`GatewaySessionWire`, `GatewaySessionLinkClient`, `GatewaySessionLinkHost`, `GatewayLinkChannel`) | Frame types 11 to 17 on the length-prefixed `GatewayWorkerTransport` framing. Every packet travels in the `ClusterSessionPacket` envelope: session generation, account, owner epoch, sequence |
| Worker endpoint | `GatewayWorkerEndpoint`, `WorldGatewaySessionAcceptor`, `RemoteWorldSession` | Hosted service registered only when `Cluster:Worker:Enabled` is `true`; one remote-mode `WorldSession` and one DI scope per forwarded client |
| Remote session | `src/ArcaneCore.World/Net/WorldSession.Remote.cs` | `WorldSession` without a socket: input arrives through a bounded queue, output goes to the link (`WorldSession.Send` branches to `SendRemote`); the normal `DispatchAsync` handles every opcode |

### Login through the gateway

1. The gateway sends `SMSG_AUTH_CHALLENGE`, checks `CMSG_AUTH_SESSION` against the auth database, and refuses in plain
   text exactly as the World's own handshake does (unknown account, wrong digest, bad build, banned, suspended).
2. It claims the account on the gateway, takes the next session generation and sends `SessionOpen` (account id and name,
   build, the client's `ip:port`).
3. The worker re-checks account status, account and IP bans of the forwarded address and its own account claim
   (`SessionRegistry`), then answers `SessionOpenReply` with the owner epoch (`Cluster:Fencing` epoch, or 1 when fencing
   is off). A refusal code is sent to the client in plain text.
4. On acceptance the gateway initializes the header cipher and sends `SMSG_AUTH_RESPONSE` (Ok) and `SMSG_ADDON_INFO`.
   Anything the worker sent before its reply was queued waits and follows the reply, so nothing reaches the client before
   the auth response.
5. From then on client packets go to the worker and server packets come back, each with the next sequence number.

### Envelope rules (both ends)

A packet is dropped and counted (`GatewaySessionLinkHost.DroppedClientPackets`, `GatewaySessionLinkClient.DroppedServerPackets`)
when its generation is unknown on that link, its account is not the session's, its owner epoch is not the session's, or its
sequence is not above the last one accepted. A `SessionOpen` whose generation is not above the highest one the link has seen
is refused. The gateway takes a session's generation and queues its `SessionOpen` under one lock on the link
(`GatewaySessionLinkClient.OpenAsync(nextGeneration, requestFor, ...)`), so logins that arrive together still reach the
worker in generation order and none is refused as stale. A frame that does not decode closes the link, and every session
on it ends (`LinkLost`).

### Link admission

The worker sends a 32-byte random challenge; the gateway answers with HMAC-SHA256 of it keyed by the shared link secret,
then registers a `Gateway`-role manifest (realm id, contract version, content revision, schema versions). The worker
checks the proof, the role and the manifest before it accepts any session. Worker mode refuses to start unless
`Cluster:Worker:LinkSecret` is at least 16 characters, **on every bind address, loopback included**: with an empty secret
the proof is an HMAC any local process can compute, and the manifest values are public defaults. The gateway likewise
refuses to start unless its own `LinkSecret` (the `Gateway` section below) is at least 16 characters. **An admitted gateway can open a session for any
account it names (the worker checks the account's status and bans, not a session-key proof), so the link secret is as
sensitive as a database password.**

What the link does not protect:

- **The worker is not authenticated to the gateway.** The gateway answers any challenge; it never checks that the worker
  knows the secret. A process that binds the worker port before the World does receives every client's plaintext opcodes.
- **The link is not encrypted and frames carry no MAC after the handshake.** Anyone on the path can read and inject.
  Keep the link on loopback or a private network you control.

## Running it

World side (`world.conf` / `appsettings.json`), documented with the other keys in the
[configuration reference](../reference/configuration.md#clusterworker):

```
Cluster.Worker.Enabled = true
Cluster.Worker.BindAddress = "127.0.0.1"
Cluster.Worker.Port = 8095
Cluster.Worker.LinkSecret = "<long random string>"
```

The World's own client listener (`World:Port`) stays open in worker mode; point the realm list at the gateway's port to
send clients through the gateway.

Realm liveness in this topology belongs to the operator, not to a world. A combined-mode world keeps its realm-list row current: offline
while starting, online while its loop ticks, offline at stop, with a heartbeat the logon daemon judges (`World:Realm:Id`,
[installation](../guide/installation.md) section 4, [realm list liveness](../ops/realm-liveness.md), vmangos Master.cpp:215/:330). A worker
does not (neither state nor heartbeat; having the gateway own liveness would need the worker's tick counter on the link and is not built): an active and a standby worker serve the same realm, so the old active world going offline after a handoff would hide a realm
the promoted standby is serving, and the client-facing endpoint is the gateway, which has no realm-list writer. Set the row's flags with
SQL (`realmlist.Flags`, Offline = 0x02) when the gateway itself is taken down, and keep `realmlist.State` at 0 (Unknown) so the logon daemon
enforces no heartbeat for a row no world writes: a row left at another state by an earlier combined-mode world would go offline for good.

Worker mode makes character fencing **mandatory** (Tier 3b groundwork): with `Cluster:Worker:Enabled = true` the
characters write fence is registered whatever `Cluster:Fencing:Enabled` says, and the World refuses to start unless
`Cluster:Fencing:Enabled = true` too. Combined mode (worker off) is unchanged.

### Standby worker (Tier 3b groundwork)

`Cluster:Worker:Standby = true` (needs `Enabled`) starts a second World on the same databases as a handoff target. It
builds its services, validates the schema without DDL (`Database:Upgrade:ValidateOnly`), takes no characters writer lock,
claims no ownership, registers no `World:Port` listener, and links to a gateway; but it attaches no feature and starts no
simulation (features read the characters database, which the active World still owns: the fence refuses a non-owner's
transactions, reads included), and it refuses every session open with `AUTH_UNAVAILABLE` (`WorkerAdmission`). It runs
only after `WorldHost.PromoteAsync(token)`: the token must be the ownership the database holds, under this process's
incarnation, as the handoff's epoch takeover leaves it. Promotion only reads the ownership row
(`ICharacterOwnershipStore.GetCurrentAsync`); a refused promotion claims nothing, even when the row is released. There is no operator command for it yet; the drain protocol
below drives it in process.

### Whole-World handoff (Tier 3b groundwork, in process only)

`WorldHandoffCoordinator` (`src/ArcaneCore.World/Cluster/Handoff/`) runs the drain protocol of design section 5.3 over
three participants and journals each stage in the characters database (`cluster_world_handoff`, schema 62):
B warm, A stops admissions, A freezes at a tick boundary (`LocalWorldHandoffSource` parks the simulation thread after
capturing every online player's save), the saves and every per-character queue flush (`CharacterPersistenceBarrier`),
then the **epoch takeover** (one transaction: the ownership row moves from A at epoch N to B at N + 1). Any failure
before the takeover resumes A and then journals the abort (a failed journal write leaves A running and the handoff open
for `RecoverAsync`); after it the handoff only rolls forward (`RecoverAsync` finishes a crashed
one from the journal): promote B, reroute, retire A. The gateway reroute (`GatewayWorldHandoffRouter`,
`GatewayServer.RetargetWorkerAsync`) moves the gateway's single worker link to B: **clients on A are disconnected and log
in again** (no `SMSG_TRANSFER_PENDING` / `SMSG_NEW_WORLD` carry-over, no input buffering during the freeze). A retired A
stays frozen; when its process stops, every write it still attempts is refused by the fence. The participants are not
registered in DI and there is no cross-process control channel: one coordinator can drive them only when A, B and the
gateway share a process (the tests). Known limits, including B crashing after the takeover, are listed in design
section 5.6.

Gateway side: `gateway.json` next to the executable, then environment variables and the command line (generic host
order). The gateway also reads the `Database:Auth` section (the realm's auth database), `Net:Protection` and `Logging`.
The build copies the World's `appsettings.json` into the gateway's output directory as a side effect of the project
reference; when the gateway is started from that directory it supplies the same `Database:Auth` defaults the World uses.
The `Gateway` section:

| Key (under `Gateway`) | Default | Meaning |
|---|---|---|
| `BindAddress` | `0.0.0.0` | Address the client listener binds |
| `Port` | `8086` | Client port (the realm list must name it) |
| `MaxConnections` | `0` | Most simultaneous client connections; 0 means no cap of the gateway's own |
| `MaxConnectionsPerIp` | `0` | Most simultaneous connections from one address; 0 means no cap of the gateway's own |
| `PreAuthTimeout` | `00:00:10` | A connection without a valid `CMSG_AUTH_SESSION` by then is closed |
| `MaxOutboundBytes` | `8388608` | A client with more unsent bytes than this is disconnected |
| `WorkerAddress` / `WorkerPort` | `127.0.0.1` / `8095` | The worker's `Cluster:Worker` listener |
| `LinkSecret` | empty | Must equal the worker's link secret; at least 16 characters, or the gateway refuses to start |
| `LinkRetryDelay` | `00:00:01` | First wait before reconnecting the link; doubles up to 30 s |
| `NodeId` | `gateway` | Node id in the link manifest |
| `RealmId`, `ContentRevision`, `CharactersSchemaVersion`, `WorldSchemaVersion` | `1`, `dev`, `1`, `1` | Must equal the worker's values or the link is refused |

These keys are **not** in the generated configuration catalog: the catalog covers the World and Realm daemons
(`ConfigDaemon` has no Gateway value), and there is no `gateway.conf.dist` or `--check-config` for the gateway yet. The
source-binding coverage test skips `src/ArcaneCore.Gateway/` by a named row in `ConfigExceptionTable.UncataloguedSourceRoots`.

## Running under the host supervisor

The gateway, the worker and a standby worker can be launched and watched by the host supervisor instead of by hand: start `ArcaneCore.World` with `Cluster:Supervisor:Enabled` true and list each child under `Cluster:Supervisor:Processes` (keys without `:`, for example `worker-EK`), each with its own configuration and its own `Cluster:Worker:Enabled`. The supervisor process builds no world and opens no database or listener; it refuses to start with fewer than `Cluster:Supervisor:MinClusterProcesses` admissible processes, does not restart a child that exits 78, and a child cannot itself run as a supervisor. See the M15.4 section of [CLUSTERING_DESIGN.md](../CLUSTERING_DESIGN.md).

## Where things run

| Concern | Runs on | Note |
|---|---|---|
| Header cipher, `SMSG_AUTH_CHALLENGE`, auth digest | Gateway | The worker holds no cipher state; it reads the session key from the auth database only to start Warden |
| `PING` / `PONG` | Gateway answers; the ping is also forwarded | The worker records the latency sample (`WorldSession.LatencyMs`) and sends no pong |
| Per-IP guards, IP bans | Both | Gateway: `NetGuard` admission and the ban check at auth. Worker: `WorldSession.RemoteEndpoint` is the client's `ip:port`, so IP bans and logs see the client address, not the gateway's |
| Opcode rate limits, all game handlers | Worker | Unchanged `DispatchAsync` path |
| Warden | Worker | Its opcodes are forwarded unchanged; the worker starts Warden with the session key from the auth database. The design (section 5.2) puts Warden on the gateway; moving it is needed before 3b |
| Account claim | Both | Gateway: one connection per account. Worker: the normal `SessionRegistry` claim |

## Node role and realm authority leases

`Cluster:Node:Role` says what this World process is. It is `Combined` by default: every feature runs here, as it always has, and nothing below is
registered. The other value a World process can host is `MapWorker`; it needs `Cluster:Worker:Enabled` = true, because a map worker is the world a
gateway links to. `Social`, `Economy` and `Gateway` are refused at start (exit 78), each with the missing piece named: auctions, mail and escrow live in
the World process's memory (`EconomyFeature`), and AutoBroadcast's all-player broadcast only reaches the sessions of its own process, so a node with only
that role would have nothing to serve.

Two features are realm-wide singletons: the auction house bot (the `economy` scope) and AutoBroadcast (the `social` scope). On a `MapWorker` each one
attaches only while this node holds the matching lease, and a node attaches nothing it cannot lease; the other node (or this one, after the holder's lease
lapses and it restarts) runs it. Warden stays attached on a map worker, because the gateway passes the client stream through to it.

| Setting | Default | Meaning |
|---|---|---|
| `Cluster:Node:Role` | `Combined` | `Combined` or `MapWorker` |
| `Cluster:Node:AuthorityLeaseTtlSeconds` | 30 | A lease lapses this long after its last renewal (5 to 600) |
| `Cluster:Node:AuthorityLeaseRenewSeconds` | 10 | Renewal interval (1 to a third of the TTL) |

The owner id of a node's leases is its `Cluster:Worker:NodeId`. The leases are rows of `realm_authority_lease` in the characters database (characters
schema 67), one per realm, next to `character_ownership`; the table is created empty by the schema upgrade on every world, and a `Combined` node never
reads or writes it. Each grant raises the scope's fencing token, which never goes down, even across a release.

- **Start.** Before it attaches features, the node takes each free lease. A lease held by another node is left alone and its singleton does not run
  here. A lease still held under this node's own id (a crash that did not release) is waited out up to the TTL, then taken with a higher token.
- **Renewal.** Every `AuthorityLeaseRenewSeconds`. The singletons cannot be detached from a running world, so a lost lease is fail-stop: the node logs it
  at critical level, records a `cluster.lease` fault, and stops with exit code 1. A database error counts as a loss once the next attempt would fall in the
  last half renewal interval before the lease lapses.
- **Stop.** After the features stop, the node releases the leases it holds; one that cannot be released lapses by itself.
- **Commands.** `.cluster status` shows `nodeRole=` for a node that is not `Combined`; `.cluster leases` shows the durable holder of each scope; `.ahbot`
  says so when the bot does not run on this node.

Mutual exclusion and its margin. A lease is valid until the time its holder last renewed it plus the TTL, and another node may take it the moment it
lapses. The holder stops itself when the next tick would fall in the last half renewal interval before the lapse, so with the defaults (TTL 30 s, renewal
10 s) a node whose renewals fail, or whose store call hangs, decides at 20 s after its last good renewal: **10 s before the lease lapses**. Every store
command is bounded to a third of the renewal interval (3 s by default), so a failed call also reports within that bound; count the margin as 7 to 10 s.
The singletons are stopped only if the whole shutdown (features stopped, saves drained) completes inside that margin; a shutdown that takes longer leaves
a window in which the old and the new holder both run the auction bot or AutoBroadcast. A renewal the store refuses (the lease was already taken over)
leaves no margin at all. Tune `Cluster:Node:AuthorityLeaseTtlSeconds` up if the shutdown of a busy node can take longer than 7 s.

Lease expiry is judged against each node's wall clock (a node passes its own time to the store), so the nodes need clocks synchronised by NTP (or chrony).
The skew a pair of nodes can tolerate is the margin minus the shutdown time: with the defaults and a shutdown of up to 5 s, a node that is ahead of
the holder by more than about 5 s can take the lease while the holder is still stopping. Keep skew under 1 s (ordinary NTP does) and treat a node whose
clock is not synchronised as unfit to run a map worker.

Open gap: storage writes are not checked against the fencing token at commit, so a partitioned former holder is bounded by its own fail-stop, not by the
database. A dedicated `Social` or `Economy` process is not built.

## Tests

`tests/ArcaneCore.Gateway.Tests` (44 tests, run with `--filter`):

- `GatewaySessionWireTests`: every frame round-trips; every truncation and an out-of-range length throw `InvalidDataException`; the HMAC proof.
- `GatewaySessionLinkTests`: over loopback TCP, packets keep their order and nothing overtakes the open reply; replays, a
  wrong account, a wrong epoch and an unknown generation are dropped and counted on the worker, and a server-side replay or
  foreign epoch is dropped on the gateway; a stale-generation open and an acceptor refusal are refused with the code; closes
  in both directions and link loss; a wrong secret or manifest is refused; a non-loopback bind without a secret will not start.
- `GatewayEndToEndTests`: the mock client logs in, creates a character, enters the world and pings **through the gateway**
  against the synthetic realm and world; the worker session sees the client's address; a wrong key is refused before the
  worker is involved; a second login supersedes the first; no link answers `AUTH_UNAVAILABLE`; worker mode registers
  nothing unless enabled.

Tier 3b groundwork: `StandbyWorkerTests` (8: standby registration, standby without worker mode refused, a closed
worker refuses opens, a standby owns and admits nothing, refused and retried promotion, a refused promotion of a
released ownership claims nothing, a promoted standby takes a session and writes while the old world is fenced) and
`WorldHandoffTests` (10: a full handoff with a player in the world through the gateway and the re-login landing on B;
an abort injected after each of the five pre-commit stages; a source that lost its ownership; a failed abort journal
write that still resumes A; a crash after the takeover rolled forward by recovery; a crash before it aborted by
recovery).

## Not done yet (rest of 3a and beyond)

- **Real-client acceptance** (the 3a exit criterion in the design) has not been run; everything above is mock-client only.
- Warden runs on the worker, not the gateway.
- The movement anticheat's receive time is stamped on the worker, so link jitter is added to the times it compares.
- The manifest's schema and content versions are operator labels, not read from the databases.
- The gateway has no conf catalog entry, `.conf.dist`, `--check-config` or metrics; its link counters are only in-process.
- Phase 3b's handoff needs a same-map `SMSG_NEW_WORLD` the client accepts; the spike (`.worldport`, [code hot reload](code-hot-reload.md#worldport-tier-3-spike-administrator-only-when-enabled)) showed on a real 1.12.1 client that it does (design section 5.5); the reroute itself is not built.
- A gateway or World restart still drops every client. The 3b handoff above moves the ownership and the players' saved
  state but still disconnects the clients (they log in again); a seamless reroute (one link per worker on the gateway,
  per-session routing, synthesized `SMSG_TRANSFER_PENDING` / `SMSG_NEW_WORLD`, input buffered during the freeze) and an
  authenticated operator trigger are not built.
- The link protocol lives in `ArcaneCore.World`, so the gateway references the whole World assembly; a shared protocol
  assembly is still to be split out.
- **Cluster roles beyond combined and worker are not hosted.** The World daemon runs either combined or as a gateway worker
  (Cluster:Worker). No daemon runs a social, economy or coordinator role, and nothing reads a node role. So the M15.2 pieces
  that only matter in that topology are tested library code, not live:
  - `ClusterRoleFeatureFilter` (keeps realm-singleton timers such as AutoBroadcast and the auction bot on one node) is never
    constructed. Every feature attaches on every World process, which is correct for the single combined or worker process
    that exists today.
  - `IRealmAuthorityLeaseStore` has only an in-memory implementation and no DI registration. The `.cluster leases` summary
    therefore always shows the fallback owner (combined, or the character ownership epoch), never a realm lease token.
- **The M15.4 host supervisor is hosted as an opt-in mode, with gaps.** `Cluster:Supervisor:Enabled` runs `ArcaneCore.World` as the
  supervisor only (see "Running under the host supervisor" above and the
  [design](../CLUSTERING_DESIGN.md#host-supervisor-and-console-layout-m154)); the `Cluster:Supervisor` keys are in the conf
  catalog and `MinClusterProcesses` is enforced when the supervisor starts. Not built: the coordinator role, restart
  reconciliation (the owned-pid set is in memory and does not survive a supervisor restart, so children of a dead supervisor
  are not found again), and placement of new or empty maps onto workers. A second drain of an entry that ended Faulted (a kill the
  OS refused) marks it Drained again; that is not yet reconciled.
