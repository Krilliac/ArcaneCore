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
is refused. A frame that does not decode closes the link, and every session on it ends (`LinkLost`).

### Link admission

The worker sends a 32-byte random challenge; the gateway answers with HMAC-SHA256 of it keyed by the shared link secret,
then registers a `Gateway`-role manifest (realm id, contract version, content revision, schema versions). The worker
checks the proof, the role and the manifest before it accepts any session. A non-loopback `Cluster:Worker:BindAddress`
refuses to start without `Cluster:Worker:LinkSecret`. **An admitted gateway can open a session for any account it
authenticated, so the link secret is as sensitive as a database password.** The link itself is not encrypted; keep it on
loopback or a private network.

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
| `LinkSecret` | empty | Must equal the worker's link secret |
| `LinkRetryDelay` | `00:00:01` | First wait before reconnecting the link; doubles up to 30 s |
| `NodeId` | `gateway` | Node id in the link manifest |
| `RealmId`, `ContentRevision`, `CharactersSchemaVersion`, `WorldSchemaVersion` | `1`, `dev`, `1`, `1` | Must equal the worker's values or the link is refused |

These keys are **not** in the generated configuration catalog: the catalog covers the World and Realm daemons
(`ConfigDaemon` has no Gateway value), and there is no `gateway.conf.dist` or `--check-config` for the gateway yet. The
source-binding coverage test skips `src/ArcaneCore.Gateway/` by a named row in `ConfigExceptionTable.UncataloguedSourceRoots`.

## Where things run

| Concern | Runs on | Note |
|---|---|---|
| Header cipher, `SMSG_AUTH_CHALLENGE`, auth digest | Gateway | The worker holds no cipher state; it reads the session key from the auth database only to start Warden |
| `PING` / `PONG` | Gateway answers; the ping is also forwarded | The worker records the latency sample (`WorldSession.LatencyMs`) and sends no pong |
| Per-IP guards, IP bans | Both | Gateway: `NetGuard` admission and the ban check at auth. Worker: `WorldSession.RemoteEndpoint` is the client's `ip:port`, so IP bans and logs see the client address, not the gateway's |
| Opcode rate limits, all game handlers | Worker | Unchanged `DispatchAsync` path |
| Warden | Worker | Its opcodes are forwarded unchanged; the worker starts Warden with the session key from the auth database. The design (section 5.2) puts Warden on the gateway; moving it is needed before 3b |
| Account claim | Both | Gateway: one connection per account. Worker: the normal `SessionRegistry` claim |

## Tests

`tests/ArcaneCore.Gateway.Tests` (22 tests, run with `--filter`):

- `GatewaySessionWireTests`: every frame round-trips; every truncation and an out-of-range length throw `InvalidDataException`; the HMAC proof.
- `GatewaySessionLinkTests`: over loopback TCP, packets keep their order and nothing overtakes the open reply; replays, a
  wrong account, a wrong epoch and an unknown generation are dropped and counted on the worker, and a server-side replay or
  foreign epoch is dropped on the gateway; a stale-generation open and an acceptor refusal are refused with the code; closes
  in both directions and link loss; a wrong secret or manifest is refused; a non-loopback bind without a secret will not start.
- `GatewayEndToEndTests`: the mock client logs in, creates a character, enters the world and pings **through the gateway**
  against the synthetic realm and world; the worker session sees the client's address; a wrong key is refused before the
  worker is involved; a second login supersedes the first; no link answers `AUTH_UNAVAILABLE`; worker mode registers
  nothing unless enabled.

## Not done yet (rest of 3a and beyond)

- **Real-client acceptance** (the 3a exit criterion in the design) has not been run; everything above is mock-client only.
- Warden runs on the worker, not the gateway.
- The movement anticheat's receive time is stamped on the worker, so link jitter is added to the times it compares.
- The manifest's schema and content versions are operator labels, not read from the databases.
- The gateway has no conf catalog entry, `.conf.dist`, `--check-config` or metrics; its link counters are only in-process.
- A gateway or World restart still drops every client (handoff is phase 3b).
- The link protocol lives in `ArcaneCore.World`, so the gateway references the whole World assembly; a shared protocol
  assembly is still to be split out.
