# Gateway passthrough (Tier 3a)

Status: **built, mock-client tested, not yet accepted with a real 1.12.1 client** (checklist: [CLUSTER_ACCEPTANCE.md](../CLUSTER_ACCEPTANCE.md)). This is phase 3a of
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

The worker sends a 32-byte random challenge; the gateway answers with HMAC-SHA256 of it keyed by the shared link secret
plus its own 32-byte random nonce. The worker checks the proof and answers with its own proof (HMAC-SHA256 under the secret
over a fixed label and both nonces), so the gateway knows the worker holds the secret and that the answer is fresh for this
connection. Only then does the gateway register a `Gateway`-role manifest (realm id, contract version, content revision,
schema versions). The worker checks the proof, the role and the manifest before it accepts any session. A wrong or missing
proof is answered with a plain refusal and the link closes. A proof recorded on one connection is useless on another: the
challenge differs per connection. Worker mode refuses to start unless
`Cluster:Worker:LinkSecret` is at least 16 characters, **on every bind address, loopback included**: with an empty secret
the proof is an HMAC any local process can compute, and the manifest values are public defaults. The gateway likewise
refuses to start unless its own `LinkSecret` (the `Gateway` section below) is at least 16 characters. **An admitted gateway can open a session for any
account it names (the worker checks the account's status and bans, not a session-key proof), so the link secret is as
sensitive as a database password.**

### Frame integrity

After the two proofs, both directions switch to MAC-protected frames. Each end derives two 32-byte keys (one per direction)
with HKDF-SHA256 from the secret, salted with both nonces, and every later frame carries a 16-byte tag: HMAC-SHA256 under the
direction's key over an 8-byte frame counter and the frame. A flipped bit, a replayed, dropped or reordered frame, or a frame
reflected into the other direction fails the check and the receiver closes the link (every session on it ends, `LinkLost`).
The gateway contract version is 5 (3 added the link integrity described here; 4 added the worker status frame; 5 added the unified control frames 28-32) and the peer link protocol version is 2, so a gateway or peer from before this change is
refused at the handshake instead of misreading frames. The golden vectors are pinned in `LinkIntegrityTests`. The peer link
(`PeerLinkHost`, `PeerLinkClient`) uses the same construction with its own secret.

Until this change the link assumed a loopback-only or fully trusted network: the gateway answered any challenge and frames
carried no MAC.

What the link does not protect:

- **The link is not encrypted.** Frames are authenticated, not hidden: anyone on the path can read the client opcodes (they
  are plaintext after the header cipher is removed at the gateway) but cannot inject, alter, replay or reorder frames without
  the secret. Keep the link on loopback or a private network you control.
- **A holder of the secret is trusted completely** (see above), and a stolen secret defeats both proofs.

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

Realm liveness in this topology belongs to the gateway, not to a world. A combined-mode world keeps its realm-list row current: offline
while starting, online while its loop ticks, offline at stop, with a heartbeat the logon daemon judges (`World:Realm:Id`,
[installation](../guide/installation.md) section 4, [realm list liveness](../ops/realm-liveness.md), vmangos Master.cpp:215/:330). A worker
does not write the row (an active and a standby worker serve the same realm, so the old active world going offline after a handoff would hide a realm
the promoted standby is serving). Instead each worker sends its tick counter, session count and countdown state on the link (a `WorkerStatus` frame), and
the gateway writes the row of `Gateway:RealmId`: Online while an active worker ticks, Restarting during a restart countdown, Starting until a worker is
ready, Offline when no active worker has ticked for 60 s, and the summed population; a standby's ticks do not count
([realm list liveness](../ops/realm-liveness.md) section 7, with the failover behaviour). `Gateway:RealmId` must therefore be the realm's `realmlist` row id.

Worker mode makes character fencing **mandatory** (Tier 3b groundwork): with `Cluster:Worker:Enabled = true` the
characters write fence is registered whatever `Cluster:Fencing:Enabled` says, and the World refuses to start unless
`Cluster:Fencing:Enabled = true` too. Combined mode (worker off) is unchanged.

### Standby worker (Tier 3b groundwork)

`Cluster:Worker:Standby = true` (needs `Enabled`) starts a second World on the same databases as a handoff target. It
builds its services, validates the schema without DDL (`Database:Upgrade:ValidateOnly`), takes no characters writer lock at start (the active world holds it),
claims no ownership, registers no `World:Port` listener, and links to a gateway; but it attaches no feature and starts no
simulation (features read the characters database, which the active World still owns: the fence refuses a non-owner's
transactions, reads included), and it refuses every session open with `AUTH_UNAVAILABLE` (`WorkerAdmission`). It runs
only after `WorldHost.PromoteAsync(token)`: the token must be the ownership the database holds, under this process's
incarnation, as the handoff's epoch takeover leaves it. Promotion first takes the characters database writer lock the standby deferred (the SQLite sidecar `<database>.world.lock`, or the MariaDB/MySQL/PostgreSQL session advisory lock of [operating a realm](../guide/operations.md)), before it reads the ownership row: while the old world is alive it still holds that lock, so the promotion is refused with a message saying so, nothing is held and the standby stays a standby (a later retry may succeed once the old world has released it or exited). The in-process handoff coordinator makes the source world give up its lock (`IWorldHandoffSource.ReleaseWriterLockAsync`) after the epoch takeover and before it promotes B (the window until B takes the lock is guarded by the ownership row, which already names B's incarnation, and by the fence on A's late writes, not by the lock); a source that is a separate process must have exited or released its lock the same way. Promotion only reads the ownership row
(`ICharacterOwnershipStore.GetCurrentAsync`); a refused promotion claims nothing, even when the row is released. There is no operator command for it yet; the drain protocol
below drives it in process.

A clustered realm needs a server database: with the characters database on SQLite, `Cluster:Worker:Enabled` (standby included), `Cluster:Node:Role` = MapWorker, or a supervisor listing a gateway-worker child or more than one world-type child is a configuration error (exit 78, even without `Startup:Strict`), because SQLite allows one writer process and the clusters world processes write it concurrently. Use PostgreSQL or MariaDB for `Database:Characters` (moving a SQLite realm: [database upgrade](../ops/database-upgrade.md)); `tools/ops/live/start-cluster.ps1 -Gateway` makes the world a gateway worker, so it needs a server database too. A supervisor running exactly one combined world child on SQLite is allowed and warns that the realm cannot grow beyond one world.

### Split topology: concurrent map workers

`tools/ops/live/start-cluster.ps1 -Topology Split` starts the realm, three map-worker worlds (EK, Kalimdor, instances) and the gateway; a real (non `-Plan`) start is allowed on PostgreSQL only (the secret file `pg-cutover.ps1` reads; a real start refuses without it). Every world runs `Cluster:Fencing:Scope=Worker` with `Cluster:Node:Role=MapWorker`: it takes its own shard advisory lock and claims its own `worker:<shard>` ownership row instead of the realm-wide `world` lock, so the three worlds write the one characters database concurrently, with durable id blocks and per-character claims (`CharacterClaimService`) keeping their rows apart. The world named by `-LeaseHolder` starts first and runs the schema DDL; the others get `Database:Upgrade:ValidateOnly=true`. The supervisor does not launch a start-order group before the previous one has sent its first `alive` heartbeat (`Cluster:Supervisor:StartGroupReadySeconds`, which the script sets to 600 for the split topology; default 0 means no wait), so the holder's DDL is finished before a validate-only world checks the schema; a child that exits first, or one still silent after the limit, only logs a warning and the start goes on (a validate-only world that then finds the schema behind exits and is restarted by the supervisor). The supervisor's config check refuses two worker children whose shard ids differ only in letter case. A character whose last live login ticket is abandoned (a duplicate login refused in EnterWorld) while nobody is online has its claim released again, so logins on the other workers are not refused as live-owned. `arcane-db copy` of the characters database refuses while any ownership row is active, any worker shard lock is held, or an ownership row names a worker scope whose shard id is not valid (it cannot be probed); the preflight is read-only and never creates a SQLite shard lock file. Design: `_wf/writer-lock-DESIGN.md`; the two-worker end-to-end scenarios are `TwoWorkerScenarioTests` (they need `ARCANECORE_TEST_POSTGRES`). The split start also wires the worker-to-worker peer link: every world (and standby) gets its own `Cluster:Peer:Port` (`-PeerPort` + index, default 8105), the whole node map as `Cluster:Peer:Endpoints:<n>:NodeId` / `Address` (the list form of `Cluster:Peer:Nodes`, needed because node ids contain `:`), its own `World:RemoteAdmin:Port` (`-RemoteAdminPort` + index, default 3443), and the one link secret is also set as `ARCANECORE_PEER_LINK_SECRET`.



**Limits of the group wait.** If the schema-holder world crashes before its first alive heartbeat, the wait for that group ends (the child exited), and the next group may start while the relaunched holder is still running DDL. The validate-only worlds then fail and are restarted, and those restarts count toward `Cluster:Supervisor:CrashLoopMaxRestarts`, so a slow DDL combined with one holder crash can still push a validator into the crash loop. Also, `StartAsync` of the supervisor hosted service blocks up to `Cluster:Supervisor:StartGroupReadySeconds` per group (600 s with `-Topology Split`, four group boundaries, so up to 40 min when children stay silent); services registered after it wait that long, and Ctrl+C cancels the wait.


### Login redirect to the map owner

A session opens on the login worker (or, while that worker's link is down, on any worker with a ready link). When the client logs a character in, the worker checks whether it owns the character's map copy; if it does not, it answers with a session redirect instead of entering the world. The gateway then re-opens the session on the worker that owns the map, hands it the buffered `CMSG_PLAYER_LOGIN`, closes the session on the first worker, and replays the client packets it held in the meantime. A redirect can happen once per connection.

- **Owner known but its link is down.** A starting gateway registers its links one by one, and a worker can be restarting, so the map's owner may have no registered link when the redirect arrives. When the route table still lists a configured owner for the map (the static map list of that worker), the gateway holds the login for up to 30 s instead of closing the client, and logs "holding the login for up to 30 s". For as long as any login waits, that worker's link supervisor dials it every 500 ms plus the time a failed dial takes (about 2 s on Windows, where a refused loopback connect is retried; immediate on Linux) instead of following its doubling reconnect backoff (`LinkRetryDelay`, up to 30 s). The hold does not reset that backoff: it keeps doubling through the hold's dials, so after a long hold on a dead worker it is at its 30 s maximum. On the owner's registration the login moves there. After 30 s, or when the map resolves back to the worker that sent the redirect, the client is disconnected and the log names the reason.
- **Packet cap.** While the login is held the client's packets are buffered, up to 256; a client that sends more is disconnected. The gateway still answers `CMSG_PING` itself.
- **Unowned map.** A map that no configured worker owns is refused at once (the client is disconnected immediately, as before redirects existed).
- **Handoffs.** A redirect that arrives while a whole-World handoff holds the session's packets is refused, and the refusal disconnects the client (the login worker's session is closed as a worker close, which kicks the connection). A redirect whose session moved to another link, or ended, while it waited drops the redirected session and also disconnects the client.

Tests: `LoginRedirectTests` (in process, including an owner that comes up 20 s into the hold) and `TwoWorkerScenarioTests` (a late-started worker, multi-process).

### Transfer to a map whose owner has no link

A far teleport routes the same way a login redirect does, and a worker whose link is down owns nothing for routing, whether it is dead or only between two dials of its reconnect backoff. So when the target map has a configured owner with no registered link, the gateway waits up to 10 s for that link before it answers the transfer request, and logs "waits up to 10 s for the link of its owner". The owner is dialled every 500 ms during the wait, as for a held login, so a worker that is up is reached at once. The player stays in the world and nothing is journalled while it waits. However the wait ends, the map is routed again: if it now has an owner the transfer starts, and if not the teleport is refused with `NoEligibleWorker` (`map-unassigned`) and the player sees "The teleport could not be completed." The teleport is also refused if the session ends or the gateway begins stopping during the wait. A map that no configured worker owns is refused at once. A refusal does not undo what the source worker did when it sent the request: a ghost entering its corpse's map stays revived, and a player taken off a transport stays off it. The answer goes to the source worker's current link, so a link that reconnected during the wait still ends the worker's in-flight entry; if the link is down when the answer is ready, the answer is lost and that entry blocks the character's far teleports until it expires (5 minutes).

Tests: `GatewayCrossWorkerTeleportTests` (in process, the target worker up during a 30 s backoff), `GatewayTransferServiceTests` (the outcomes of the wait) and `ClusterControlChannelScenarioTests` (a late-started worker, multi-process).

### Whole-World handoff (Tier 3b groundwork, in process only)

`WorldHandoffCoordinator` (`src/ArcaneCore.World/Cluster/Handoff/`) runs the drain protocol of design section 5.3 over
three participants and journals each stage in the characters database (`cluster_world_handoff`, schema 62):
B warm, A stops admissions, A freezes at a tick boundary (`LocalWorldHandoffSource` parks the simulation thread after
capturing every online player's save), the saves and every per-character queue flush (`CharacterPersistenceBarrier`),
then the **epoch takeover** (one transaction: the ownership row moves from A at epoch N to B at N + 1). Any failure
before the takeover resumes A and then journals the abort (a failed journal write leaves A running and the handoff open
for `RecoverAsync`); after it the handoff only rolls forward (`RecoverAsync` finishes a crashed
one from the journal): promote B, reroute, retire A. The gateway side (`GatewayWorldHandoffRouter`) is seamless:
when A freezes, the gateway holds every client's input (`GatewayServer.HoldSessionsForHandoff`, at most 256 packets per
client, one past that is disconnected; an abort replays it to A). After B is promoted, `GatewayServer.RetargetWorkerSeamlessAsync`
connects the gateway's worker link to B while the link to A is still open and re-points every session: B adopts the character
with the client's buffered `CMSG_PLAYER_LOGIN` (it loads the save A captured at the freeze), B's `SMSG_LOGIN_VERIFY_WORLD`
reaches the client as `SMSG_TRANSFER_PENDING` + `SMSG_NEW_WORLD` (same map and position as the save: the same-map world port
the real client accepts, design section 5.5), the held input is delivered to B in order, and the link to A closes. A client at
character select just gets a plain session on B. A client whose move fails (B refuses, 30 s timeout) is disconnected and logs
in again. A **gateway restart still drops every client**: the sessions and their ciphers live in the gateway process. The loss
contract of section 5.3 applies (loading screen; cast, combat, trade, loot and similar transients are cancelled). A retired A
stays frozen; when its process stops, every write it still attempts is refused by the fence. The participants are not
registered in DI unless the opt-in control channel is on (see [Handoff and transfers over the control channel](#handoff-and-transfers-over-the-control-channel-opt-in));
without it one coordinator can drive them only when A, B and the gateway share a process (the tests). Known limits, including B crashing after the takeover,
are listed in design section 5.6.

Gateway side, lowest precedence first: `appsettings.json`, `gateway.conf` (or `--config <file>`, the same format as `world.conf`),
`gateway.json` next to the executable, environment variables, the command line. The gateway also reads the `Database:Auth` section (the realm's auth database), `Net:Protection` and `Logging`.
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
| `Workers` | empty | One entry per world worker: `NodeId`, `Address`, `Port`, optional `Maps` (map ids it owns, informational). The gateway keeps one link per entry, each with its own reconnect backoff and state; a new login goes to the ready link with the fewest sessions, and a worker that goes away drops only its own sessions |
| `WorkerAddress` / `WorkerPort` | `127.0.0.1` / `8095` | The single-worker form, used only while `Workers` is empty (one entry named `worker`): the worker's `Cluster:Worker` listener |
| `LinkSecret` | empty | Must equal the worker's link secret; at least 16 characters, or the gateway refuses to start |
| `LinkRetryDelay` | `00:00:01` | First wait before reconnecting the link; doubles up to 30 s |
| `NodeId` | `gateway` | Node id in the link manifest |
| `RealmId`, `ContentRevision`, `CharactersSchemaVersion`, `WorldSchemaVersion` | `1`, `world-schema-N`, the build's characters schema version, the build's world schema version (N is the world version) | Must equal the worker's values or the link is refused. A worker reads the real versions from its databases by default (`Cluster:Worker:VersionsFromDatabase`, `true`); `false` makes it advertise its `Cluster:Worker` `ContentRevision`/`CharactersSchemaVersion`/`WorldSchemaVersion` labels instead (explicit override, same defaults as the gateway's), and a worker whose database is behind the gateway's build is refused |

These keys are in the generated configuration catalog (`ConfigDaemon.Gateway`, the `Gateway` section of the
[configuration reference](../reference/configuration.md)), together with the shared sections the gateway also reads (`Database`,
`Net:Protection`, `Logging`, `Diagnostics`). `src/ArcaneCore.Gateway/gateway.conf.dist` is generated from the catalog like
the world and realm files (`ARCANECORE_UPDATE_DOCS=1 dotnet test tests/ArcaneCore.World.Tests --filter Docs`).

### Command line

| Command | What it does |
|---|---|
| `ArcaneCore.Gateway --check-config` (or `check-config`) | Loads the whole configuration, validates `Gateway:*` (`GatewayOptions.Validate`) and the link secret rule, prints every problem and exits: 0 valid, 78 invalid. Nothing is started and no database is touched. The `.conf` key check warns about misspelled keys |
| `ArcaneCore.Gateway write-config (--defaults\|--current) <file\|->` | Writes the documented defaults or the effective configuration |
| `ArcaneCore.Gateway --help` | Usage (exit 64); a mistyped or world-only option is a usage error (64), not silently dropped |

The same validation runs at every start (exit 78 when invalid).

### Metrics

The gateway records `System.Diagnostics.Metrics` instruments on the meter `ArcaneCore.Gateway` (collected by a `MetricsStore` when a host
registers one; with no listener each is a single enabled check). Every series carries `node` (`Gateway:NodeId`):
`arcanecore.gateway.link_state` (links up), `arcanecore.gateway.worker_link_state` (1 up, 0 down; extra tags `worker_node`, `worker`), `arcanecore.gateway.sessions` (sessions open across the links),
`arcanecore.gateway.connections` (client connections), `arcanecore.gateway.link_reconnects`, `arcanecore.gateway.link_failures`,
`arcanecore.gateway.forwarded_packets` and `arcanecore.gateway.forwarded_bytes` (tag `direction`: `to_worker`, `to_client`),
`arcanecore.gateway.session_opens` (tag `result`: `accepted`, `refused`, `no_link`) and `arcanecore.gateway.link_latency`
(histogram, milliseconds: the session-open round trip over the link, tag `worker`). Client packets and bytes stay on
`arcanecore.net.packets_in`, `arcanecore.net.packets_out`, `arcanecore.net.bytes_in` and `arcanecore.net.bytes_out`.
The gateway does not start a metrics exporter yet (see below).

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
`WorldHandoffTests` (11: a full handoff with a player in the world through the gateway, B answering the input held during the freeze;
an abort injected after each of the five pre-commit stages; a source that lost its ownership; a failed abort journal
write that still resumes A; a crash after the takeover rolled forward by recovery; a crash before it aborted by
recovery).

## Handoff and transfers over the control channel (opt-in)

By default the whole-World handoff and the player transfer run only in process (the tests). Four keys, all `false` by default, host them across
processes with the gateway as the one coordinator (design `_wf/handoff-control-DESIGN.md`; the tier view is
[hot-reload-tiers.md](../design/hot-reload-tiers.md) section 5.6):

| Key | Where | Effect when `true` |
|---|---|---|
| `Cluster:Worker:Handoff:Enabled` | worker (and standby) | The worker answers the handoff verbs of an admitted gateway (`Describe`, `Warm`, `StopAdmissions`, `Freeze`, `Flush`, `Resume`, `ReleaseWriterLock`, `Retire`, `Promote`); needs `Cluster:Worker:Enabled`. Off, it answers `Disabled` |
| `Cluster:Worker:Transfers:Enabled` | worker (and standby) | The worker builds its production transfer source and target and answers the transfer verbs; a teleport to a map another worker owns becomes a transfer request. Off, it answers `Disabled` and the refusal of that teleport is unchanged |
| `Gateway:Handoff:Enabled` | gateway | The gateway connects to the characters database (validate-only, nothing is created or upgraded), keeps a control link to each slot's standby, coordinates handoffs and operator restarts and recovers interrupted ones from the journal |
| `Gateway:Transfers:Enabled` | gateway | The gateway coordinates cross-worker transfers and finishes interrupted ones from the journal |

Each slot's standby is named on its worker entry: `Gateway:Workers:N:StandbyNodeId` (the standby's `Cluster:Worker:NodeId`, which must differ from
every other node id), `StandbyAddress` and `StandbyPort` (its `Cluster:Worker:BindAddress` and `Cluster:Worker:Port`). The reply timeouts are
`Gateway:Control:*` and the recovery cadence `Gateway:Handoff:*`. One gateway per realm is the contract: a worker reports how many gateways it has
admitted and the coordinator refuses to begin when it is more than one. The gateway and every worker must run the same link contract version.

Under the supervisor, a retired source is relaunched as the slot's next standby, addressed by child name, so the supervisor child of a world must be
named by its node id. `start-cluster.ps1` does this itself:

```
pwsh -NoProfile -File tools/ops/live/start-cluster.ps1 -Topology Split -Standby -Transfers
```

`-Standby` (Split only) adds one standby per world (`standby-EK`, `standby-Kalimdor`, `standby-instances`; node ids `standby:EK` and so on; their own
link and client ports after the three worlds'), starts it with `Cluster:Worker:Standby=true`, `Cluster:Fencing:Shard` set to the shard of the world it
stands behind (so its promotion takes over that world's durable scope) and its own `Cluster:Fencing:OwnerNode` and node id, names the world children
by node id, and sets the handoff keys on the worlds and the gateway. `-Transfers` sets the transfer keys on the worlds, the standbys and the gateway.
`-Plan` prints all of it. Without either switch the launch is exactly the plain Split launch. The gateway needs the characters database; it inherits
the PostgreSQL environment of the supervisor and only validates the schema, so a role limited to `cluster_world_handoff`, `cluster_transfer_*` and
`character_ownership` is enough.

Operator commands: `.cluster restart $worker` (Administrator) hands the slot off and asks the supervisor to relaunch the retired process as the next
standby; `.cluster handoff status [$operationId]` (Game Master) shows the unresolved handoffs of the journal or one by id. The PostgreSQL end to end
scenarios (a cross-worker teleport, `.cluster restart`) are `ClusterControlChannelScenarioTests`, skipped without `ARCANECORE_TEST_POSTGRES`.

## Cross-worker player transfer, gateway side (S5c)

`GatewayTransferRouter` is the gateway half of `ITransferGatewayRouter`. The source worker's coordinator host
(`PlayerTransferCoordinatorHost` with `LinkTransferGatewayRouter`) sends `TransferStage`, `TransferCancel` and `TransferCommit` control
frames on the worker's own gateway link (`GatewayTransferWire`; each is answered with a `TransferAck`). Stage buffers the session's client
packets (at most 256 packets and 256 KiB), opens a session on the target worker and sends a synthesized `SMSG_TRANSFER_PENDING`. Cancel
replays the buffer to the source and closes the target session. Commit replays the buffer to the target in order, re-points the connection
(link and generation) and retires the old generation, so packets still stamped with it are dropped. A buffer overflow frees the buffer, closes
the target session and disconnects the client; the later commit is then a no-op. Node ids in the frames are the `Gateway:Workers:N:NodeId` values.
Not built: the worker-side peer-link participant for the target, and the session of the target being resolvable by the shadow loader before
stage 4 (the target session opens at stage 4, after the passive shadow install).

## Not done yet (rest of 3a and beyond)

- **Real-client acceptance** (the 3a exit criterion in the design) has not been run; everything above is mock-client only. The checklist and log-capture procedure for that run, with an empty results table, is [CLUSTER_ACCEPTANCE.md](../CLUSTER_ACCEPTANCE.md).
- Warden runs on the worker, not the gateway.
- The movement anticheat's receive time is stamped on the worker, so link jitter is added to the times it compares.
- The manifest's schema and content versions are operator labels, not read from the databases.
- The gateway records its metrics but starts no Prometheus or OTLP exporter: it needs a scrape port of its own, which is not chosen yet.
- Phase 3b's handoff relies on a same-map `SMSG_NEW_WORLD` the client accepts; the spike (`.worldport`, [code hot reload](code-hot-reload.md#worldport-tier-3-spike-administrator-only-when-enabled)) showed on a real 1.12.1 client that it does (design section 5.5). The gateway's seamless reroute is built and tested in process only, not on a real client.
- The seamless reroute is untested on a real client in three respects: B's whole login burst follows the synthesized `SMSG_NEW_WORLD` without waiting for `MSG_MOVE_WORLDPORT_ACK` (B ignores that ACK when no teleport is pending, whereas the spike added the player after it); a fresh login on B has re-login side effects (message of the day, friend and guild "came online" notices); and a character aboard a ship gets the plain 4-byte `SMSG_TRANSFER_PENDING`, not the transport form a passenger teleport sends.
- A gateway or World restart still drops every client. An authenticated operator trigger for the handoff is not built.
- The link protocol (channel, session wire, frame types, contracts, manifest) now lives in `ArcaneCore.Cluster.Protocol`, but the
  gateway still references `ArcaneCore.World` for `WorldSession` constants, `AddonInfo`, the build protocols (over Game), the session
  link client and the handoff router seam; those need their own split before the reference can go.
- **Cluster roles beyond combined and worker are not hosted.** The World daemon runs either combined or as a gateway worker
  (Cluster:Worker). `ClusterNodeOptions.Validate` refuses the Social, Economy and Gateway roles on a World process, so the
  only cluster role a World process takes is MapWorker. The role pieces that exist are registered and used, but only in that
  one shape:
  - `ClusterNodeHosting.AddClusterNodeRole` registers `ClusterRoleFeatureFilter`, `RealmAuthorityLeaseKeeper` and the
    database-backed `EfRealmAuthorityLeaseStore` (as `IRealmAuthorityLeaseStore`) whenever `Cluster:Node:Role` is not
    Combined. `WorldHost` takes the leases it can get at start and attaches a feature only when the filter admits it, so
    the auction bot and AutoBroadcast run only under a lease the node holds. A MapWorker also takes the Instances lease scope; it gates only the realm-wide durable writes of the instance reset schedule (see [instances.md](instances.md)).
  - What is still missing is what those pieces serve: cross-process realm services on the link at the lease holder
    (S2/S3) and map routing (S4). Decision D3 means there will be no dedicated social or economy process. The staged plan is in the
    [design](../CLUSTERING_DESIGN.md#decisions-and-staged-plan-2026-10-30) (S0-S7).
- **The M15.4 host supervisor is hosted as an opt-in mode, with gaps.** `Cluster:Supervisor:Enabled` runs `ArcaneCore.World` as the
  supervisor only (see "Running under the host supervisor" above and the
  [design](../CLUSTERING_DESIGN.md#host-supervisor-and-console-layout-m154)); the `Cluster:Supervisor` keys are in the conf
  catalog and `MinClusterProcesses` is enforced when the supervisor starts. Not built: the coordinator role, restart
  reconciliation (the owned-pid set is in memory and does not survive a supervisor restart, so children of a dead supervisor
  are not found again), and placement of new or empty maps onto workers. A second drain of an entry that ended Faulted (a kill the
  OS refused) marks it Drained again; that is not yet reconciled.
